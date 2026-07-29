using System.Text;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Presence;
using MageRide.Shared.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.Dispatch.Mqtt;

/// <summary>
/// Holds the EMQX presence subscription and hands every <c>veh/{vehicleId}/status</c> message to
/// <see cref="IVehicleStatusService"/> (R-15).
/// </summary>
/// <remarks>
/// <para>
/// ADD §6's dispatch-svc row lists the EMQX last will among what this service consumes, in the same
/// sentence as <c>reputation-svc.block_status</c>: "Consumes <c>reputation-svc.block_status</c> and
/// EMQX LWT (<c>veh/{vehicleId}/status=offline</c>) to release stale offers (R-15) and clear active
/// directional filters (DT-04)". The first half is this component's; the DT-04 half is C036's and
/// needs a filter that cannot be set yet.
/// </para>
/// <para>
/// <b>ride-svc holds the same subscription, and both are right.</b> The two services take different
/// actions on the same fact: ride-svc arms §11.12's <c>offline_grace</c> for an *accepted* ride,
/// dispatch-svc releases an outstanding *offer*. They are mutually exclusive by state —
/// <see cref="VehicleStatusService"/> refuses to act on an ON_RIDE driver for exactly that reason —
/// so neither can undo the other.
/// </para>
/// <para>
/// The transport half here is deliberately the same shape as ride-svc's
/// <c>VehiclePresenceWorker</c>: whole-topic subscription rather than a shared one (presence is
/// rare and idempotent, so a replica taking a copy costs nothing and missing one costs a stuck
/// offer), manual acknowledgement so a failed apply is redelivered rather than dropped, and
/// exponential backoff on a lost session.
/// </para>
/// </remarks>
public sealed class VehicleStatusWorker(
    IServiceProvider services,
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    IOptions<DispatchOptions> dispatchOptions,
    ILogger<VehicleStatusWorker> logger) : BackgroundService
{
    /// <summary>Every vehicle's presence topic — <c>veh/+/status</c>.</summary>
    public const string StatusFilter = "veh/+/status";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));

    private readonly DispatchOptions _dispatch =
        dispatchOptions?.Value ?? throw new ArgumentNullException(nameof(dispatchOptions));

    private bool _subscribed;
    private long _applied;

    /// <summary>True once the subscription is live, so a test can wait rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    /// <summary>Presence messages this replica has applied. Read by the R-15 test.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                attempt++;
                logger.LogError(exception, "Presence subscription session {Attempt} ended; reconnecting", attempt);
            }
            finally
            {
                Volatile.Write(ref _subscribed, false);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(BackoffFor(attempt), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.DisconnectedAsync += args =>
        {
            logger.LogWarning(
                "Presence subscription disconnected: {Reason}", args.ReasonString ?? args.Reason.ToString());

            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += args => ApplyAsync(args, stoppingToken);

        var credential = tokens.IssueForService(_dispatch.MqttServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId($"mageride-dispatch-presence-{Guid.NewGuid():N}")
            .WithCredentials(credential.Username, credential.Jwt)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithTimeout(ConnectTimeout)
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0);

        if (_mqtt.UseTls)
        {
            options = options.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(_mqtt.Host));
        }

        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCancellation.CancelAfter(ConnectTimeout);

        var result = await client.ConnectAsync(options.Build(), connectCancellation.Token);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"EMQX refused the presence CONNECT: {result.ResultCode} ({result.ReasonString}). " +
                "Check that Mqtt:SessionTokenSecret matches EMQX_AUTHENTICATION__1__SECRET.");
        }

        var subscription = await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic(StatusFilter)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            stoppingToken);

        foreach (var item in subscription.Items)
        {
            // A refused subscription is the failure that looks like success from the outside: the
            // client stays connected and simply never hears about a driver going offline.
            if (item.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2))
            {
                throw new InvalidOperationException(
                    $"EMQX refused the subscription to '{item.TopicFilter.Topic}': {item.ResultCode}.");
            }
        }

        Volatile.Write(ref _subscribed, true);
        logger.LogInformation("Dispatch presence subscription live on {Filter} (R-15)", StatusFilter);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (stoppingToken.Register(() => stopped.TrySetResult()))
        {
            await Task.WhenAny(disconnected.Task, stopped.Task);
        }

        if (stoppingToken.IsCancellationRequested && client.IsConnected)
        {
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
    }

    private async Task ApplyAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        args.AutoAcknowledge = false;

        try
        {
            if (!MqttTopics.TryParse(args.ApplicationMessage.Topic, out var reference)
                || reference.Kind != MqttTopicKind.Status)
            {
                return;
            }

            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload).Trim();

            await using var scope = services.CreateAsyncScope();
            var status = scope.ServiceProvider.GetRequiredService<IVehicleStatusService>();

            if (string.Equals(payload, VehicleStatus.Offline, StringComparison.OrdinalIgnoreCase))
            {
                await status.WentOfflineAsync(reference.VehicleId, cancellationToken);
                Interlocked.Increment(ref _applied);
            }
            else if (string.Equals(payload, VehicleStatus.Online, StringComparison.OrdinalIgnoreCase))
            {
                await status.CameOnlineAsync(reference.VehicleId, cancellationToken);
                Interlocked.Increment(ref _applied);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not acknowledged, so EMQX redelivers. Both paths are idempotent — arming is
            // arm-if-absent and retiring is an update that has already happened — so redelivery is
            // strictly better than dropping a last will on the floor.
            logger.LogError(exception, "Could not apply presence from {Topic}", args.ApplicationMessage.Topic);
            return;
        }

        await args.AcknowledgeAsync(cancellationToken);
    }

    private static TimeSpan BackoffFor(int attempt) =>
        attempt <= 0 ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 5))));
}

using System.Text;
using MageRide.Shared.Mqtt;
using MageRide.TripState.Configuration;
using MageRide.TripState.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.TripState.Mqtt;

/// <summary>
/// Holds the EMQX presence subscription and records when a vehicle goes away (R-15, T-04).
/// </summary>
/// <remarks>
/// <para>
/// <c>veh/{vehicleId}/status</c> is the retained last-will topic: the device publishes
/// <c>online</c> after CONNECT and the broker publishes <c>offline</c> on its behalf when the
/// session dies, whether the device disconnected cleanly, ran out of battery or drove into a
/// tunnel. D3' §3.2's table routes it to dispatch-svc, trip-state-svc and fleet-health — this is
/// this service's half.
/// </para>
/// <para>
/// <b>An <c>offline</c> does not end a session here.</b> It records the instant, and the sweep
/// ends the session when the vehicle has stayed away for <see cref="TripStateOptions.OfflineGrace"/>.
/// Ending on the first last will would close a journey every time a bus passes under a bridge, and
/// R-15/T-04 say nothing about how long a coverage gap is allowed to be — so the grace is this
/// service's number and is stated as one.
/// </para>
/// <para>
/// <b>Not a shared subscription.</b> The bridge splits the position firehose across replicas
/// because every message is work; presence is rare, idempotent and needs to reach whichever
/// replica happens to run the sweep — so every replica takes the whole retained topic and the
/// database settles the duplicate.
/// </para>
/// </remarks>
public sealed class VehicleStatusWorker(
    IServiceProvider services,
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    IOptions<TripStateOptions> tripOptions,
    TimeProvider clock,
    ILogger<VehicleStatusWorker> logger) : BackgroundService
{
    /// <summary>Every vehicle's presence topic — <c>veh/+/status</c>.</summary>
    public const string StatusFilter = "veh/+/status";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));
    private readonly TripStateOptions _trip =
        tripOptions?.Value ?? throw new ArgumentNullException(nameof(tripOptions));

    /// <summary>True once the subscription is live, so a test can wait rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    /// <summary>Presence messages this replica has applied. Read by the R-15/T-04 test.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    private bool _subscribed;
    private long _applied;

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

        var credential = tokens.IssueForService(_trip.MqttServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId($"mageride-trip-state-presence-{Guid.NewGuid():N}")
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
            // client stays connected and simply never hears about a vehicle going offline.
            if (item.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2))
            {
                throw new InvalidOperationException(
                    $"EMQX refused the subscription to '{item.TopicFilter.Topic}': {item.ResultCode}.");
            }
        }

        Volatile.Write(ref _subscribed, true);
        logger.LogInformation("Presence subscription live on {Filter} (R-15, T-04)", StatusFilter);

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

            // The retained `online` a device publishes after CONNECT is not interesting here: a
            // vehicle that is present is the normal case and changes nothing about its session.
            // What matters is the transition away, which is the last will.
            if (!string.Equals(payload, VehicleStatus.Offline, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(payload, VehicleStatus.Online, StringComparison.OrdinalIgnoreCase))
                {
                    await ClearOfflineAsync(reference.VehicleId, cancellationToken);
                }

                return;
            }

            await RecordOfflineAsync(reference.VehicleId, cancellationToken);
            Interlocked.Increment(ref _applied);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not acknowledged, so EMQX redelivers. Presence is idempotent — recording the same
            // offline instant twice is a no-op — so redelivery is strictly better than dropping it.
            logger.LogError(
                exception, "Could not apply presence from {Topic}", args.ApplicationMessage.Topic);

            return;
        }

        await args.AcknowledgeAsync(cancellationToken);
    }

    private async Task RecordOfflineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var presence = scope.ServiceProvider.GetRequiredService<IVehiclePresenceStore>();

        await presence.MarkOfflineAsync(vehicleId, clock.GetUtcNow(), cancellationToken);

        logger.LogInformation(
            "Vehicle {VehicleId} went offline; its session ends in {Grace} unless it comes back (R-15, T-04)",
            vehicleId, _trip.OfflineGrace);
    }

    private async Task ClearOfflineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var presence = scope.ServiceProvider.GetRequiredService<IVehiclePresenceStore>();

        await presence.MarkOnlineAsync(vehicleId, cancellationToken);
    }

    private static TimeSpan BackoffFor(int attempt) =>
        attempt <= 0 ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 5))));
}

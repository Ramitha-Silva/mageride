using System.Text;
using MageRide.Ride.Configuration;
using MageRide.Shared.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.Ride.Mqtt;

/// <summary>
/// Holds the EMQX presence subscription and starts the R-15 grace clock when a ride's driver goes
/// off the air.
/// </summary>
/// <remarks>
/// <para>
/// ADD §6's ride-svc row lists <c>veh/{vehicleId}/status</c> (LWT) among what this service
/// consumes, so the subscription is ride-svc's own. §11.12 also has dispatch-svc calling
/// <c>system-cancel</c> on an expired grace, and both are true: the durable
/// <c>offline_grace</c> timer is the ride's, and whichever of the two triggers reaches the matrix
/// first settles it — the other finds no matrix row and does nothing.
/// </para>
/// <para>
/// <b>A last will starts a clock; it does not cancel a ride.</b> R-16 gives four windows — 60 s
/// after accept, 120 s after arrive, 5 min in progress, 10 min at payment — precisely because a
/// driver who drives into an underpass has not abandoned anybody. So an <c>offline</c> arms the
/// timer and an <c>online</c> retires it; only the timer expiring reaches the §11.12 matrix.
/// </para>
/// <para>
/// <b>"GPS not advancing" is the same fact, not a second one.</b> §11.12's in-progress row reads
/// "LWT → offline &gt; 5 min, GPS not advancing". A vehicle whose broker session has been dead for
/// five minutes is publishing no positions by construction — the last will fires when the session
/// dies, and positions travel on that session. The clause distinguishes a dropped connection from a
/// flap, and the <c>online</c> retirement is what implements the distinction.
/// </para>
/// <para>
/// <b>Not a shared subscription.</b> The bridge splits the position firehose across replicas
/// because every message is work; presence is rare and idempotent, so every replica takes the whole
/// retained topic and <c>ArmIfAbsentAsync</c> settles the duplicate in the database.
/// </para>
/// </remarks>
public sealed class VehiclePresenceWorker(
    IServiceProvider services,
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    IOptions<RideOptions> rideOptions,
    ILogger<VehiclePresenceWorker> logger) : BackgroundService
{
    /// <summary>Every vehicle's presence topic — <c>veh/+/status</c>.</summary>
    public const string StatusFilter = "veh/+/status";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));
    private readonly RideOptions _ride = rideOptions?.Value ?? throw new ArgumentNullException(nameof(rideOptions));

    /// <summary>True once the subscription is live, so a test can wait rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    /// <summary>Presence messages this replica has applied. Read by the R-15/R-16 test.</summary>
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

        var credential = tokens.IssueForService(_ride.MqttServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId($"mageride-ride-presence-{Guid.NewGuid():N}")
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
        logger.LogInformation("Ride presence subscription live on {Filter} (R-15, R-16)", StatusFilter);

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
            var presence = scope.ServiceProvider.GetRequiredService<IVehiclePresence>();

            if (string.Equals(payload, VehicleStatus.Offline, StringComparison.OrdinalIgnoreCase))
            {
                await presence.WentOfflineAsync(reference.VehicleId, cancellationToken);
                Interlocked.Increment(ref _applied);
            }
            else if (string.Equals(payload, VehicleStatus.Online, StringComparison.OrdinalIgnoreCase))
            {
                await presence.CameOnlineAsync(reference.VehicleId, cancellationToken);
                Interlocked.Increment(ref _applied);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not acknowledged, so EMQX redelivers. Both paths are idempotent — arming is
            // arm-if-absent, forgiving is a retirement that has already happened — so redelivery is
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

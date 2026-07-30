using System.Text;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Mqtt;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Holds the EMQX presence subscription and takes a vehicle off the map the moment its broker
/// session dies (R-15, T-04, US-7.17).
/// </summary>
/// <remarks>
/// <para>
/// ADD §6's fanout-svc row names the last will explicitly: "a vehicle … whose EMQX LWT marks it
/// <c>status=offline</c> (GPS off / app offline) is dropped from public groups until live ingest
/// resumes". The freshness window covers the same ground a minute later and is the backstop; this
/// is what makes it immediate, which matters because the interesting case is a passenger about to
/// walk towards a three-wheeler whose driver just closed the app.
/// </para>
/// <para>
/// <b>Not a shared subscription.</b> mqtt-bridge-svc splits the position firehose across replicas
/// because every message there is work; presence is rare and the effect is a Redis write that is
/// idempotent, so every replica takes the whole topic. Doing it the other way round would leave a
/// replica's own visibility filter reading a key some other replica may or may not have written yet.
/// </para>
/// <para>
/// <b>The mark is written to Redis, not applied to groups.</b> A vehicle going offline is a fact the
/// filter reads on the next tick, on every replica; pushing a <c>VehicleRemoved</c> from here would
/// reach only the connections of whichever replicas happened to hold the subscription, and the pumps
/// already send it from the audience's own replica.
/// </para>
/// </remarks>
internal sealed class PresenceWorker(
    IVisibilityIndex visibility,
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    TimeProvider clock,
    ILogger<PresenceWorker> logger) : BackgroundService
{
    /// <summary>Every vehicle's presence topic — <c>veh/+/status</c> (D3' §3.2).</summary>
    public const string StatusFilter = "veh/+/status";

    /// <summary>The MQTT identity this service connects as; <c>acl.conf</c> grants <c>svc-*</c>.</summary>
    public const string ServiceName = "fanout";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));

    private bool _subscribed;
    private long _applied;

    /// <summary>True once the subscription is live, so a test can wait rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    /// <summary>Presence messages this replica has applied.</summary>
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

                // Never fatal. The freshness window is the backstop, so a broker this service cannot
                // reach costs US-7.17 its immediacy and not its effect — and a fan-out plane that
                // refused to start because EMQX was down would take the live map with it.
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

        var credential = tokens.IssueForService(ServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId($"mageride-fanout-presence-{Guid.NewGuid():N}")
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
        logger.LogInformation("Fan-out presence subscription live on {Filter} (US-7.17)", StatusFilter);

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

            if (string.Equals(payload, VehicleStatus.Offline, StringComparison.OrdinalIgnoreCase))
            {
                // Stamped with this replica's clock rather than with anything in the message: the
                // mark is compared against a sample's `sampleTs`, which position-processor-svc has
                // already accepted as being within T-07's skew of server time.
                await visibility.MarkOfflineAsync(reference.VehicleId, clock.GetUtcNow(), cancellationToken);
                Interlocked.Increment(ref _applied);
            }
            else if (string.Equals(payload, VehicleStatus.Online, StringComparison.OrdinalIgnoreCase))
            {
                await visibility.MarkOnlineAsync(reference.VehicleId, cancellationToken);
                Interlocked.Increment(ref _applied);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not acknowledged, so EMQX redelivers. Both writes are idempotent, so redelivery is
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

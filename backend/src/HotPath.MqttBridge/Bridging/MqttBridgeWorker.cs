using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.HotPath.MqttBridge.Bridging;

/// <summary>
/// Holds the EMQX shared subscription and forwards every device payload to <c>telemetry.raw</c>
/// (ADD §7.3, D6' §3.3, E-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shared subscription is the whole component.</b> N replicas all subscribe
/// <c>$share/posGroup/veh/+/pos/live</c>; EMQX dispatches each message to <b>exactly one</b> of
/// them, so the bridge scales horizontally with no coordinator and no duplicate ingest. Dropping
/// the <c>$share/</c> prefix would not fail — every replica would simply receive every message and
/// <c>telemetry.raw</c> would carry one copy per replica, which is why
/// <c>MqttTopics.SharedPositionLive</c> builds the filter and nothing here writes it by hand.
/// </para>
/// <para>
/// <b>It decodes nothing.</b> The payload crosses as opaque bytes. CBOR is what the driver app
/// sends today, but a tracker's firmware is not something this platform gets to revise, and a
/// bridge that parsed payloads would drop a sample it merely failed to understand — before anyone
/// could see it on <c>telemetry.raw</c> and find out why. Normalisation, the <c>seq</c> dedupe and
/// the anti-spoof gates are position-processor-svc's.
/// </para>
/// <para>
/// <b>The partition key comes from the topic, never from the payload.</b> The topic is the half
/// EMQX authenticated — <c>acl.conf</c> binds a device to <c>veh/${username}/*</c> and
/// <c>emqx.conf</c> binds <c>${username}</c> to the token's <c>vehicleId</c> claim — while the
/// payload is whatever the device chose to write. Keying on the payload would let a compromised
/// handset write into another vehicle's partition, which is exactly the impersonation the ACL
/// exists to stop.
/// </para>
/// <para>
/// <b>Acknowledgement is manual and follows the produce.</b> MQTTnet acknowledges automatically as
/// soon as the handler returns; that would make EMQX → Redpanda at-most-once, so a Redpanda blip
/// would lose positions the broker had already been told were safe. Here the PUBACK goes out only
/// after the broker has persisted the record. A payload that cannot be produced is <b>not</b>
/// acknowledged, and EMQX redispatches it to another member of the group when this session ends.
/// There is no in-process retry and no <c>telemetry.raw.dlq</c> — D6' §2.3 specifies one and C039
/// owns it.
/// </para>
/// </remarks>
internal sealed class MqttBridgeWorker(
    IEventPublisher publisher,
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    IOptions<MqttBridgeOptions> bridgeOptions,
    ILogger<MqttBridgeWorker> logger) : BackgroundService
{
    /// <summary>Header naming the concrete MQTT topic a record came off.</summary>
    public const string TopicHeader = "mqttTopic";

    /// <summary>Header distinguishing the live stream from the backlog: <c>live</c> | <c>replay</c>.</summary>
    public const string StreamHeader = "stream";

    /// <summary>Header stamping when the bridge saw the payload — the platform's receive clock.</summary>
    public const string ReceivedAtHeader = "receivedTs";

    /// <summary>Header naming the replica that forwarded it, for E-08 attribution.</summary>
    public const string BridgeHeader = "bridge";

    /// <summary><see cref="StreamHeader"/> value for <c>veh/+/pos/live</c>.</summary>
    public const string LiveStream = "live";

    /// <summary><see cref="StreamHeader"/> value for <c>veh/+/pos/replay</c>.</summary>
    public const string ReplayStream = "replay";

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));
    private readonly MqttBridgeOptions _bridge =
        bridgeOptions?.Value ?? throw new ArgumentNullException(nameof(bridgeOptions));

    /// <summary>
    /// This replica's MQTT client id. Unique per process: two clients presenting one id make the
    /// broker disconnect the first, which across replicas looks exactly like a flapping bridge.
    /// </summary>
    public string ClientId { get; } =
        $"{bridgeOptions?.Value.ClientIdPrefix ?? "mageride-bridge"}-{Guid.NewGuid():N}";

    /// <summary>Payloads this replica has forwarded. Read by the E-08 test, which has to show that
    /// two replicas <i>shared</i> the stream rather than each seeing all of it.</summary>
    public long Forwarded => Interlocked.Read(ref _forwarded);

    /// <summary>True once the subscription is live, so a test can wait for readiness rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    private long _forwarded;
    private bool _subscribed;

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
            catch (Exception ex)
            {
                attempt++;
                logger.LogError(ex, "MQTT bridge session {Attempt} ended; reconnecting", attempt);
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
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        // Signalled when the broker (or the network) drops us, so the supervision loop above can
        // reconnect instead of sitting on a dead socket.
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.DisconnectedAsync += args =>
        {
            logger.LogWarning(
                "MQTT bridge {ClientId} disconnected: {Reason}", ClientId, args.ReasonString ?? args.Reason.ToString());

            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += args => ForwardAsync(args, stoppingToken);

        var credential = tokens.IssueForService(_bridge.ServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId(ClientId)
            // The bridge authenticates exactly as a device does — username plus session JWT — and
            // `acl.conf` grants the `svc-` prefix the wildcard and `$share/#` privileges E-08 needs.
            .WithCredentials(credential.Username, credential.Jwt)
            // Shared subscriptions and the reason codes that report them are MQTT 5 features.
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithKeepAlivePeriod(_bridge.KeepAlive)
            .WithTimeout(_bridge.ConnectTimeout)
            // Clean start with no session expiry: a bridge replica holds no state worth resuming,
            // and ending the session promptly is what makes EMQX redispatch anything this replica
            // took but never acknowledged to another member of the group.
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0);

        if (_mqtt.UseTls)
        {
            options = options.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(_mqtt.Host));
        }

        using var connectCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCancellation.CancelAfter(_bridge.ConnectTimeout);

        var result = await client.ConnectAsync(options.Build(), connectCancellation.Token);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"EMQX refused the bridge CONNECT: {result.ResultCode} ({result.ReasonString}). " +
                "Check that Mqtt:SessionTokenSecret matches EMQX_AUTHENTICATION__1__SECRET.");
        }

        await SubscribeAsync(client, stoppingToken);

        Volatile.Write(ref _subscribed, true);
        logger.LogInformation(
            "MQTT bridge {ClientId} subscribed to {Filter}", ClientId, MqttTopics.SharedPositionLive(_bridge.LiveShareGroup));

        // Park until the broker goes away or the host stops. The receive loop runs on MQTTnet's own
        // thread; there is nothing for this one to do but supervise.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (stoppingToken.Register(() => stopped.TrySetResult()))
        {
            await Task.WhenAny(disconnected.Task, stopped.Task);
        }

        if (stoppingToken.IsCancellationRequested && client.IsConnected)
        {
            // A clean DISCONNECT rather than a dropped socket, so EMQX redispatches this replica's
            // share immediately instead of waiting out the keep-alive.
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
    }

    private async Task SubscribeAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter
                .WithTopic(MqttTopics.SharedPositionLive(_bridge.LiveShareGroup))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));

        if (_bridge.ConsumeReplay)
        {
            builder = builder.WithTopicFilter(filter => filter
                .WithTopic(MqttTopics.SharedPositionReplay(_bridge.ReplayShareGroup))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        }

        var result = await client.SubscribeAsync(builder.Build(), cancellationToken);

        foreach (var item in result.Items)
        {
            // A refused shared subscription is the one failure that looks like success from the
            // outside: the bridge stays connected and simply never receives anything.
            if (item.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2))
            {
                throw new InvalidOperationException(
                    $"EMQX refused the subscription to '{item.TopicFilter.Topic}': {item.ResultCode}.");
            }
        }
    }

    private async Task ForwardAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        // Before anything can throw: MQTTnet acknowledges on return unless this is cleared first.
        args.AutoAcknowledge = false;

        var topic = args.ApplicationMessage.Topic;

        if (!MqttTopics.TryParse(topic, out var reference)
            || reference.Kind is not (MqttTopicKind.PositionLive or MqttTopicKind.PositionReplay))
        {
            // Not a position topic. Acknowledged so it does not sit in flight forever — the bridge
            // subscribes to two filters and neither can deliver anything else, so this is a
            // misconfiguration worth saying out loud.
            logger.LogWarning("Ignoring a message on an unexpected topic '{Topic}'", topic);
            await args.AcknowledgeAsync(cancellationToken);
            return;
        }

        var stream = reference.Kind == MqttTopicKind.PositionLive ? LiveStream : ReplayStream;
        var receivedAt = DateTimeOffset.UtcNow;

        var message = new EventMessage(
            EventTopics.TelemetryRaw,
            // vehicleId keys the partition, so every sample from one vehicle stays in order
            // end to end (D6' §2.1).
            reference.VehicleId.ToString(),
            // The payload is a ReadOnlySequence over MQTTnet's receive buffer, which is recycled the
            // moment this handler returns. The producer's send is asynchronous, so the bytes have
            // to be copied out rather than referenced.
            BuffersExtensions.ToArray(args.ApplicationMessage.Payload),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TopicHeader] = topic,
                [StreamHeader] = stream,
                [ReceivedAtHeader] = receivedAt.ToString("O", CultureInfo.InvariantCulture),
                [BridgeHeader] = ClientId,
            });

        using var activity = MageRideDiagnostics.ActivitySource.StartActivity(
            "mqtt-bridge.forward", ActivityKind.Producer);
        activity?.SetTag("mageride.vehicle_id", reference.VehicleId);
        activity?.SetTag("messaging.destination.name", EventTopics.TelemetryRaw);

        try
        {
            await publisher.PublishAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not acknowledged. EMQX still holds it, and redispatches it to another
            // member of the group when this session ends.
            MageRideDiagnostics.MqttBridgeFailures.Add(1, new KeyValuePair<string, object?>("stream", stream));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            logger.LogError(
                ex, "Could not forward {Topic} to {RawTopic}; leaving it unacknowledged",
                topic, EventTopics.TelemetryRaw);
            return;
        }

        await args.AcknowledgeAsync(cancellationToken);

        Interlocked.Increment(ref _forwarded);
        MageRideDiagnostics.MqttBridgeForwarded.Add(1, new KeyValuePair<string, object?>("stream", stream));
    }

    /// <summary>
    /// R-09's jittered exponential backoff: 1 s to 60 s with ±25 %. The same symmetric band the
    /// kernel uses for Polly — a decorrelated curve would let a fleet re-synchronise into a second
    /// thundering herd.
    /// </summary>
    private TimeSpan BackoffFor(int attempt)
    {
        if (attempt <= 0)
        {
            return _bridge.ReconnectDelayMin;
        }

        var exponential = _bridge.ReconnectDelayMin * Math.Pow(2, Math.Min(attempt - 1, 16));
        var capped = exponential > _bridge.ReconnectDelayMax ? _bridge.ReconnectDelayMax : exponential;
        var jitter = 1 + ((Random.Shared.NextDouble() - 0.5) / 2);

        return capped * jitter;
    }
}

using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.HotPath.MqttBridge.Throttling;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.MqttBridge.Bridging;

/// <summary>
/// Holds the EMQX shared subscriptions and forwards every device payload to <c>telemetry.raw</c>
/// (ADD §7.3, D6' §3.3, E-08, R-09, T-05, D-17).
/// </summary>
/// <remarks>
/// <para>
/// Two broker sessions, not one. <c>$share/posGroup/veh/+/pos/live</c> is the hot path and goes
/// straight to the producer; <c>$share/posReplayGroup/veh/+/pos/replay</c> is a backlog and goes
/// through <see cref="ReplayPacer"/> at T-05's 20 samples/s/device. Separate sessions are what make
/// the second one incapable of starving the first — see <see cref="MqttStreamSession"/>.
/// </para>
/// <para>
/// The <c>$share/</c> prefix, the topic-derived partition key, the opaque payload and the
/// acknowledge-after-produce rule are all load-bearing and are each explained where they live:
/// <see cref="MqttStreamSession"/>, <see cref="TelemetryForwarder"/>, <see cref="BridgedMessage"/>.
/// </para>
/// </remarks>
internal sealed class MqttBridgeWorker : BackgroundService, IAsyncDisposable
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

    private readonly MqttBridgeOptions _bridge;
    private readonly TelemetryForwarder _forwarder;
    private readonly ReplayPacer? _pacer;
    private readonly List<MqttStreamSession> _sessions = [];
    private readonly ILogger<MqttBridgeWorker> _logger;

    public MqttBridgeWorker(
        IEventPublisher publisher,
        MqttSessionTokenIssuer tokens,
        IOptions<MqttOptions> mqttOptions,
        IOptions<MqttBridgeOptions> bridgeOptions,
        ReplayThrottle replayThrottle,
        PublishRateMonitor rateMonitor,
        ILogger<MqttBridgeWorker> logger,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(mqttOptions);
        ArgumentNullException.ThrowIfNull(bridgeOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _bridge = bridgeOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ClientId = $"{_bridge.ClientIdPrefix}-{Guid.NewGuid():N}";

        _forwarder = new TelemetryForwarder(
            publisher,
            new PartitionOffsetLog(ClientId).Register(),
            // Null when the monitor is not hosted: unflushed observations would otherwise accumulate
            // one dictionary entry per vehicle per second, forever.
            _bridge.MonitorPublishRate ? rateMonitor : null,
            ClientId,
            loggerFactory.CreateLogger<TelemetryForwarder>());

        _sessions.Add(new MqttStreamSession(
            LiveStream,
            MqttTopics.SharedPositionLive(_bridge.LiveShareGroup),
            $"{ClientId}-live",
            // Not awaited: the produce is enqueued synchronously and in receive order, and the wait
            // for the delivery report happens on a continuation. Awaiting here would cap the replica
            // at one broker round trip per sample.
            bridged => { _ = _forwarder.Forward(bridged); return Task.CompletedTask; },
            mqttOptions.Value,
            _bridge,
            tokens,
            loggerFactory.CreateLogger<MqttStreamSession>())
        {
            DrainWithinAsync = _forwarder.DrainAsync,
        });

        if (!_bridge.ConsumeReplay)
        {
            return;
        }

        _pacer = _bridge.ThrottleReplay
            ? new ReplayPacer(
                replayThrottle,
                bridgeOptions,
                bridged => _forwarder.Forward(bridged),
                loggerFactory.CreateLogger<ReplayPacer>())
            : null;

        _sessions.Add(new MqttStreamSession(
            ReplayStream,
            MqttTopics.SharedPositionReplay(_bridge.ReplayShareGroup),
            $"{ClientId}-replay",
            ReceiveReplayAsync,
            mqttOptions.Value,
            _bridge,
            tokens,
            loggerFactory.CreateLogger<MqttStreamSession>())
        {
            DrainWithinAsync = DrainReplayAsync,
        });
    }

    /// <summary>
    /// This replica's identity. Each stream's MQTT client id is derived from it, because two clients
    /// presenting one id make the broker disconnect the first — which, across replicas, looks
    /// exactly like a flapping bridge.
    /// </summary>
    public string ClientId { get; }

    /// <summary>Payloads this replica has forwarded. Read by the E-08 test, which has to show that
    /// two replicas <i>shared</i> the stream rather than each seeing all of it.</summary>
    public long Forwarded => _forwarder.Forwarded;

    /// <summary>Payloads forwarded off the live stream alone.</summary>
    public long ForwardedLive => _forwarder.ForwardedLive;

    /// <summary>Payloads forwarded off the backlog stream alone.</summary>
    public long ForwardedReplay => _forwarder.ForwardedReplay;

    /// <summary>Backlog samples that had to wait for a T-05 token — the throttle's own evidence.</summary>
    public long ReplayThrottled => _pacer?.Throttled ?? 0;

    /// <summary>Highest <c>telemetry.raw</c> offset this replica has written, per partition.</summary>
    public IReadOnlyDictionary<int, long> PartitionOffsets => _forwarder.PartitionOffsets;

    /// <summary>True once every stream holds its subscription, so a test can wait rather than sleep.</summary>
    public bool IsSubscribed => _sessions.TrueForAll(session => session.IsSubscribed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await Task.WhenAll(_sessions.Select(session => session.RunAsync(stoppingToken)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancels ExecuteAsync's token, which is what puts each session into its
        // unsubscribe-drain-disconnect sequence. The base call returns once they are through it.
        await base.StopAsync(cancellationToken);

        if (_forwarder.InFlight > 0)
        {
            _logger.LogWarning(
                "{Bridge} stopped with {InFlight} forward(s) still in flight", ClientId, _forwarder.InFlight);
        }
    }

    public override void Dispose()
    {
        _forwarder.Dispose();
        base.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_pacer is not null)
        {
            await _pacer.DisposeAsync();
        }

        Dispose();
    }

    private Task ReceiveReplayAsync(BridgedMessage bridged)
    {
        if (_pacer is null)
        {
            // Throttling off. Only a test that is measuring something else asks for this — a bridge
            // with no T-05 limit is the reconnect storm R-09 exists to prevent.
            _ = _forwarder.Forward(bridged);
            return Task.CompletedTask;
        }

        // Refused samples are left unacknowledged on purpose: EMQX still holds them.
        _pacer.TryEnqueue(bridged);
        return Task.CompletedTask;
    }

    private async Task<bool> DrainReplayAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        // The queue first — a sample still on a lane has not been produced, so waiting on the
        // forwarder alone would report a drained bridge with work left to do.
        if (_pacer is not null && !await _pacer.DrainAsync(timeout))
        {
            return false;
        }

        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero && await _forwarder.DrainAsync(remaining);
    }
}

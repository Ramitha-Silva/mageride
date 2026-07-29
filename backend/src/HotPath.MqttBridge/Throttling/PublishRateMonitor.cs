using System.Collections.Concurrent;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.HotPath.MqttBridge.Throttling;

/// <summary>
/// D-17's per-vehicle publish ceiling on <c>veh/+/pos/live</c>: counts, and raises
/// <c>mqtt.rate_violation</c> onto <c>audit.events</c> when a vehicle goes over it
/// (ADD §7.5.2, D6' §3.3, <c>backend/contracts/realtime/mqtt-topics.md</c> §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>It counts; it does not enforce.</b> Enforcement is the broker's — <c>emqx.conf</c> sets
/// <c>messages_rate = "5/s"</c> on every listener, and a client over it is paused there. The bridge
/// forwards the sample regardless: a position dropped here would be one anti-spoof never gets to
/// look at, and a vehicle publishing too fast is a case for investigation rather than for silence.
/// </para>
/// <para>
/// <b>The bridge is where the ceiling can actually be observed.</b> D6' §3.3 asks the EMQX rule
/// engine for this, with a <c>TUMBLINGWINDOW</c> aggregate the spec itself calls illustrative — open
/// -source EMQX 5.8 has no windowed aggregation, and the listener limiter that does the enforcing
/// emits no event. The listener limiter is also <i>per connection</i> while D-17 is written per
/// <c>vehicleId</c>, so a device opening two sessions under one vehicle credential passes the broker
/// and still breaches the ceiling. The bridge sees every live sample exactly once (E-08), across all
/// of a vehicle's connections, which makes it the only component that can measure the rate D-17
/// names. Recorded as a spec finding in the C038 handoff.
/// </para>
/// <para>
/// <b>The window is in Redis, and so is the debounce.</b> A shared subscription hands each replica a
/// random slice of a vehicle's stream, so no replica on its own ever sees the true rate; the counts
/// are folded into a per-second key every <see cref="MqttBridgeOptions.RateFlushInterval"/> and the
/// replica whose fold crosses the ceiling wins a <c>SET NX</c> against the cooldown key and writes
/// the one audit event. One report per vehicle per cooldown, however many replicas noticed.
/// </para>
/// </remarks>
internal sealed class PublishRateMonitor(
    IConnectionMultiplexer redis,
    IEventPublisher publisher,
    IOptions<MqttBridgeOptions> options,
    TimeProvider time,
    ILogger<PublishRateMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Window, Counter> _counts = new();
    private readonly MqttBridgeOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private long _violations;

    /// <summary>Violations this replica reported. Read by the D-17 test.</summary>
    public long Violations => Interlocked.Read(ref _violations);

    /// <summary>Records one live sample. On the hot path: a dictionary bump, no I/O.</summary>
    /// <param name="receivedAt">
    /// When the bridge took the payload off the broker, not when the produce completed — a sample
    /// belongs to the second the device published it in, and the produce can lag by milliseconds.
    /// </param>
    public void Observe(Guid vehicleId, DateTimeOffset receivedAt)
    {
        var window = new Window(vehicleId, receivedAt.ToUnixTimeSeconds());
        _counts.GetOrAdd(window, _ => new Counter()).Increment();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(_options.RateFlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping. The counts still in hand are one second of a rate nobody is now measuring.
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();

        foreach (var window in _counts.Keys)
        {
            if (window.UnixSecond >= now)
            {
                // Still open. Folding it early would report a fraction of a second as a rate.
                continue;
            }

            if (!_counts.TryRemove(window, out var counter))
            {
                continue;
            }

            var count = counter.Read();

            if (count <= 0)
            {
                continue;
            }

            try
            {
                await FoldAsync(window, count, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The ceiling is the broker's to enforce; losing an observation of it costs a report,
                // not a guarantee.
                logger.LogWarning(
                    ex, "Could not fold the D-17 publish window for {VehicleId}", window.VehicleId);
            }
        }
    }

    private async Task FoldAsync(Window window, long count, CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        var key = (RedisKey)RedisKeys.VehiclePublishWindow(window.VehicleId, window.UnixSecond);

        var observed = await database.StringIncrementAsync(key, count);

        // Well past the window it counts, so a straggler folded a tick late still lands on the same
        // total rather than starting a second, smaller one.
        await database.KeyExpireAsync(key, TimeSpan.FromSeconds(30));

        if (observed <= _options.PublishCeilingPerSecond)
        {
            return;
        }

        // One report per vehicle per cooldown, cluster-wide. Whichever replica's fold crossed the
        // ceiling first takes the key and writes the event; the rest see the vehicle is already
        // reported and say nothing.
        var claimed = await database.StringSetAsync(
            RedisKeys.VehicleRateViolation(window.VehicleId),
            window.UnixSecond,
            _options.RateViolationCooldown,
            When.NotExists);

        if (!claimed)
        {
            return;
        }

        var detectedAt = DateTimeOffset.FromUnixTimeSeconds(window.UnixSecond);

        var audit = AuditEvent.Observed(
            AuditEvent.MqttRateViolation,
            AuditEvent.VehicleEntity,
            window.VehicleId.ToString(),
            // The device is the actor. There is no user behind a position publish, and a
            // rate_violation with no actor is a fact nobody can be asked about.
            actorId: window.VehicleId.ToString(),
            after: new
            {
                topic = MqttTopics.AllPositionsLive,
                ceilingPerSecond = _options.PublishCeilingPerSecond,
                observedPerSecond = observed,
                windowStart = detectedAt,
                detectedBy = MqttBridgeApplication.ServiceName,
            },
            ts: time.GetUtcNow());

        await publisher.PublishAsync(audit.ToEventMessage(), cancellationToken);

        Interlocked.Increment(ref _violations);
        MageRideDiagnostics.MqttRateViolations.Add(1);

        logger.LogWarning(
            "Vehicle {VehicleId} published {Observed} live samples in one second against a ceiling of {Ceiling}; " +
            "raised {Action} on {Topic}",
            window.VehicleId, observed, _options.PublishCeilingPerSecond,
            AuditEvent.MqttRateViolation, EventTopics.AuditEvents);
    }

    /// <summary>One vehicle inside one wall-clock second.</summary>
    private readonly record struct Window(Guid VehicleId, long UnixSecond);

    /// <summary>A boxed counter, so an increment costs no dictionary write.</summary>
    private sealed class Counter
    {
        private long _value;

        public void Increment() => Interlocked.Increment(ref _value);

        public long Read() => Interlocked.Read(ref _value);
    }
}

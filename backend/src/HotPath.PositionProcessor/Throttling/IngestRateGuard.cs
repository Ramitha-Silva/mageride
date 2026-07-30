using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.Shared.Caching;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.HotPath.PositionProcessor.Throttling;

/// <summary>D-17's second line: whether this vehicle may still be ingested.</summary>
public interface IIngestRateGuard
{
    /// <summary>
    /// Counts one arrival and answers whether it is inside the ceiling.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> to process the sample, <see langword="false"/> to drop it. Fails
    /// <b>open</b> — a Redis outage lets samples through rather than taking the live map down for a
    /// limit the broker still half-enforces.
    /// </returns>
    Task<bool> AdmitAsync(Guid vehicleId, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}

/// <summary>
/// The second-line per-vehicle ceiling of D5' §5.3 and <c>mqtt-topics.md</c> §4 —
/// <b>10 msg/s averaged over 10 s, drop + flag</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three lines, and this is the one that drops.</b> EMQX's listener limiter enforces 5 msg/s per
/// <i>connection</i> and pauses the publisher; mqtt-bridge-svc measures the same ceiling per
/// <i>vehicle</i> and forwards regardless, because a position dropped there is one anti-spoof never
/// gets to look at. By the time a sample reaches here anti-spoof has looked at it, and a vehicle
/// still doubling the broker's ceiling is publishing from several sessions under one credential —
/// the exact case <c>mqtt-topics.md</c> §4 says the first line cannot see. So this one drops.
/// </para>
/// <para>
/// <b>Averaged over ten seconds, not per second.</b> D5' §5.2's near-geofence cadence is one sample
/// a second and R-07 lets the server ask for bursts on top of it; an instantaneous threshold here
/// would fire on a vehicle doing exactly what the platform told it to. Ten seconds is long enough
/// for a burst to average out and short enough that a flood is caught inside one window.
/// </para>
/// <para>
/// <b>The counter is in Redis, keyed by vehicle.</b> <c>telemetry.raw</c> is partitioned by
/// <c>vehicleId</c>, so one consumer owns a vehicle at a time and an in-process counter would
/// <i>almost</i> work — but the assignment moves on every rebalance and a per-replica count would
/// reset with it, which is when a flooding device is most likely to be the cause. The key is also
/// what lets an operator read the rate a vehicle is publishing at without a metrics query.
/// </para>
/// <para>
/// <b>One audit event per vehicle per cooldown, cluster-wide.</b> A vehicle at 50 msg/s would
/// otherwise write an audit row per sample — turning a rate problem into a second, larger one on
/// <c>audit.events</c>. The debounce is its own key rather than the bridge's, so the first line
/// firing cannot silence the second.
/// </para>
/// </remarks>
public sealed class IngestRateGuard(
    IConnectionMultiplexer redis,
    IEventPublisher publisher,
    IOptions<PositionProcessorOptions> options,
    TimeProvider clock,
    ILogger<IngestRateGuard> logger) : IIngestRateGuard
{
    private readonly PositionProcessorOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The ceiling in samples per window — 10 msg/s over 10 s is 100.</summary>
    public long CeilingPerWindow =>
        (long)Math.Round(_options.RateCeilingPerSecond * _options.RateCheckWindow.TotalSeconds);

    public async Task<bool> AdmitAsync(
        Guid vehicleId, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var windowSeconds = (long)_options.RateCheckWindow.TotalSeconds;

        // Fixed windows rather than a sliding one. A sliding window costs a sorted set per vehicle
        // and a ZREMRANGEBYSCORE on the hot path; the cost of the fixed one is that a burst
        // straddling a boundary can pass at up to twice the rate for one window, which for a
        // *second-line misbehaviour ceiling* — already twice the broker's — is not worth the write.
        var windowStart = receivedAt.ToUnixTimeSeconds() / windowSeconds * windowSeconds;
        var key = (RedisKey)RedisKeys.VehicleIngestWindow(vehicleId, windowStart);

        long observed;

        try
        {
            var db = redis.GetDatabase();
            observed = await db.StringIncrementAsync(key);

            if (observed == 1)
            {
                // Only on the window's first sample: an EXPIRE per sample would push the window's
                // end out for as long as the vehicle keeps publishing, which is exactly backwards.
                // Twice the window, so a straggler counted late still lands on the same total.
                await db.KeyExpireAsync(key, _options.RateCheckWindow + _options.RateCheckWindow);
            }
        }
        catch (RedisException ex)
        {
            // Fails open, like the bridge's two counters. Losing telemetry to a cache outage is
            // worse than losing a ceiling the broker still half-enforces.
            logger.LogWarning(ex, "Could not count the D-17 second-line window for vehicle {VehicleId}", vehicleId);
            return true;
        }

        if (observed <= CeilingPerWindow)
        {
            return true;
        }

        await FlagAsync(vehicleId, observed, windowStart, cancellationToken);

        return false;
    }

    private async Task FlagAsync(
        Guid vehicleId, long observed, long windowStart, CancellationToken cancellationToken)
    {
        MageRideDiagnostics.PositionsDropped.Add(
            1,
            new KeyValuePair<string, object?>("reason", "rate_limited"),
            new KeyValuePair<string, object?>("vehicle_id", vehicleId));

        bool claimed;

        try
        {
            claimed = await redis.GetDatabase().StringSetAsync(
                RedisKeys.PositionRateViolation(vehicleId),
                windowStart,
                _options.RateViolationCooldown,
                When.NotExists);
        }
        catch (RedisException ex)
        {
            // The drop already happened and is counted; only the report is lost.
            logger.LogWarning(ex, "Could not claim the second-line violation debounce for {VehicleId}", vehicleId);
            return;
        }

        if (!claimed)
        {
            return;
        }

        var observedPerSecond = observed / _options.RateCheckWindow.TotalSeconds;

        var audit = AuditEvent.Observed(
            // The only audit action any spec spells for the MQTT plane (D6' §3.3, ADD §7.5.2,
            // mqtt-topics.md §4). `detectedBy` is what tells the two lines apart on the topic;
            // inventing a second action would give the same fact two names.
            AuditEvent.MqttRateViolation,
            AuditEvent.VehicleEntity,
            vehicleId.ToString(),
            // The device is the actor. There is no user behind a position publish.
            actorId: vehicleId.ToString(),
            after: new
            {
                topic = MqttTopics.AllPositionsLive,
                line = "second",
                ceilingPerSecond = _options.RateCeilingPerSecond,
                windowSeconds = _options.RateCheckWindow.TotalSeconds,
                observedPerSecond,
                observedInWindow = observed,
                windowStart = DateTimeOffset.FromUnixTimeSeconds(windowStart),
                action = "dropped",
                detectedBy = PositionProcessorApplication.ServiceName,
            },
            ts: clock.GetUtcNow());

        try
        {
            await publisher.PublishAsync(audit.ToEventMessage(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as the Redis catch: the sample is already dropped and counted, and a
            // broker problem must not turn into an unhandled exception on the consume loop that
            // would then stall the partition every other vehicle in this shard shares.
            logger.LogWarning(ex, "Could not publish {Action} for vehicle {VehicleId}",
                AuditEvent.MqttRateViolation, vehicleId);
            return;
        }

        MageRideDiagnostics.PositionRateViolations.Add(1);

        logger.LogWarning(
            "Vehicle {VehicleId} ingested {Observed} samples in {Window} ({Rate:F1}/s) against a " +
            "second-line ceiling of {Ceiling}/s; dropping and raising {Action}",
            vehicleId, observed, _options.RateCheckWindow, observedPerSecond,
            _options.RateCeilingPerSecond, AuditEvent.MqttRateViolation);
    }
}

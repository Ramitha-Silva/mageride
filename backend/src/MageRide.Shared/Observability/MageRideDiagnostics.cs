using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MageRide.Shared.Observability;

/// <summary>
/// The platform's own <see cref="ActivitySource"/> and <see cref="Meter"/>. Everything emitted
/// here is scraped as Prometheus metrics and exported over OTLP to Tempo/Loki (D7' §12).
/// </summary>
public static class MageRideDiagnostics
{
    public const string ActivitySourceName = "MageRide";
    public const string MeterName = "MageRide";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Commit-to-publish latency of the transactional outbox, in milliseconds. E-09 budgets a
    /// median under 50 ms; this is the signal an SLO alert watches.
    /// </summary>
    public static readonly Histogram<double> OutboxDispatchLatencyMs =
        Meter.CreateHistogram<double>("mageride.outbox.dispatch.latency", "ms",
            "Time from an outbox row being written to it being acknowledged by the broker.");

    /// <summary>Outbox rows successfully published.</summary>
    public static readonly Counter<long> OutboxDispatched =
        Meter.CreateCounter<long>("mageride.outbox.dispatched", "{event}", "Outbox rows published to the event backbone.");

    /// <summary>Failed publish attempts. Rows stay undispatched and are retried.</summary>
    public static readonly Counter<long> OutboxPublishFailures =
        Meter.CreateCounter<long>("mageride.outbox.publish_failures", "{failure}", "Outbox publish attempts that failed.");

    /// <summary>Requests answered from the command log instead of being executed again (R-14).</summary>
    public static readonly Counter<long> IdempotentReplays =
        Meter.CreateCounter<long>("mageride.idempotency.replays", "{request}", "Requests served as an idempotent replay.");

    /// <summary>Token-bucket rejections, tagged by policy (D-32, P-12).</summary>
    public static readonly Counter<long> RateLimitRejections =
        Meter.CreateCounter<long>("mageride.ratelimit.rejections", "{request}", "Requests rejected by a token-bucket rate limiter.");

    // --- Ride aggregate (C032): the R-20 stuck-state business SLOs --------------------------------
    // ADD §13.3.1 computes each one as `count(rides WHERE state=S AND age > T)` rolling 1 min, so
    // the gauge is the metric and the alert rule is the threshold. Published by ride-svc, tagged by
    // state; the counters either side of it are what says a backstop noticed before an operator did.

    /// <summary>
    /// Rides sitting in one state past its ADD §13.3.1 window, tagged <c>state</c>. Any sustained
    /// non-zero reading pages on-call and points at <c>runbooks/ride-stuck.md</c>.
    /// </summary>
    public const string RidesStuckGauge = "mageride.rides.stuck";

    /// <summary>A durable backstop found a ride stuck and reported it, tagged <c>state</c>.</summary>
    public static readonly Counter<long> RideStuckDetected =
        Meter.CreateCounter<long>("mageride.rides.stuck_detected", "{ride}",
            "Rides a durable timer found past their expected window (R-20).");

    /// <summary>
    /// Ride timers that fired and moved a ride, tagged <c>kind</c> — the no-show and grace
    /// transitions §11.12 makes the platform's responsibility rather than a client's.
    /// </summary>
    public static readonly Counter<long> RideTimersFired =
        Meter.CreateCounter<long>("mageride.rides.timers_fired", "{timer}",
            "Durable ride timers that fired and changed the aggregate (R-04).");

    /// <summary>
    /// Unfired <c>rides.timers</c> rows more than 30 s overdue — ADD §13.4's backlog runbook
    /// trigger ("&gt; 100 ⇒ scheduler ill").
    /// </summary>
    public const string RideTimerBacklogGauge = "mageride.rides.timer_backlog";

    // --- Hot path (C024): EMQX -> Redpanda -> Redis -> SignalR ------------------------------------
    // ADD §13.2's golden signals for the position plane. The end-to-end histogram is the one that
    // answers this component's SLO ("a position reaches the passenger's group in under 5 s, p95");
    // the counters either side of it are what says *where* the time went when it does not.

    /// <summary>Device payloads lifted off EMQX onto <c>telemetry.raw</c>, tagged by stream.</summary>
    public static readonly Counter<long> MqttBridgeForwarded =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.forwarded", "{message}", "MQTT payloads forwarded to telemetry.raw.");

    /// <summary>Payloads the bridge could not forward. Not acknowledged, so EMQX redelivers.</summary>
    public static readonly Counter<long> MqttBridgeFailures =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.failures", "{message}", "MQTT payloads the bridge failed to forward.");

    // --- mqtt-bridge-svc, production form (C038): T-05 replay throttle, D-17 ceiling -------------

    /// <summary>
    /// Replay samples the T-05 bucket made wait for a token. A sustained non-zero reading is a
    /// fleet draining its backlog, which is the state R-09 exists to keep off the live path.
    /// </summary>
    public static readonly Counter<long> MqttReplayThrottled =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.replay_throttled", "{sample}",
            "Replay samples held back by the 20/s per-device limit (T-05).");

    /// <summary>
    /// Replay samples dropped without forwarding, tagged by reason — the lane was full or the wait
    /// exceeded its ceiling. Not acknowledged, so EMQX still holds them.
    /// </summary>
    public static readonly Counter<long> MqttReplayShed =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.replay_shed", "{sample}",
            "Replay samples the bridge shed rather than queue without bound.");

    /// <summary>
    /// Time a replay sample spent waiting on its device's token bucket, in milliseconds. The
    /// counterpart to <see cref="PositionIngestLatencyMs"/>: latency here is intentional.
    /// </summary>
    public static readonly Histogram<double> MqttReplayWaitMs =
        Meter.CreateHistogram<double>("mageride.mqtt.bridge.replay_wait", "ms",
            "Time a backlog sample waited for a T-05 token before being forwarded.");

    /// <summary>
    /// Vehicles reported over D-17's 5 msg/s <c>pos/live</c> ceiling. One per vehicle per cooldown,
    /// however many replicas saw it, and one <c>mqtt.rate_violation</c> on <c>audit.events</c> each.
    /// </summary>
    public static readonly Counter<long> MqttRateViolations =
        Meter.CreateCounter<long>("mageride.mqtt.bridge.rate_violations", "{violation}",
            "Vehicles observed publishing above the D-17 per-vehicle ceiling.");

    /// <summary>
    /// Highest <c>telemetry.raw</c> offset this replica has written, tagged <c>partition</c> — the
    /// producer half of ADD §7.3's per-partition offset management.
    /// </summary>
    public const string MqttBridgePartitionOffsetGauge = "mageride.mqtt.bridge.partition_offset";

    /// <summary>Samples normalised onto <c>telemetry.normalized</c> and the live indexes.</summary>
    public static readonly Counter<long> PositionsProcessed =
        Meter.CreateCounter<long>("mageride.positions.processed", "{sample}", "Position samples normalised and indexed.");

    /// <summary>Samples dropped before indexing, tagged by reason (undecodable, malformed, replayed).</summary>
    public static readonly Counter<long> PositionsDropped =
        Meter.CreateCounter<long>("mageride.positions.dropped", "{sample}", "Position samples dropped before indexing.");

    /// <summary>
    /// GNSS capture instant to Redis cell stream, in milliseconds — the ingest half of the SLO.
    /// </summary>
    /// <remarks>
    /// Measured from the device's own clock, so a handset with a wrong clock skews it. That is
    /// deliberate: it is the number a passenger experiences, and clock skew is one of the ways the
    /// experience goes wrong.
    /// </remarks>
    public static readonly Histogram<double> PositionIngestLatencyMs =
        Meter.CreateHistogram<double>("mageride.positions.ingest.latency", "ms",
            "Time from a sample's GNSS instant to it landing on its cell stream.");

    /// <summary>Vehicle frames pushed to SignalR geocell groups.</summary>
    public static readonly Counter<long> FanoutFramesSent =
        Meter.CreateCounter<long>("mageride.fanout.frames", "{frame}", "Vehicle frames pushed to geocell groups.");

    /// <summary>
    /// Cell stream entry to SignalR send, in milliseconds — the fan-out half of the SLO.
    /// </summary>
    public static readonly Histogram<double> FanoutLatencyMs =
        Meter.CreateHistogram<double>("mageride.fanout.latency", "ms",
            "Time from a cell stream entry being written to it being pushed to a group.");
}

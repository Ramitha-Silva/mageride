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

    // --- position-processor-svc, production form (C039): D-18/T-07 anti-spoof, D-17 second line ---

    /// <summary>
    /// Samples the D-18/T-07 plausibility filter refused, tagged <c>check</c> — <c>accuracy</c>,
    /// <c>speed</c>, <c>jump</c>, <c>clock</c> or <c>satellites</c> — and <c>vehicle_type</c>.
    /// </summary>
    /// <remarks>
    /// The tag is the point. "Positions are being dropped" is not actionable; "every drop on this
    /// fleet is <c>accuracy</c>" is a coverage problem and "every drop is <c>speed</c>" is a spoofer
    /// or a wrongly-typed vehicle. D5' §13.1 also counts these toward a per-device fraud score,
    /// which nothing owns yet — see the C039 handoff.
    /// </remarks>
    public static readonly Counter<long> PositionsImplausible =
        Meter.CreateCounter<long>("mageride.positions.implausible", "{sample}",
            "Position samples refused by the D-18/T-07 plausibility filter.");

    /// <summary>
    /// Vehicles reported over D-17's second-line 10 msg/s ceiling. One per vehicle per cooldown,
    /// however many replicas saw it, and one <c>mqtt.rate_violation</c> on <c>audit.events</c> each.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="MqttRateViolations"/>, which is the bridge's observation of the
    /// broker's 5 msg/s ceiling and drops nothing. This one drops
    /// (<c>mqtt-topics.md</c> §4: "Drop + flag").
    /// </remarks>
    public static readonly Counter<long> PositionRateViolations =
        Meter.CreateCounter<long>("mageride.positions.rate_violations", "{violation}",
            "Vehicles observed publishing above D-17's second-line ceiling.");

    /// <summary>
    /// Changes position-processor-svc made to the R-08 candidate pool, tagged <c>change</c> —
    /// <c>added</c>, <c>moved</c> or <c>removed</c>.
    /// </summary>
    /// <remarks>
    /// A sustained <c>removed</c> rate with no matching <c>added</c> is drivers ageing out of the
    /// hot index faster than they are being put back, which shows up to a passenger as "no drivers
    /// available" with a full car park outside.
    /// </remarks>
    public static readonly Counter<long> DriverPoolChanges =
        Meter.CreateCounter<long>("mageride.dispatch.pool_changes", "{change}",
            "Drivers added to, moved within or removed from the R-08 candidate index.");

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

    // --- persistence-writer-svc (C040): the durable write path (T-06, T-10) ----------------------
    // ADD §9.5's write path is a batch pipeline, so the signals that matter are throughput, the
    // ratio that says how much of it is replay, and the two things that mean it has fallen behind.

    /// <summary>Rows the <c>telemetry.positions</c> hypertable accepted.</summary>
    public static readonly Counter<long> TelemetryRowsWritten =
        Meter.CreateCounter<long>("mageride.telemetry.rows_written", "{row}",
            "Position rows COPYed into the telemetry.positions hypertable.");

    /// <summary>
    /// Rows the replay-idempotency index refused (T-05/R-17).
    /// </summary>
    /// <remarks>
    /// Healthy at a low rate — it is <c>ux_positions_vehicle_seq</c> doing its job on a redelivered
    /// batch. A sustained high ratio to <see cref="TelemetryRowsWritten"/> means a device is
    /// re-sending a backlog the platform already has, which is a tracker firmware problem.
    /// </remarks>
    public static readonly Counter<long> TelemetryRowsDeduped =
        Meter.CreateCounter<long>("mageride.telemetry.rows_deduped", "{row}",
            "Position rows the (vehicle_id, seq, sample_ts) unique index rejected as replays.");

    /// <summary>Rows Postgres refused on their own merits, sent to the DLQ.</summary>
    /// <remarks>
    /// Should be zero: C039 refuses everything implausible upstream, so a row this table still
    /// rejects is a producer that has changed shape. Any non-zero reading is worth a page.
    /// </remarks>
    public static readonly Counter<long> TelemetryRowsDeadLettered =
        Meter.CreateCounter<long>("mageride.telemetry.rows_dead_lettered", "{row}",
            "Position rows the system of record refused, published to telemetry.normalized.dlq.");

    /// <summary>Rows written to <c>trips.position_samples</c> — ADD §9.2's 1/min downsample.</summary>
    public static readonly Counter<long> OperationalSamplesWritten =
        Meter.CreateCounter<long>("mageride.telemetry.operational_samples", "{row}",
            "1/min operational position samples written for Mode A/B sessions.");

    /// <summary>Trip summaries written, tagged <c>geometry_source</c> (ADD §9.2).</summary>
    /// <remarks>
    /// The tag is the useful part: a rising <c>operational</c> or <c>none</c> share means summaries
    /// are being computed after the raw chunks were dropped, or for sessions that produced no fixes
    /// at all — both of which make the distance figure something different from what it usually is.
    /// </remarks>
    public static readonly Counter<long> TripSummariesWritten =
        Meter.CreateCounter<long>("mageride.trips.summaries", "{summary}",
            "Per-session trip summaries computed and stored.");

    /// <summary>
    /// Batch write time, in milliseconds — the <c>COPY</c>, the insert and the downsample together.
    /// </summary>
    public static readonly Histogram<double> TelemetryFlushLatencyMs =
        Meter.CreateHistogram<double>("mageride.telemetry.flush.latency", "ms",
            "Time to COPY one batch into the hypertable and apply it.");

    /// <summary>
    /// Flushes that threw and were retried with the offsets left uncommitted.
    /// </summary>
    /// <remarks>
    /// Nothing is lost when this rises — the backlog sits on <c>telemetry.normalized</c>, which is
    /// retained seven days — but it is the signal that the system of record has stopped keeping up,
    /// and the fence C040 is written against ("a slow or failed write must not affect the live map")
    /// is only honoured for as long as that retention lasts.
    /// </remarks>
    public static readonly Counter<long> TelemetryFlushFailures =
        Meter.CreateCounter<long>("mageride.telemetry.flush.failures", "{failure}",
            "Batch writes that failed and were retried.");

    /// <summary>
    /// Polls the writer declined because its in-process buffer was full.
    /// </summary>
    /// <remarks>
    /// The deliberate back-pressure of the "degrade by buffering" fence: the writer stops consuming
    /// rather than growing without bound, so the buffer of record is Redpanda. A non-zero reading
    /// means the database is not keeping up with ingest.
    /// </remarks>
    public static readonly Counter<long> TelemetryWriterStalls =
        Meter.CreateCounter<long>("mageride.telemetry.writer.stalls", "{stall}",
            "Consume polls skipped because the writer's buffer was at its ceiling.");

    /// <summary>Rows buffered in the writer awaiting a flush, as an observable gauge.</summary>
    public const string TelemetryWriterBacklogGauge = "mageride.telemetry.writer.backlog";

    /// <summary>Vehicle frames pushed to SignalR geocell groups.</summary>
    public static readonly Counter<long> FanoutFramesSent =
        Meter.CreateCounter<long>("mageride.fanout.frames", "{frame}", "Vehicle frames pushed to geocell groups.");

    /// <summary>
    /// Cell stream entry to SignalR send, in milliseconds — the fan-out half of the SLO.
    /// </summary>
    public static readonly Histogram<double> FanoutLatencyMs =
        Meter.CreateHistogram<double>("mageride.fanout.latency", "ms",
            "Time from a cell stream entry being written to it being pushed to a group.");

    /// <summary>
    /// GNSS capture instant to the frame reaching a passenger's group, in milliseconds — the whole
    /// of ADD §13.3's position SLO (D-19: p95 &lt; 5 s, p99 &lt; 8 s) in one histogram.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C119 added this because the SLO was not measurable before it.
    /// <see cref="PositionIngestLatencyMs"/> ends where <see cref="FanoutLatencyMs"/> begins and
    /// the two together do cover the path — but a p95 is not additive, so
    /// <c>p95(ingest) + p95(fanout)</c> is a number with no defined relationship to the p95 an SLO
    /// is written about. Only a histogram over the *same* observation can answer it, and this is
    /// the only point in the platform that holds both ends: the sample carries its own capture
    /// instant all the way to the push.
    /// </para>
    /// <para>
    /// Measured against the device's clock, like <see cref="PositionIngestLatencyMs"/> and for the
    /// same reason — it is the delay the passenger experiences, and a handset with a wrong clock is
    /// one of the ways that delay goes wrong. ADD §13.2's synthetic probe (a probe vehicle
    /// publishing to a probe passenger) is the clock-independent counterpart and is what
    /// <c>infra/observability</c>'s blackbox job is for.
    /// </para>
    /// </remarks>
    public static readonly Histogram<double> PositionEndToEndLatencyMs =
        Meter.CreateHistogram<double>("mageride.positions.e2e.latency", "ms",
            "Device GNSS instant to the position reaching a subscribed client (D-19).");

    /// <summary>The three ways a position reaches a person, as the <c>surface</c> tag's closed set.</summary>
    public static class PositionSurfaces
    {
        /// <summary>The public map's geocell groups.</summary>
        public const string Geocell = "geocell";

        /// <summary>A passenger watching their own ride.</summary>
        public const string Ride = "ride";

        /// <summary>An entitled Mode B watcher following one vehicle (D-23).</summary>
        public const string Vehicle = "vehicle";
    }

    /// <summary>
    /// Records one D-19 observation. The single implementation of the clamp, the tag and the
    /// null-guard, because three call sites across two pumps measure the same journey.
    /// </summary>
    /// <param name="captured">
    /// The sample's own GNSS instant. <c>null</c> contributes nothing rather than a zero, which
    /// would drag the quantile down.
    /// </param>
    /// <param name="delivered">When the frame left for the subscriber.</param>
    /// <param name="surface">One of <see cref="PositionSurfaces"/>.</param>
    public static void RecordPositionE2E(DateTimeOffset? captured, DateTimeOffset delivered, string surface)
    {
        if (captured is not { } stamped)
        {
            return;
        }

        PositionEndToEndLatencyMs.Record(
            Math.Max(0, (delivered - stamped).TotalMilliseconds),
            new KeyValuePair<string, object?>("surface", surface));
    }

    /// <summary>
    /// Frames the D-22/D-23 visibility filter kept off the public geocell groups, by reason
    /// (<c>engaged</c> | <c>stale</c> | <c>offline</c> | <c>private</c>).
    /// </summary>
    /// <remarks>
    /// The one number that says the filter is doing anything. A working filter and an absent filter
    /// look identical from a passenger's map — vehicles appear, positions move — and the difference
    /// only shows when somebody who should not see a vehicle does. A reading of zero <c>engaged</c>
    /// on a platform with rides in progress is the alarm (C041).
    /// </remarks>
    public static readonly Counter<long> FanoutFramesFiltered =
        Meter.CreateCounter<long>("mageride.fanout.filtered", "{frame}",
            "Vehicle frames withheld from public geocell groups, by reason.");

    /// <summary>
    /// <c>share.revoked</c> arriving at fanout-svc to the affected passenger being out of the
    /// vehicle's group, in milliseconds. D-22's budget is 200 ms.
    /// </summary>
    public static readonly Histogram<double> FanoutRevocationMs =
        Meter.CreateHistogram<double>("mageride.fanout.revocation", "ms",
            "Mode B revocation to directed group removal (D-22, budget 200 ms).");

    /// <summary>Directed sends applied from the Redis control channel, by kind.</summary>
    public static readonly Counter<long> FanoutSignalsApplied =
        Meter.CreateCounter<long>("mageride.fanout.signals", "{signal}",
            "Directed fan-out signals applied to this replica's local connections.");

    // --- fleet-health-svc (C044): the device-health plane (T-04, T-10, US-3.13/3.16) --------------
    // These four are cross-cutting in the same sense the rest of this file is: the tracker plane's
    // health is what an operator's page rule is written against, and D7' §12 scrapes this meter by
    // name. The per-state gauge is the one a dashboard draws; the two counters either side of it are
    // what say whether the *alerting* half is alive, which a gauge sitting at zero cannot.

    /// <summary>
    /// Devices in each US-3.13 state, tagged <c>state</c> and <c>fleet_id</c>, as an observable
    /// gauge.
    /// </summary>
    /// <remarks>
    /// Published by fleet-health-svc's transition sweep, so it moves at the sweep's cadence and not
    /// per sample. A whole fleet arriving in <c>offline</c> at once is the SIM-provider outage
    /// US-3.16 exists to catch, and it is visible here a window before the alert fires.
    /// </remarks>
    public const string DeviceHealthGauge = "mageride.fleet.device_health";

    /// <summary>
    /// Devices whose health state changed, tagged <c>from</c> and <c>to</c>.
    /// </summary>
    /// <remarks>
    /// The transition and not the level, because "40 devices are stale" is a fact about last week
    /// and "40 devices went stale in the last minute" is an incident. A sustained
    /// <c>online→stale</c> rate with no matching <c>stale→online</c> is a fleet going dark.
    /// </remarks>
    public static readonly Counter<long> DeviceHealthTransitions =
        Meter.CreateCounter<long>("mageride.fleet.device_transitions", "{device}",
            "Tracker health-state changes fleet-health-svc recorded, by from-state and to-state.");

    /// <summary>Device-health rows written, tagged <c>input</c> — <c>ping</c>, <c>status</c>, <c>diag</c> or <c>binding</c>.</summary>
    /// <remarks>
    /// A reading of zero on <c>ping</c> while <see cref="PositionsProcessed"/> is non-zero means the
    /// health plane has stopped consuming <c>telemetry.normalized</c> — which looks exactly like a
    /// fleet that has parked for the night.
    /// </remarks>
    public static readonly Counter<long> DeviceHealthUpdates =
        Meter.CreateCounter<long>("mageride.fleet.device_updates", "{update}",
            "Device-health rows written by fleet-health-svc, by which input produced them.");

    /// <summary>
    /// Device-down threshold alerts raised, tagged <c>fleet_id</c> — one per fleet per window
    /// (US-3.16).
    /// </summary>
    /// <remarks>
    /// Counted only when the <c>ux_fleet_health_alert_window</c> insert actually claimed the window,
    /// so the number is alerts *raised* and not windows evaluated: every replica evaluates every
    /// window and exactly one of them wins.
    /// </remarks>
    public static readonly Counter<long> FleetHealthAlerts =
        Meter.CreateCounter<long>("mageride.fleet.health_alerts", "{alert}",
            "Fleet device-down threshold alerts raised (US-3.16).");

    // --- query-svc (C042): the read side of the same two planes -----------------------------------

    /// <summary>Vehicles <c>GET /v1/nearby</c> returned.</summary>
    public static readonly Counter<long> NearbyVehiclesReturned =
        Meter.CreateCounter<long>("mageride.query.nearby.vehicles", "{vehicle}",
            "Vehicles included in a nearby snapshot after filtering.");

    /// <summary>
    /// Vehicles the visibility filter withheld from a nearby snapshot, by the same reasons
    /// <see cref="FanoutFramesFiltered"/> uses, plus <c>unknown</c> (no <c>veh:meta</c> at all) and
    /// <c>out_of_radius</c> (the exact post-filter).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comparable with <see cref="FanoutFramesFiltered"/> on purpose: the socket and the snapshot
    /// apply one rule (<see cref="Realtime.VehicleVisibilityRules"/>) and their <c>engaged</c>,
    /// <c>stale</c>, <c>offline</c> and <c>private</c> rates should move together. They diverging is
    /// the signal that one plane is reading state the other is not.
    /// </para>
    /// <para>
    /// <c>unknown</c> and <c>out_of_radius</c> are this endpoint's alone and both count the cost of
    /// <c>geo:live</c> having no expiry: nothing removes a member, so the index is a superset of the
    /// live fleet and every candidate has to be re-checked against its own current position.
    /// </para>
    /// </remarks>
    public static readonly Counter<long> NearbyVehiclesFiltered =
        Meter.CreateCounter<long>("mageride.query.nearby.filtered", "{vehicle}",
            "Vehicles withheld from a nearby snapshot, by reason.");

    /// <summary>
    /// Nearby snapshots served without the live index — ADD §12's <c>limited_live</c> degradation.
    /// </summary>
    /// <remarks>
    /// A reading above zero means passengers are being shown an empty or partial map with a flag
    /// rather than an error. That is the prescribed behaviour and it is still an outage.
    /// </remarks>
    public static readonly Counter<long> NearbyLimitedLive =
        Meter.CreateCounter<long>("mageride.query.nearby.limited_live", "{snapshot}",
            "Nearby snapshots served in the degraded limited-live mode (Redis unreachable).");

    /// <summary>Forward and reverse geocodes, tagged <c>op</c> and <c>outcome</c>.</summary>
    /// <remarks>
    /// The <c>outcome=cache_hit</c> share is the number that matters: a self-hosted Nominatim on its
    /// own VPS (ADD §6) is the slowest dependency on the passenger's search path, and the cache is
    /// the whole of D-15's mitigation for it.
    /// </remarks>
    public static readonly Counter<long> GeocodeRequests =
        Meter.CreateCounter<long>("mageride.query.geocode.requests", "{request}",
            "Forward and reverse geocode lookups, by operation and outcome.");

    /// <summary>Read queries served by a replica rather than the primary (ADD §9.3).</summary>
    public static readonly Counter<long> QueryReplicaReads =
        Meter.CreateCounter<long>("mageride.query.replica_reads", "{query}",
            "Read queries routed to a Postgres read replica, by whether read-after-write forced the primary.");

    // --- safety-svc (C052): the one SLO that pages on a single window (D-33) ----------------------

    /// <summary>
    /// Button tap to the alert being handed to a gateway, in milliseconds, tagged <c>outcome</c> —
    /// ADD §13.3's SOS dispatch SLO (D-33: p99 ≤ 5 s, and any 5-minute window over it pages).
    /// </summary>
    /// <remarks>
    /// <para>
    /// C119 added this. safety-svc already measured the interval — <c>safety.sos_events.ts</c> is
    /// the tap and <c>dispatched_at</c> is the gateway, deliberately two columns so the SLO
    /// survives the request — but a column is queryable *after* an incident and D-33's alert has to
    /// fire *during* one. The row stays the system of record; this is the same interval where
    /// Prometheus can watch it.
    /// </para>
    /// <para>
    /// The <c>outcome</c> tag carries <c>dispatched</c>, <c>failed</c> or <c>no_contact</c> and the
    /// SLO is computed over <c>dispatched</c> alone: an alert that failed at the gateway in 200 ms
    /// is a worse outcome than one that took six seconds and arrived, and averaging them together
    /// would let a rising failure rate pull the latency graph *down*. The failures have their own
    /// alert.
    /// </para>
    /// </remarks>
    public static readonly Histogram<double> SosDispatchLatencyMs =
        Meter.CreateHistogram<double>("mageride.sos.dispatch.latency", "ms",
            "SOS button tap to the alert being handed to the primary SMS gateway (D-33).");

    // --- The two §13.3.1 rows that are not about a ride (C119) -------------------------------
    // Each is published by the service that owns the table, through the kernel's ScrapedGauges —
    // fare-svc's OverpaidGauge and registry-svc's ExpiredDocumentsGauge. Both were briefly in the
    // analytics read model on the theory that they spanned two bounded contexts; neither does, and
    // that read model's only host (admin-bff) is not a scrape target in the deployment shape the
    // code is actually built in, so the pages would have been silent there.

    /// <summary>
    /// Payments sitting in <c>Overpaid</c>. ADD §13.3.1 pages when this is non-zero for over an
    /// hour — a late-callback storm, or OnePay reconciliation stalled (R-19, ADD §11.14).
    /// </summary>
    public const string PaymentsOverpaidGauge = "mageride.payments.overpaid";

    /// <summary>
    /// Vehicles past a document's expiry whose <c>registry.vehicles.dispatch_state</c> is still
    /// <c>ACTIVE</c>. ADD §13.3.1's last row; the cause it names is "doc-expiry job not running",
    /// and that column is the one the job writes.
    /// </summary>
    public const string ExpiredDocumentsDispatchingGauge = "mageride.registry.expired_documents_dispatching";

    /// <summary>SOS alerts raised, tagged <c>outcome</c> and <c>source</c>.</summary>
    /// <remarks>
    /// The denominator D-33's 99.9% is measured against, and the one number that says the panic
    /// button is reaching anybody at all — safety-svc's own CLAUDE.md puts it plainly: an SOS that
    /// goes nowhere looks exactly like one that worked.
    /// </remarks>
    public static readonly Counter<long> SosRaised =
        Meter.CreateCounter<long>("mageride.sos.raised", "{alert}",
            "SOS alerts recorded, by dispatch outcome and by where they were raised from.");
}

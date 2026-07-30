using System.ComponentModel.DataAnnotations;

namespace MageRide.HotPath.PersistenceWriter.Configuration;

/// <summary>
/// How the durable write path batches, downsamples and summarises
/// (<c>PersistenceWriter</c> section).
/// </summary>
/// <remarks>
/// D7' §4.2 gives this service exactly two settings — <c>Timescale__BatchRows</c>=1000 and
/// <c>Timescale__FlushMs</c>=500 — under a <c>Timescale</c> prefix. They are bound here as
/// <see cref="BatchRows"/> and <see cref="FlushInterval"/> under <c>PersistenceWriter</c> instead,
/// so that one section holds everything this service reads rather than splitting it across two
/// prefixes; the D7' names are accepted as aliases in <c>infra/env/.env.app.example</c>. Recorded as
/// a micro-change-set in the C040 handoff.
/// </remarks>
public sealed class PersistenceWriterOptions
{
    public const string SectionName = "PersistenceWriter";

    /// <summary>Runs the <c>telemetry.normalized</c> batch writer in this process.</summary>
    /// <remarks>
    /// Off in tests that drive the writer directly, so a background consumer cannot race an
    /// assertion about what a flush wrote.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Consumer group for <c>telemetry.normalized</c> (D6' §2: "consumer group per service").</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "persistence-writer-svc";

    /// <summary>Consumer group for <c>trip.events</c>, which drives the trip summary.</summary>
    [Required]
    public string TripConsumerGroup { get; set; } = "persistence-writer-svc-trips";

    /// <summary>Runs the <c>trip.events</c> consumer that writes trip summaries.</summary>
    public bool SummariesEnabled { get; set; } = true;

    /// <summary>
    /// Rows per <c>COPY</c> batch — D7' §4.2's <c>Timescale__BatchRows</c>, ADD §9.5 item 5.
    /// </summary>
    [Range(1, 100_000)]
    public int BatchRows { get; set; } = 1_000;

    /// <summary>
    /// How long a partially-filled batch waits — D7' §4.2's <c>Timescale__FlushMs</c>.
    /// </summary>
    /// <remarks>
    /// The deadline runs from the batch's <i>first</i> buffered row, not from the last flush: a
    /// vehicle reporting once every thirty seconds must not have its sample held for as long as the
    /// partition keeps receiving other traffic. Half a second is the ADD's number and is also what
    /// keeps the <c>received_ts</c>-to-committed lag inside a second at any realistic rate.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Total rows buffered across all partitions before the consumer stops polling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the "degrade by buffering" fence, and the buffer is Redpanda — not this
    /// process.</b> When Postgres is slow or down, flushes fail, offsets are not committed and the
    /// in-process buffer fills; at this ceiling the loop stops consuming entirely and the backlog
    /// accumulates on the topic, which D6' §2.1 retains for seven days. An unbounded in-memory queue
    /// would instead turn a database outage into an OOM kill, and the pod restart would then replay
    /// from the last committed offset anyway — so the ceiling costs nothing and removes a failure
    /// mode.
    /// </para>
    /// <para>
    /// Ten batches' worth. Large enough that a flush never starves the loop, small enough that the
    /// resident set is bounded by something an operator can read off a setting.
    /// </para>
    /// </remarks>
    [Range(1, 1_000_000)]
    public int MaxBufferedRows { get; set; } = 10_000;

    /// <summary>How long a failing flush waits before retrying, doubling up to the maximum.</summary>
    /// <remarks>
    /// The batch is retried in place and the offsets stay uncommitted, so nothing is lost to a
    /// failure however long it lasts. A crash mid-retry replays from the last committed offset and
    /// <c>ux_positions_vehicle_seq</c> discards what was already written (T-05/R-17).
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling on the retry backoff.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Send rows Postgres refuses on their own merits to <c>telemetry.normalized.dlq</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A batch that fails on a <i>data</i> error — a CHECK violation, a bad numeric — will fail on
    /// every retry, and retrying it forever stalls the partition every other vehicle in the shard
    /// shares. So the batch is re-attempted row by row to find which rows are poison; those go to
    /// the DLQ with the reason attached and the rest are written.
    /// </para>
    /// <para>
    /// Off means a poison row stalls its partition instead, which is loud rather than lossy — the
    /// kernel's <c>KafkaTopicConsumer</c> behaviour, and the right choice for a deployment that would
    /// rather stop than lose a sample. On by default because a position is not a booking: C039
    /// already refused everything implausible upstream, so a row Postgres still rejects here is a
    /// producer bug, and a bug in one vehicle's firmware must not stop a shard.
    /// </para>
    /// </remarks>
    public bool DeadLetterEnabled { get; set; } = true;

    // --- The 1/min operational downsample (ADD §9.2) ---------------------------------------------

    /// <summary>Write the 1/min Mode A/B sample into <c>trips.position_samples</c>.</summary>
    public bool OperationalSamplingEnabled { get; set; } = true;

    /// <summary>
    /// The operational sample's period — ADD §9.2's "1/min sampled".
    /// </summary>
    /// <remarks>
    /// Also the bucket each row's <c>sample_ts</c> is aligned to, which is what makes the write
    /// idempotent against <c>ux_possample_session_minute</c> without per-vehicle state. Changing it
    /// changes the meaning of rows already stored, so it is here to be read rather than tuned.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan SamplePeriod { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a vehicle's tracking-session lookup is cached, including a miss.
    /// </summary>
    /// <remarks>
    /// <b>Caching the miss is the point.</b> Every Mode C vehicle on the platform publishes on this
    /// topic and none of them has a tracking session (R-01), so an uncached negative would put a
    /// query per vehicle per batch on the hot side of the write path for an answer that is always
    /// no. Thirty seconds bounds how late a freshly started session's first sample can be —
    /// at most one sample, and its minute bucket is written by the next one.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan SessionCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a vehicle's fleet membership is cached.</summary>
    /// <remarks>
    /// <c>mqtt-topics.md</c> §6: "<c>fleetId</c> is denormalised onto the sample at write time so a
    /// fleet-scoped read needs no join. <b>C040 must populate it.</b>" A vehicle joins or leaves a
    /// fleet by an admin action measured in months, so ten minutes is generous; a vehicle that
    /// changes fleet keeps its old rows under the old fleet, which C006 decision 8 already settled
    /// as correct for an audit trail.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "24:00:00")]
    public TimeSpan FleetCacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Entries either lookup cache holds before the oldest are dropped.</summary>
    [Range(64, 1_000_000)]
    public int LookupCacheCapacity { get; set; } = 50_000;

    // --- The trip summary (ADD §9.2, §9.5 item 2) ------------------------------------------------

    /// <summary>
    /// Douglas-Peucker tolerance for the stored polyline, in metres.
    /// </summary>
    /// <remarks>
    /// <b>No spec names it.</b> A two-hour Mode A journey at D5' §5.2's 5–10 s standby cadence is
    /// well over a thousand fixes, and a passenger looking at a trip on a phone map cannot see
    /// twenty metres. Twenty-five metres matches D5' §5.2's own <c>Δpos &lt; 25 m</c> coalescing
    /// threshold — the distance the platform has already decided is not worth transmitting — so the
    /// simplification throws away exactly what the cadence rule would have. The <i>distance</i> is
    /// computed before simplification and is unaffected.
    /// </remarks>
    [Range(0d, 1_000d)]
    public double PolylineToleranceM { get; set; } = 25;

    /// <summary>
    /// Fall back to the 1/min operational samples when the full-resolution rows are gone.
    /// </summary>
    /// <remarks>
    /// ADD §9.5 item 4 drops raw chunks after 30 days. A summary written on <c>session.ended</c>
    /// always finds them, so this only matters for a replayed or manually re-run event — and a
    /// summary whose distance is a lower bound, labelled <c>operational</c>, is better than no
    /// summary at all. Off means such a session gets <c>geometry_source = 'none'</c>.
    /// </remarks>
    public bool AllowOperationalGeometryFallback { get; set; } = true;
}

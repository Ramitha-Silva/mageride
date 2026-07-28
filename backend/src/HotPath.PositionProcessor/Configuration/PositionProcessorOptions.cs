using System.ComponentModel.DataAnnotations;

namespace MageRide.HotPath.PositionProcessor.Configuration;

/// <summary>
/// What the processor consumes and how long the live indexes remember
/// (<c>PositionProcessor</c> section).
/// </summary>
public sealed class PositionProcessorOptions
{
    public const string SectionName = "PositionProcessor";

    /// <summary>
    /// Runs the <c>telemetry.raw</c> consumer in this process. Off in tests that drive
    /// <c>IPositionProcessor</c> directly, so a background consumer cannot race an assertion.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Consumer group for <c>telemetry.raw</c> (D6' §2: "consumer group per service").</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "position-processor-svc";

    /// <summary>
    /// Where a consumer group with no committed offset starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off, and that is the opposite of every other consumer on the platform.</b> dispatch-svc
    /// reads <c>ride.events</c> from the earliest offset because a booking committed while it was
    /// down still has to be dispatched. A position does not work that way: this is a
    /// <i>current-state</i> index, and a processor that woke up and replayed ten minutes of stale
    /// samples would write every one of them to <c>geo:live</c> and push them to passengers as
    /// though they were current — oldest last. History is Timescale's (T-06); the live map's
    /// recovery path is the next sample from each vehicle, seconds away.
    /// </para>
    /// <para>
    /// Turn it on to replay a backlog deliberately — a fresh environment being seeded, or a test
    /// that needs the pipeline to be deterministic rather than racing the consumer's group
    /// assignment. It only affects a group that has never committed; an existing group resumes
    /// where it was either way, and the <c>seq</c> watermark discards whatever it re-reads.
    /// </para>
    /// </remarks>
    public bool StartFromEarliest { get; set; }

    /// <summary>
    /// Republish each normalised sample onto <c>telemetry.normalized</c>.
    /// </summary>
    /// <remarks>
    /// D6' §2.1 registers persistence-writer, trip-state and fleet-health as its consumers. None of
    /// them exists yet, so in this slice the topic is written and nothing reads it — which is the
    /// right way round: C040 should find the data already there rather than have to change this
    /// service to get it.
    /// </remarks>
    public bool PublishNormalized { get; set; } = true;

    /// <summary>
    /// How long the <c>veh:seq:{vehicleId}</c> replay watermark is kept.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins this.</b> <c>mqtt-topics.md</c> §5 gives the tracker a 50,000-sample flash
    /// ring and no expiry for the watermark. 24 hours is chosen to outlast any plausible offline
    /// stretch — a vehicle that has been dark longer than a day has nothing worth deduping, and
    /// layer 3 (<c>ux_positions_vehicle_seq</c>) catches an exact duplicate regardless. Recorded as
    /// a gap in the C024 handoff.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan SeqWatermarkTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long <c>veh:meta:{vehicleId}</c> survives without a fresh sample.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan VehicleMetaTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Approximate cap on a <c>cell:{h3index}</c> stream.
    /// </summary>
    /// <remarks>
    /// The stream is a fan-out buffer, not a record: Timescale is the system of record (T-06,
    /// ADD §9.5) and a passenger who reconnects resyncs from <c>GET /v1/nearby</c>, not from here
    /// (<c>signalr-hub.md</c> §1.1). Trimmed with <c>MAXLEN ~</c> so Redis can trim on whole nodes
    /// rather than walking the stream on every write.
    /// </remarks>
    [Range(16, 100_000)]
    public int CellStreamMaxLength { get; set; } = 1_000;

    /// <summary>
    /// How long a <c>cell:{h3index}</c> stream is kept after its last write.
    /// </summary>
    /// <remarks>
    /// Sri Lanka is roughly 2,500 res-7 cells, but the key space is global and a stream nothing has
    /// written to in an hour is a cell no vehicle is in. Without this the keyspace only ever grows.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan CellStreamTtl { get; set; } = TimeSpan.FromHours(1);
}

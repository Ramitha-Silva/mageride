using System.ComponentModel.DataAnnotations;

namespace MageRide.HotPath.PositionProcessor.Configuration;

/// <summary>
/// What the processor consumes, what it refuses, and how long the live indexes remember
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
    /// D6' §2.1 registers persistence-writer, trip-state and fleet-health as its consumers; C034
    /// added dispatch-svc, which keeps <c>dispatch.driver_presence</c> at the driver's live position
    /// off the same topic. Turning this off stops all four.
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
    /// <remarks>
    /// Also the horizon of the D-18 plausibility filter: the last accepted position it measures a
    /// step against is read from this hash, so a vehicle that has been silent longer than this gets
    /// its next sample accepted unchecked. That is the right way round — the alternative is
    /// measuring a step over an unknown gap — and it is why this is comfortably longer than any
    /// cadence in D5' §5.2.
    /// </remarks>
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

    // --- D-18 / T-07 plausibility (D5' §13.1, ADD §12.6) -----------------------------------------

    /// <summary>Run the D-18/T-07 plausibility filter.</summary>
    /// <remarks>
    /// Off means every gate below passes and a spoofed position reaches the live map. It exists so
    /// the decision is visible in configuration rather than as a commented-out call, and
    /// <c>PositionProcessorApplication</c> says so once, loudly, at start-up.
    /// </remarks>
    public bool PlausibilityEnabled { get; set; } = true;

    /// <summary>
    /// Per-vehicle-type ceiling in km/h, the ADD §12.6 anti-spoof table (D-18).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are configuration, not constants</b> — this component's first fence. Bound from
    /// <c>PositionProcessor:MaxSpeedKph:{vehicleType}</c>, so an operator retuning a tier after a
    /// month of false positives changes a setting rather than a build. The values seeded here are
    /// ADD §12.6's verbatim: Bus 120, Sedan 180, Mini Van 140, Van 130, Flex 200, Three-wheeler 80,
    /// Motorbike 180.
    /// </para>
    /// <para>
    /// <b>Three of registry's ten types are missing from that table</b> — <c>truck</c>,
    /// <c>mini_truck</c> and <c>train</c> (<c>0303__registry_vehicles.sql</c>). They are left out
    /// rather than guessed at; they fall to <see cref="DefaultMaxSpeedKph"/>. Raised as a
    /// micro-change-set in the C039 handoff.
    /// </para>
    /// <para>
    /// Keys are the canonical snake-case type names the sample carries, compared ordinally — a
    /// mismatch here is a tier whose ceiling silently becomes the default, so the comparison is not
    /// made case-insensitive to paper over a wrong key.
    /// </para>
    /// </remarks>
    public Dictionary<string, double> MaxSpeedKph { get; set; } = new(StringComparer.Ordinal)
    {
        ["bus"] = 120,
        ["sedan"] = 180,
        ["mini_van"] = 140,
        ["van"] = 130,
        ["flex"] = 200,
        ["three_wheeler"] = 80,
        ["motorbike"] = 180,
    };

    /// <summary>
    /// Ceiling for a sample whose <c>vehicleType</c> is absent or is not in
    /// <see cref="MaxSpeedKph"/>.
    /// </summary>
    /// <remarks>
    /// 200 km/h — the <i>most permissive</i> value in ADD §12.6's own table, deliberately. A type
    /// the spec does not price should never be rejected by a number nobody wrote; the absolute
    /// <see cref="MaxJumpSpeedKph"/> backstop is what still catches a teleport on those tiers.
    /// </remarks>
    [Range(1d, 5_000d)]
    public double DefaultMaxSpeedKph { get; set; } = 200;

    /// <summary>
    /// The absolute teleport backstop — ADD §12.6's "jump &lt; 1 km/s", in km/h.
    /// </summary>
    /// <remarks>
    /// 3,600 km/h. Above every per-type ceiling on purpose: it is the bound that still applies when
    /// the type is unknown, and it is reported under its own <c>jump</c> tag so a fleet failing it
    /// is distinguishable from one merely driving too fast for its tier.
    /// </remarks>
    [Range(1d, 100_000d)]
    public double MaxJumpSpeedKph { get; set; } = 3_600;

    /// <summary>
    /// The shortest gap an implied speed is computed over. Shorter gaps are judged as if they were
    /// this long.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No spec names it, and without it the teleport gate is unusable.</b> An implied speed is a
    /// distance over a time, and over a very short time the distance is dominated by the fix's own
    /// error circle rather than by motion: 10 m of ordinary GNSS jitter across 100 ms implies
    /// 360 km/h. Worse, most trackers stamp <c>sampleTs</c> to a whole second, so two fixes 200 ms
    /// apart arrive bearing the <i>same</i> instant and there is no gap to divide by at all.
    /// </para>
    /// <para>
    /// One second, because that is the fastest cadence the platform ever asks for — D5' §5.2's
    /// near-geofence burst, AL-12's "1 call/s within 150 m of pickup". Two fixes closer together
    /// than the fastest rate the server requests are not two observations of a journey.
    /// </para>
    /// <para>
    /// It is a <i>clamp</i>, not a skip: a genuine teleport published as two same-instant samples is
    /// still judged, at this interval, and still fails. Skipping would hand a spoofer the gate.
    /// </para>
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan MinStepInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reported horizontal accuracy above which a sample is discarded, in metres (D-18).
    /// </summary>
    /// <remarks>
    /// <b>Discarded, not smoothed</b> — this component's second fence, and D-18 replacing NY's
    /// arbitrary 200 km/h with no accuracy filter at all. A fix with a 500 m error circle is not a
    /// worse position, it is a different building; averaging it into a track moves a vehicle
    /// somewhere it has never been.
    /// </remarks>
    [Range(1d, 100_000d)]
    public double MaxAccuracyM { get; set; } = 200;

    /// <summary>
    /// Minimum satellites in a <b>hardware</b> fix (T-07). Ignored when the device reports none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four is the number of satellites a 3-D fix needs; below it the receiver is extrapolating.
    /// <b>No spec pins the value</b> — D5' §13.1 asks for "minimum satellite count" and gives none.
    /// Raised in the C039 handoff.
    /// </para>
    /// <para>
    /// Mobile samples are exempt by <c>PositionSource</c>: a handset reports a fused
    /// Wi-Fi/cell/GNSS position and <c>satCount</c> is meaningless for it, which is why D5' §13.1
    /// says "hardware <b>additionally</b>".
    /// </para>
    /// </remarks>
    [Range(0, 64)]
    public int MinSatellites { get; set; } = 4;

    /// <summary>
    /// Reject a hardware sample that reports no satellite count at all.
    /// </summary>
    /// <remarks>
    /// <b>Off.</b> <c>mqtt-topics.md</c> §2.1 makes <c>satCount</c> optional and a GT06 location
    /// frame carries no satellite count in the first place, so turning this on blinds the whole
    /// GT06 fleet — the largest tracker family in <c>mqtt-topics.md</c> §7 — rather than catching a
    /// spoofer. On for a deployment whose trackers all report it.
    /// </remarks>
    public bool RequireSatelliteCount { get; set; }

    /// <summary>
    /// How far ahead of the platform's clock a sample's GNSS instant may be before it is refused.
    /// </summary>
    /// <remarks>
    /// <b>No spec names this.</b> It is here because T-07's monotonic-clock rule has a failure mode
    /// that outlives the sample: one fix dated 2099 becomes the vehicle's watermark and every real
    /// sample after it is non-monotonic, so a single bad frame takes a tracker off the map
    /// permanently. Five minutes is generous for the NTP skew of a cheap tracker and far short of
    /// anything that could do that.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "24:00:00")]
    public TimeSpan MaxClockSkewAhead { get; set; } = TimeSpan.FromMinutes(5);

    // --- D-17 second line (D5' §5.3, mqtt-topics.md §4) ------------------------------------------

    /// <summary>Run D-17's second-line per-vehicle rate check.</summary>
    public bool RateCheckEnabled { get; set; } = true;

    /// <summary>
    /// The second-line ceiling in messages per second, averaged over
    /// <see cref="RateCheckWindow"/> (D-17).
    /// </summary>
    /// <remarks>
    /// Ten, twice the broker's five, because this is the <i>second</i> line: EMQX's per-connection
    /// limiter is the first and a device that gets past it did so by opening several sessions under
    /// one credential (<c>mqtt-topics.md</c> §4). Unlike the bridge's observation of the 5/s
    /// ceiling, breaching this one <b>drops</b> the sample — D5' §5.3 and §4 of the contract both
    /// say "drop + flag".
    /// </remarks>
    [Range(1, 10_000)]
    public int RateCeilingPerSecond { get; set; } = 10;

    /// <summary>The window the ceiling is averaged over — D5' §5.3's "per 10 s".</summary>
    /// <remarks>
    /// Averaged rather than instantaneous on purpose: D5' §5.2's near-geofence cadence is one
    /// sample a second and R-07 lets the server ask for bursts, so a per-second threshold at this
    /// level would fire on a vehicle behaving exactly as the platform told it to.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan RateCheckWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>One <c>mqtt.rate_violation</c> per vehicle per this long, cluster-wide.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RateViolationCooldown { get; set; } = TimeSpan.FromMinutes(1);

    // --- R-08 dispatch candidate index (ADD §9.4) ------------------------------------------------

    /// <summary>Keep the R-08 candidate index at the driver's live position.</summary>
    public bool AvailabilityIndexEnabled { get; set; } = true;

    /// <summary>
    /// The <c>driver:availability:{driverId}</c> TTL, refreshed on every live sample (R-08).
    /// </summary>
    /// <remarks>
    /// 60 s, ADD §9.4 verbatim, and it <b>must equal</b> dispatch-svc's <c>Dispatch:PresenceTtl</c>:
    /// that service's GPS-freshness gate is written against the same number and the two agreeing is
    /// what makes "in the hash" and "fresh enough to be offered a ride" the same fact.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan DriverAvailabilityTtl { get; set; } = TimeSpan.FromSeconds(60);
}

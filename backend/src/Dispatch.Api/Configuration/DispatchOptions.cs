using System.ComponentModel.DataAnnotations;

namespace MageRide.Dispatch.Configuration;

/// <summary>
/// Everything dispatch-svc's offer loop is tuned by (<c>Dispatch</c> section).
/// </summary>
/// <remarks>
/// The defaults are the spec's numbers wherever the spec prints one. Where it does not —
/// <see cref="SearchRadiusM"/>, <see cref="OfferReleaseGrace"/>, the three scoring weights — the
/// default is marked at its declaration and recorded as a gap in the C023/C034 handoffs.
/// </remarks>
public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    /// <summary>Ceiling on any offer window. The 15 s is a driver-facing promise, not a knob.</summary>
    public static readonly TimeSpan MaxOfferTtl = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The 15 s offer window (D5' §3.5, US-6A.3). ride-svc stamps the actual deadline from its own
    /// clock; this is what dispatch asks for and what the Redis <c>PEXPIRE</c> mirrors.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan OfferTtl { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The whole cascade's deadline — US-6A.11's "no driver after N rounds / 120 s". When it passes
    /// the ride is system-cancelled into <c>ExpiredNoDriver</c>.
    /// </summary>
    /// <remarks>
    /// <b>Two specs, two numbers.</b> D5' §3.5 and US-6A.11 say "Global timeout 2 min"; ADD §11.12's
    /// matrix row says "timeout (60 s)". 120 s wins — it is the number the user story, the business
    /// logic document and this component's Definition of Done all print, against one parenthesis in
    /// a table cell. Recorded as a micro-change-set in the C034 handoff.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:15", "00:30:00")]
    public TimeSpan GlobalTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Exact-distance post-filter radius, in metres. **No spec pins this** — D5' §3.1 writes
    /// <c>ST_DWithin(d.geo, pickup, searchRadius)</c> and never gives the value (C023 handoff gap).
    /// 5 km is chosen to sit inside the H3 res-5 ring(2) pre-filter's reach so the pre-filter is
    /// genuinely coarse, and above the ~3 km passenger live-map view (R-06).
    /// </summary>
    [Range(100, 100_000)]
    public int SearchRadiusM { get; set; } = 5_000;

    /// <summary>H3 resolution of the candidate index. ADD §9.4 keys it at res 5 (D-06, R-06).</summary>
    [Range(0, 15)]
    public int H3Resolution { get; set; } = 5;

    /// <summary>Ring size around the pickup cell. D5' §3.1: <c>ring(1..2)</c> ⇒ <c>gridDisk(k=2)</c>.</summary>
    [Range(0, 5)]
    public int H3RingK { get; set; } = 2;

    /// <summary>TTL of <c>driver:availability:{driverId}</c> (60 s, R-08 / ADD §9.4).</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan PresenceTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often a driver in the pool is expected to report a position. D5' §3.2 excludes a
    /// candidate whose "GPS sample [is] older than <c>2×expectedInterval</c>"; this is that
    /// interval and <see cref="PositionFreshnessFactor"/> is that 2.
    /// </summary>
    /// <remarks>
    /// <b>D5' §5 gives two different intervals for the same driver.</b> §5.1 says "Idle standby
    /// (Mode C) = 1 / 60 s" and §5.2's phase table repeats it ("Standby idle … 30–60 s"), while the
    /// row below it says "Candidate in pool | availability=AVAILABLE | 2–5 s | none (scoring
    /// freshness)" — and a Mode C driver on standby is *both* at once, which is what makes the two
    /// rows contradict rather than compose. The default takes §5.1's 60 s, because it is the number
    /// R-08's <c>driver:availability</c> TTL already agrees with and because taking 5 s would put
    /// the freshness bound at 10 s and exclude every driver whose app is on the standby cadence the
    /// same document asks it to use. An operator whose whole fleet is on the 2–5 s candidate cadence
    /// should set this to 5 s and get the tighter scoring freshness §5.2 wants. Micro-change-set in
    /// the C034 handoff.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan ExpectedPositionInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The multiplier in D5' §3.2's <c>2×expectedInterval</c> freshness rule.</summary>
    [Range(1, 10)]
    public int PositionFreshnessFactor { get; set; } = 2;

    /// <summary>
    /// The age at which a durable presence row stops being a candidate — D5' §3.2's GPS-freshness
    /// gate, which the Redis TTL alone cannot enforce because the durable row has no TTL.
    /// </summary>
    public TimeSpan PositionFreshness => ExpectedPositionInterval * PositionFreshnessFactor;

    /// <summary>
    /// How many offers one ride may cascade through before dispatch gives up (D5' §3.5's sequential
    /// cascade, §11.12's "no candidates after N rounds"). Reaching it ends the ride in
    /// <c>ExpiredNoDriver</c>, exactly as <see cref="GlobalTimeout"/> does.
    /// </summary>
    [Range(1, 100)]
    public int MaxOfferRounds { get; set; } = 8;

    /// <summary>How often the durable <c>rides.timers</c> backstop is swept (R-04: ≤1 s late).</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan TimerPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Rows the backstop claims per sweep.</summary>
    [Range(1, 1_000)]
    public int TimerBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a claimed timer is leased to the worker that took it. Long enough to cover a
    /// ride-svc round trip and its retry; short enough that a worker killed mid-expiry hands the
    /// row back well inside the R-20 stuck-state window.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan TimerLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the R-04 backstop sweep in this process. Off in tests that drive
    /// <c>IDispatchService</c> directly, so a background sweep cannot race the assertion.
    /// </summary>
    public bool ExpiryWorkerEnabled { get; set; } = true;

    /// <summary>
    /// Runs the <c>dispatch.timers</c> sweep — the US-6A.11 global timeout and the R-15 LWT
    /// release grace. Separate from <see cref="ExpiryWorkerEnabled"/> because they are separate
    /// tables with separate failure modes.
    /// </summary>
    public bool DispatchTimerWorkerEnabled { get; set; } = true;

    /// <summary>Consumes <c>ride.events</c>. Off where no broker is configured.</summary>
    public bool ConsumerEnabled { get; set; } = true;

    /// <summary>Consumer group for <c>ride.events</c> (D6' §2: "consumer group per service").</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "dispatch-svc";

    /// <summary>
    /// Consumes <c>telemetry.normalized</c> to keep <c>dispatch.driver_presence</c> and the R-08
    /// candidate index at the driver's live position.
    /// </summary>
    public bool PositionConsumerEnabled { get; set; } = true;

    /// <summary>
    /// Consumer group for <c>telemetry.normalized</c>. Its own, not <see cref="ConsumerGroup"/>: a
    /// group is per (service, topic) and sharing one across two topics makes a rebalance on the
    /// position firehose stall the ride stream.
    /// </summary>
    [Required]
    public string PositionConsumerGroup { get; set; } = "dispatch-svc-presence";

    /// <summary>
    /// Skip a position sample that has moved the driver less than this. D5' §5.2 coalesces on
    /// "Δpos &lt; 25 m" for the standby phases; here it saves a Postgres write and a GEOADD per
    /// sample per driver, which at 2–5 s cadence is the service's busiest write by an order of
    /// magnitude.
    /// </summary>
    /// <remarks>
    /// <c>last_seen_at</c> is refreshed either way — the freshness gate above is about *liveness*,
    /// not about movement, and a stationary driver at a rank is exactly the candidate this service
    /// most wants to keep.
    /// </remarks>
    [Range(0, 10_000)]
    public int PositionMoveThresholdM { get; set; } = 25;

    /// <summary>
    /// Subscribe to Redis keyspace expiry events for <c>offer:{rideId}</c> (D-07). A fast hint
    /// only — the <c>rides.timers</c> row is what makes expiry survive a Redis flush (R-04).
    /// </summary>
    public bool KeyspaceNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Issue <c>CONFIG SET notify-keyspace-events</c> at start-up when the server is not already
    /// publishing expiry events.
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and it should usually stay off.</b> <c>CONFIG GET/SET</c> need an admin
    /// connection, which the kernel's multiplexer deliberately does not open, and a managed Redis
    /// refuses them outright — so the setting belongs in the server's own command line
    /// (<c>--notify-keyspace-events Ex</c>, which <c>infra/docker-compose.dev.slim.yml</c> sets).
    /// Turning this on makes the service try anyway and log what happened; the durable
    /// <c>rides.timers</c> backstop is unaffected either way.
    /// </remarks>
    public bool ConfigureKeyspaceNotifications { get; set; }

    /// <summary>Base address of ride-svc, e.g. <c>http://ride-svc:8080</c>.</summary>
    [Required]
    public string RideServiceBaseUrl { get; set; } = "http://ride-svc:8080";

    /// <summary>
    /// Must equal ride-svc's <c>Ride:InternalApiKey</c>. Unset means no offer can ever be placed,
    /// so <see cref="DispatchApplication"/> refuses to start the offer loop and says why.
    /// </summary>
    public string? RideServiceInternalKey { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan RideServiceTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------------------------
    // Scoring (D5' §3.3, R-11)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Version stamped on <c>dispatch.candidate_scores.dispatch_algorithm_version</c> (R-11).
    /// </summary>
    /// <remarks>
    /// <b>1 is the D5' §3.3 weighted algorithm</b>, the one C034 landed. 0 was C023's
    /// nearest-only ordering and is still what a version-0 row in the audit means; the version is
    /// on the row precisely so a decision taken under either can be reproduced from the number
    /// beside it. Bump it whenever the formula or the weights change in a way that would make an
    /// old row's breakdown irreproducible.
    /// </remarks>
    [Range(0, short.MaxValue)]
    public int AlgorithmVersion { get; set; } = 1;

    /// <summary>The three D5' §3.3 weights. Admin-config per version.</summary>
    public DispatchScoringWeights Weights { get; set; } = new();

    /// <summary>
    /// The distance at which the proximity term is worth half its maximum. D5' §3.3 writes
    /// <c>normalize(1/distanceToPickup)</c> and gives no normaliser; <c>1 / (1 + d/halfLife)</c> is
    /// the reading that keeps the term in (0,1], is monotonically decreasing in distance and does
    /// not divide by zero for a driver standing on the pickup. 1 km is chosen so the term still
    /// discriminates across the 5 km search radius rather than saturating in the first block.
    /// </summary>
    [Range(1, 100_000)]
    public int DistanceHalfLifeM { get; set; } = 1_000;

    /// <summary>
    /// R-12 Phase 2. <b>Off, and it stays off in this component</b> — D5' §3.3 says "Phase 1 =
    /// sequential matching (top-1 reserved, R-12); batch matching deferred to Phase 2", and this
    /// component's fence repeats it. The flag exists so the decision is visible in configuration
    /// rather than only in a comment; turning it on today changes nothing, because no batch
    /// matcher is wired behind it.
    /// </summary>
    public bool BatchMatchingEnabled { get; set; }

    // -------------------------------------------------------------------------------------------
    // Hard eligibility gates (D5' §3.2)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Gate candidates on reputation-svc's <c>GetBlockStatus</c> / <c>GetDriverLevel</c> (D-04,
    /// D5' §3.2).
    /// </summary>
    /// <remarks>
    /// <b>Off means the gate always opens</b> — a <c>BOOKING_DISABLED</c> or <c>DELISTED</c> driver
    /// is offered rides, and every candidate scores as Level 3. That is a deliberate configuration
    /// for a deployment with no reputation-svc, and <see cref="DispatchApplication"/> says so
    /// loudly at start-up for exactly the reason the C033 CLAUDE.md gives about its own consumer.
    /// </remarks>
    public bool ReputationGateEnabled { get; set; } = true;

    /// <summary>reputation-svc's gRPC address (D7' §4.2 <c>Grpc__ListenPort</c> = 5005).</summary>
    [Required]
    public string ReputationGrpcAddress { get; set; } = "http://reputation-svc:5005";

    /// <summary>
    /// Must equal reputation-svc's <c>Reputation:InternalApiKey</c>, presented as the
    /// <c>x-mageride-internal-key</c> metadata header until the C042 mesh lands.
    /// </summary>
    public string? ReputationInternalKey { get; set; }

    /// <summary>
    /// One candidate build asks about every candidate; a slow reputation-svc must degrade a round,
    /// not hang it.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan ReputationTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Local memo of a block status, on top of reputation-svc's own 5 s Redis cache. Sized to match
    /// it (C033's <c>BlockStatusCacheTtl</c>) so the two cannot disagree for longer than either.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan ReputationCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The D-08 pre-dispatch wallet gate: first trip of the Colombo day is free, the 2nd onwards
    /// needs <c>walletBalance ≥ dailyFee</c> (D5' §2.1).
    /// </summary>
    public bool WalletGateEnabled { get; set; } = true;

    /// <summary>TTL of the <c>wallet:bal:{driverId}</c> cache — D-08 says 5 s, debit-invalidated.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:05:00")]
    public TimeSpan WalletCacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    // -------------------------------------------------------------------------------------------
    // EMQX last will (R-15)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Hold the <c>veh/+/status</c> subscription and release a dropped driver's live offer (R-15).
    /// Off by default because it is the only part of this service that needs a broker.
    /// </summary>
    public bool LastWillEnabled { get; set; }

    /// <summary>
    /// How long a driver's EMQX session may stay dead before their live offer is released (R-15).
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> ADD's R-15 row says "releases active offer / <em>starts grace timer
    /// per ride state</em>" and R-16's four windows are all about a ride that has been *accepted*,
    /// which is ride-svc's <c>offline_grace</c> and not this. 5 s is chosen against the only clock
    /// that matters here — the offer's own 15 s window: long enough that a driver whose phone
    /// switched cell towers keeps the offer, short enough that two thirds of the window is still
    /// left for the next candidate. Recorded in the C034 handoff.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan OfferReleaseGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The service name minted into this process's MQTT session token.</summary>
    [Required]
    public string MqttServiceName { get; set; } = "dispatch";
}

/// <summary>
/// The three D5' §3.3 weights, one per term of the score.
/// </summary>
/// <remarks>
/// <b>No spec gives the values</b> — D5' §3.3 prints the formula with <c>w_dist</c>, <c>w_level</c>
/// and <c>w_cat</c> as symbols and says only that they are "versioned per
/// <c>dispatch_algorithm_version</c> (admin-config)". The defaults sum to 1 so a score is directly
/// readable as a fraction of the best possible candidate, and they are ordered the way the surrounding
/// specs argue: proximity dominates because it is what the passenger waits on and the only term the
/// exact post-filter already ranked by; Driver Level is a real but secondary preference (US-6A.2);
/// the category term is smallest because the tier is already a hard gate (§3.2) and the index key,
/// so it can only ever separate an exact match from a compatible one. Recorded in the C034 handoff.
/// </remarks>
public sealed class DispatchScoringWeights
{
    /// <summary><c>w_dist</c> — the proximity term.</summary>
    [Range(0d, 1_000d)]
    public double Distance { get; set; } = 0.60;

    /// <summary><c>w_level</c> — <c>driverLevel / 3</c> (US-6A.2).</summary>
    [Range(0d, 1_000d)]
    public double Level { get; set; } = 0.25;

    /// <summary><c>w_cat</c> — the vehicle-category match.</summary>
    [Range(0d, 1_000d)]
    public double Category { get; set; } = 0.15;
}

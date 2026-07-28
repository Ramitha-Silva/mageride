using System.ComponentModel.DataAnnotations;

namespace MageRide.Dispatch.Configuration;

/// <summary>
/// Everything dispatch-svc's offer loop is tuned by (<c>Dispatch</c> section).
/// </summary>
/// <remarks>
/// The defaults are the spec's numbers wherever the spec prints one. Where it does not —
/// <see cref="SearchRadiusM"/> — the default is marked and recorded as a gap in the C023 handoff.
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

    /// <summary>
    /// TTL of <c>driver:availability:{driverId}</c> (60 s, R-08 / ADD §9.4). Also the age at which
    /// a durable presence row stops being a candidate — D5' §3.2's GPS-freshness gate, which the
    /// Redis TTL alone cannot enforce because the durable row has no TTL.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan PresenceTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many offers one ride may cascade through before dispatch stops trying (D5' §3.5's
    /// sequential cascade). The global 120 s <c>ExpiredNoDriver</c> timeout is **not** implemented
    /// here — no route exists to write that state (C034); this bound is what stops the loop.
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

    /// <summary>Consumes <c>ride.events</c>. Off where no broker is configured.</summary>
    public bool ConsumerEnabled { get; set; } = true;

    /// <summary>Consumer group for <c>ride.events</c> (D6' §2: "consumer group per service").</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "dispatch-svc";

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

    /// <summary>
    /// Version stamped on <c>dispatch.candidate_scores.dispatch_algorithm_version</c> (R-11).
    /// **0 means "not the D5' §3.3 weighted algorithm"** — this slice orders by exact distance and
    /// nothing else. C034 lands the weighted formula at version 1 and up.
    /// </summary>
    [Range(0, short.MaxValue)]
    public int AlgorithmVersion { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace MageRide.Ride.Configuration;

/// <summary>ride-svc's own settings (D7' §4.2 <c>ride-svc</c> row).</summary>
public sealed class RideOptions
{
    public const string SectionName = "Ride";

    /// <summary>Upper bound on an offer window, so a bad <c>ttlSeconds</c> cannot pin a ride open.</summary>
    public static readonly TimeSpan MaxOfferTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long an offer stays acceptable when dispatch-svc does not say (D5' §3.5: 15 s).
    /// <c>offer_expires_at</c> is the authoritative deadline; the Redis key is dispatch-svc's fast
    /// path and its <c>rides.timers</c> <c>offer_expiry</c> row is the R-04 backstop (C023).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan OfferTtl { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Shared secret guarding <c>/v1/internal/**</c>.
    /// <para>
    /// D3' §0 puts the internal family on service-to-service mTLS (Linkerd/SPIFFE) and the API
    /// gateway already refuses the prefix at the edge (C008). Neither is a mesh: until C042 wires
    /// one, an in-cluster caller is only as authenticated as this header. **Unset means the
    /// internal routes are not mapped at all** — a deployment that forgets it gets 404s, not an
    /// open door.
    /// </para>
    /// </summary>
    public string? InternalApiKey { get; set; }

    // -----------------------------------------------------------------------------------------
    // The §11.12 matrix's money column
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The post-acceptance cancellation fee, in LKR minor units (D-05, US-6A.9: Rs 50).
    /// </summary>
    /// <remarks>
    /// Accrued, never collected here: it becomes the passenger's outstanding balance and settles
    /// against their next completed trip (D5' §7.1). ride-svc states that it is owed and to whom;
    /// the ledger entry is fare-svc's and billing's.
    /// </remarks>
    [Range(0, 1_000_000)]
    public long CancellationPenaltyMinor { get; set; } = 5_000;

    /// <summary>
    /// The rider no-show fee, in LKR minor units (§11.12: "Rs 100 (configurable)").
    /// </summary>
    [Range(0, 1_000_000)]
    public long NoShowPenaltyMinor { get; set; } = 10_000;

    /// <summary>
    /// How many consecutive post-acceptance cancellations disable booking (US-6A.10b, AL-16: 3).
    /// </summary>
    [Range(1, 100)]
    public int CancellationDisableThreshold { get; set; } = 3;

    // -----------------------------------------------------------------------------------------
    // Durable timers (R-04). Windows first, then the worker that fires them.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// How long a driver who accepted has to reach the pickup before the ride becomes
    /// <c>NoShowDriver</c> (§11.12's "rider waits, grace exceeded" row).
    /// </summary>
    /// <remarks>
    /// <b>No spec pins this.</b> §11.12 gives the row and stops at "grace exceeded"; the URD has no
    /// number either. Fifteen minutes is longer than any Colombo pickup ETA the dispatch radius can
    /// produce, so a driver stuck in traffic is not punished for traffic, and short enough that a
    /// passenger standing on a pavement is released to book again rather than left waiting on a
    /// driver who is not coming. It is deliberately several times the five-minute rider no-show
    /// window, because the two are not symmetric: the rider's clock only starts once the driver has
    /// actually arrived, and the driver's is running while they are still travelling.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "02:00:00")]
    public TimeSpan ArrivalGrace { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the rider has to appear after the driver arrives (D5' §7: "rider no-show 5min").
    /// </summary>
    /// <remarks>
    /// §11.12 adds "+ 2 SMS reminders" to the same row. The reminders are notification-svc's
    /// (C051): ride-svc has no SMS channel and D5' §14.4 routes every user-facing message through
    /// content-svc templates. <c>ride.driver_arrived</c> carries the instant this window opened,
    /// which is what a reminder schedule is built from.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan RiderNoShowGrace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a ride may sit in <c>PaymentPending</c> before it is reported stuck (R-20's
    /// <c>Completed+PaymentPending &gt; 10 min</c> row; R-16's "at-payment 10 min").
    /// </summary>
    /// <remarks>
    /// The fire <b>moves nothing</b>. No row of the §11.12 matrix authorises a state change out of
    /// <c>PaymentPending</c> on a timeout — R-05 reserves that for fare-svc reporting a terminal
    /// payment state — so what this timer produces is the ADD §13.3.1 alert, named per ride instead
    /// of as a count, plus the <c>runbooks/ride-stuck.md</c> pointer ADD §13.4 asks for.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "06:00:00")]
    public TimeSpan PaymentPendingGrace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Whether the durable-timer worker runs in this process.</summary>
    /// <remarks>
    /// On by default: the no-show and grace transitions are platform guarantees, and a deployment
    /// that silently skipped them would strand every ride whose driver stopped answering. Off in
    /// tests, which drive one pass directly rather than waiting on a ticker.
    /// </remarks>
    public bool TimersEnabled { get; set; } = true;

    /// <summary>How often the worker looks for due timers.</summary>
    /// <remarks>
    /// R-04 wants a fire "≤1 s after expiry"; the sweep's precision is its interval, so it is a
    /// second. The scan is one index-only claim against <c>ix_timers_due</c>, which is proportional
    /// to the backlog rather than to the table.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan TimerInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many timers one pass claims.</summary>
    [Range(1, 10_000)]
    public int TimerBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a claimed timer is hidden from other replicas before it becomes due again.
    /// </summary>
    /// <remarks>
    /// Long enough for one transition to commit, short enough that a worker killed mid-fire costs a
    /// ride half a minute rather than its only backstop.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan TimerLease { get; set; } = TimeSpan.FromSeconds(30);

    // -----------------------------------------------------------------------------------------
    // The device plane (R-15) and the R-20 gauges
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the EMQX presence subscriber runs in this process (R-15).
    /// </summary>
    /// <remarks>
    /// ADD §6's ride-svc row lists <c>veh/{vehicleId}/status</c> (LWT) among what this service
    /// consumes, so the subscription is this service's own rather than something dispatch-svc
    /// relays. **Off by default**, like trip-state-svc's: it is the only part that needs a broker
    /// connection, and a deployment without EMQX reachable should run the ride lifecycle rather
    /// than log a connection failure every few seconds. With it off, the grace path is still
    /// reachable through <c>POST /v1/internal/rides/{rideId}/system-cancel</c>, which is how
    /// §11.12 describes dispatch-svc driving it.
    /// </remarks>
    public bool VehicleStatusEnabled { get; set; }

    /// <summary>MQTT service-account name; <c>svc-</c> is prefixed by the token issuer.</summary>
    public string MqttServiceName { get; set; } = "ride";

    // -----------------------------------------------------------------------------------------
    // Δ C037 — proxy booking (P-02, P-03, P-12) and package delivery (P-07, P-08, P-14)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// HMAC key for <c>rides.rides.rider_phone_hash</c> and
    /// <c>rides.location_requests.rider_phone_hash</c> (P-03).
    /// </summary>
    /// <remarks>
    /// Required outside Development — see <see cref="Rides.RiderPhoneHasher"/> for why an unkeyed
    /// digest of a <c>+947XXXXXXXX</c> number is not a hash. <b>Not rotatable in place</b>: a new
    /// key partitions the existing rows rather than re-keying them, so the P-12 audit stops being
    /// able to group a booker's requests by subject across the change.
    /// </remarks>
    public string? PhoneHashKey { get; set; }

    /// <summary>
    /// HMAC pepper for the two package OTP digests (P-07: "hashed (HMAC-SHA256 with a
    /// per-environment pepper) before persistence").
    /// </summary>
    /// <remarks>
    /// Required outside Development. Rotating it makes every package booked before the change
    /// undeliverable by OTP — the digests no longer match any code — so a rotation is a drain, not
    /// a restart. The photo-proof path (P-10) is what an in-flight delivery falls back to.
    /// </remarks>
    public string? OtpPepper { get; set; }

    /// <summary>
    /// How many wrong codes one OTP gate absorbs before <c>423 otp-locked</c> (P-07: "max 5
    /// attempts each → admin queue").
    /// </summary>
    [Range(1, 20)]
    public int MaxOtpAttempts { get; set; } = 5;

    /// <summary>
    /// How long a rider has to answer a location request (P-02, ADD §11.15: 5 min; the contract
    /// types <c>ttl</c> as <c>const: 300</c>).
    /// </summary>
    /// <remarks>
    /// The contract pins the number a client is told, so this is not a knob a deployment may turn
    /// freely: changing it makes the 202 disagree with <c>ride.yaml</c>. It is configuration so a
    /// test can close the window without waiting five minutes.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:30:00")]
    public TimeSpan LocationRequestTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>P-12: 5 location requests per booker per hour.</summary>
    [Range(1, 1_000)]
    public int LocationRequestsPerHour { get; set; } = 5;

    /// <summary>P-12: 30 location requests per booker per day.</summary>
    [Range(1, 10_000)]
    public int LocationRequestsPerDay { get; set; } = 30;

    /// <summary>How many expired location requests one sweep pass resolves.</summary>
    [Range(1, 10_000)]
    public int LocationRequestBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a delivered COD package may go unreconciled before it becomes a dispute (P-14,
    /// D5' §8.3: 24 h).
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan CodUncollectedGrace { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// iam-svc's base address, for the P-03 registration lookup (<c>GET /v1/users/lookup</c>).
    /// </summary>
    /// <remarks>
    /// <b>Unset means the location-request family is not mapped at all</b> — the same discipline
    /// <see cref="InternalApiKey"/> applies to the internal plane. A proxy booking whose rider is
    /// named by phone needs the same lookup and is refused with
    /// <c>503 dependency-unavailable</c> rather than guessed at.
    /// </remarks>
    public string? IamBaseUrl { get; set; }

    /// <summary>Must equal iam-svc's <c>Auth:InternalApiKey</c>, or every lookup is a 404 (C027).</summary>
    public string? IamInternalApiKey { get; set; }

    /// <summary>Budget for one iam-svc lookup. It sits in front of a booker tapping a button.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan IamTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Where P-10 delivery photos are written. Empty means a directory under the system temp path.
    /// </summary>
    /// <remarks>
    /// <b>Not object storage.</b> D-36 puts uploads on SSE-KMS buckets and no service in this build
    /// has a client for one — see <see cref="Rides.IProofPhotoStore"/>.
    /// </remarks>
    public string? ProofPhotoRoot { get; set; }

    /// <summary>Ceiling on one delivery photo, past which the upload is <c>413</c>.</summary>
    /// <remarks>
    /// <b>No spec pins it.</b> Eight megabytes is a generous phone photo and small enough that a
    /// driver on a Sri Lankan mobile network finishes the upload while standing at the door; the
    /// point of the number is that there is one, because the route reads a stream from an
    /// unauthenticated-by-content client.
    /// </remarks>
    [Range(64 * 1024, 64 * 1024 * 1024)]
    public long ProofPhotoMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Whether the ADD §13.3.1 stuck-state gauges are published (R-20).</summary>
    /// <remarks>
    /// On by default and cheap: one indexed count per state per scrape, only while something is
    /// scraping. Off in tests, where a dozen harnesses share one database and each would otherwise
    /// report the others' rides.
    /// </remarks>
    public bool StuckStateMetricsEnabled { get; set; } = true;
}

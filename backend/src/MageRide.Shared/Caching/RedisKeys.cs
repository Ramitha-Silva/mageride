using System.Globalization;

namespace MageRide.Shared.Caching;

/// <summary>
/// The Redis key space (ADD §9.4). Every key pattern in one place so two services cannot disagree
/// about where a value lives — <c>position-processor-svc</c> writes
/// <c>driver:availability:{driverId}</c> and <c>dispatch-svc</c> reads it, and a typo in either
/// would silently produce an empty candidate set.
/// </summary>
public static class RedisKeys
{
    /// <summary>GEO set of every active vehicle's last position.</summary>
    public const string GeoLive = "geo:live";

    /// <summary>Postgres notification channel for the outbox dispatcher (E-09).</summary>
    public const string OutboxNotifyChannel = "ride_outbox";

    /// <summary>HASH of cached vehicle metadata (type, colour, route).</summary>
    public static string VehicleMeta(Guid vehicleId) => $"veh:meta:{vehicleId}";

    /// <summary>STREAM of per-cell position events for fan-out consumers.</summary>
    public static string Cell(string h3Index) => $"cell:{h3Index}";

    /// <summary>
    /// Highest <c>seq</c> seen for a vehicle — layer 1 of the replay dedupe (R-17, T-05).
    /// </summary>
    /// <remarks>
    /// <c>backend/contracts/realtime/mqtt-topics.md</c> §5: position-processor discards
    /// <c>seq &lt;= last_seen</c>. A fast filter, not the guarantee — layer 3 is
    /// <c>ux_positions_vehicle_seq</c> on the hypertable, which is what survives a Redis flush.
    /// </remarks>
    public static string VehicleSeq(Guid vehicleId) => $"veh:seq:{vehicleId}";

    /// <summary>HASH of live trip state (start time, seats, mode).</summary>
    public static string ActiveTrip(Guid vehicleId) => $"trip:active:{vehicleId}";

    /// <summary>IMEI → vehicleId lookup cache (T-03).</summary>
    /// <remarks>
    /// <para>
    /// Written by provisioning-svc (C030) on bind and deleted on unbind, revoke or quarantine;
    /// <c>prov.tracker_bindings</c> is the source of truth and this is only a cache (D6' §4.3,
    /// 24 h TTL). <b>Present means ACTIVE</b> — there is no "revoked" value, because a lookup
    /// that missed and a lookup that found a revoked binding must produce the same answer from a
    /// reader that has not been told the difference.
    /// </para>
    /// <para>
    /// The tcp-adapter reads this on every device connect and re-reads it every 5 minutes on a
    /// long socket (T-01), which is what makes a deletion here a disconnection there.
    /// </para>
    /// </remarks>
    public static string Imei(string imei) => $"imei:{imei}";

    /// <summary>
    /// Pub/sub channel carrying tracker credential lifecycle changes — the sub-second half of
    /// T-12 (D6' §4.2).
    /// </summary>
    /// <remarks>
    /// Fire-and-forget beside the durable <c>provisioning.events</c> outbox, not instead of it:
    /// a subscriber that was down misses the message and falls back to the cache TTL and the
    /// topic. Subscribers force-close any socket whose IMEI or credential serial the message
    /// names, inside the 1 s the ADD §7.7.3 budget allows.
    /// </remarks>
    public const string TrackerCredentialChannel = "prov:tracker";

    /// <summary>Publish-rate token bucket for a vehicle (D-17, E-08).</summary>
    public static string VehicleRateLimit(Guid vehicleId) => $"rate:{vehicleId}";

    /// <summary>Generic token-bucket key: <c>rate:{policy}:{subject}</c>.</summary>
    public static string RateLimit(string policy, string subject) => $"rate:{policy}:{subject}";

    /// <summary>
    /// Cluster-wide count of a vehicle's <c>pos/live</c> publishes inside one wall-clock second —
    /// D-17's 5 msg/s ceiling, counted across every mqtt-bridge replica (C038).
    /// </summary>
    /// <remarks>
    /// A plain <c>INCRBY</c>+<c>EXPIRE</c> counter rather than a token bucket, because the question
    /// is "what rate did this vehicle actually publish at" and the answer has to be reported, not
    /// enforced — the bridge observes, EMQX's listener limiter enforces. It has to be shared: a
    /// shared subscription hands each replica a random slice of one vehicle's stream, so no replica
    /// on its own ever sees the rate the vehicle is really publishing at.
    /// </remarks>
    public static string VehiclePublishWindow(Guid vehicleId, long unixSecond) =>
        $"rate:mqtt-live:{vehicleId}:{unixSecond.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Debounce for D-17's <c>mqtt.rate_violation</c> audit event — one report per vehicle per
    /// cooldown, however many replicas noticed (C038).
    /// </summary>
    public static string VehicleRateViolation(Guid vehicleId) => $"rate:mqtt-violation:{vehicleId}";

    /// <summary>GEO index of dispatch candidates for a vehicle type in an H3 res-5 cell (R-08).</summary>
    public static string AvailableDrivers(string vehicleType, string h3Res5Cell) =>
        $"geo:drivers:available:{vehicleType}:{h3Res5Cell}";

    /// <summary>HASH of <c>{state, lastSeen, vehicleType, level, walletOk, currentRideId?}</c>, TTL 60 s (R-08).</summary>
    public static string DriverAvailability(Guid driverId) => $"driver:availability:{driverId}";

    /// <summary>HASH of the Directional Travel filter, TTL = remaining duration (DT-01).</summary>
    public static string DriverDirectional(Guid driverId) => $"driver:directional:{driverId}";

    /// <summary>Per-Colombo-day activation counter for Directional Travel, TTL 36 h (DT-03).</summary>
    public static string DriverDirectionalUses(Guid driverId, DateOnly businessDate) =>
        $"driver:directional:uses:{driverId}:{businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>HASH of <c>{driverId, expiresAt, status}</c>, TTL 15 s. A fast hint, not authoritative (R-04).</summary>
    public static string Offer(Guid rideId) => $"offer:{rideId}";

    /// <summary>Atomic driver reservation, <c>SET NX PX</c> via Lua (R-10).</summary>
    public static string DriverOfferLock(Guid driverId) => $"lock:driver-offer:{driverId}";

    /// <summary>Single-writer lock held for the duration of a ride state transition.</summary>
    public static string RideLock(Guid rideId) => $"lock:ride:{rideId}";

    /// <summary>
    /// The vehicle a driver has selected to go live on — <c>lock:driver:{driverId}</c> (D-03,
    /// US-9.6). Holds the vehicle id, not a token.
    /// </summary>
    /// <remarks>
    /// A published fact rather than a mutual-exclusion lock, despite the <c>lock:</c> prefix the
    /// ADD gives it: the one-vehicle-at-a-time invariant is
    /// <c>registry.driver_profiles.active_vehicle_id</c>, whose primary key enforces it without
    /// anybody's cooperation. This is what the two downstream planes read so they agree with the
    /// registry about which vehicle that is (D-03 names both
    /// <c>ux_sessions_active_driver</c> and <c>dispatch.driver_presence</c>).
    /// </remarks>
    public static string DriverLiveVehicle(Guid driverId) => $"lock:driver:{driverId}";

    /// <summary>
    /// The Mode A/B tracking session a driver currently holds — trip-state-svc's half of the
    /// D-03 active-session mutex (C031).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-03 and ADD §6 both name <c>lock:driver:{driverId}</c> SETNX for this, and that key was
    /// already taken.</b> <see cref="DriverLiveVehicle"/> is registry-svc's published go-live
    /// selection (C028) and is written with an unconditional <c>SET</c> at the moment the driver
    /// picks a vehicle — which is necessarily *before* they start a session. A <c>SETNX</c> against
    /// it would therefore fail every single time, and the mutex would refuse every start rather
    /// than every second one. Raised as a micro-change-set in the C031 handoff: <b>the two are
    /// different facts at different phases and need different keys.</b>
    /// </para>
    /// <para>
    /// Like registry's, this is a published fact rather than the invariant.
    /// <c>ux_sessions_active_driver</c> is what actually stops a driver holding two live sessions;
    /// this is how the planes that need the answer quickly get it without a query, and it is
    /// written after COMMIT and best effort.
    /// </para>
    /// </remarks>
    public static string DriverSession(Guid driverId) => $"lock:session:{driverId}";

    /// <summary>
    /// The D-04 block status for one user — <c>OK | WARN | BOOKING_DISABLED | DELISTED</c> plus the
    /// counters behind it, TTL <c>Reputation:BlockStatusCacheTtl</c> (5 s).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in ADD §9.4's key space</b> — a micro-change-set raised in the C033 handoff. D5' §3.2
    /// makes <c>reputation.block_state</c> a hard gate dispatch-svc applies to every candidate, and
    /// the C033 DoD requires the gRPC call to answer inside 20 ms p95 "against a warm cache", so a
    /// cache has to exist; ADD §9.4 gives it no key. Sits beside D-08's wallet gate, which is the
    /// other per-candidate lookup on the same hot path.
    /// </para>
    /// <para>
    /// Written and deleted by reputation-svc alone (C033) and never read directly by anybody else:
    /// dispatch-svc and fanout-svc ask over gRPC, because a second reader would have to agree about
    /// the record shape and about what a miss means.
    /// </para>
    /// </remarks>
    public static string BlockStatus(Guid userId) => $"reputation:block:{userId}";

    /// <summary>
    /// The D-08 pre-dispatch wallet balance for one driver, in LKR minor units, TTL 5 s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADD §6's dispatch-svc row and D5' §9.2 both name it: "reads <c>wallet:bal:{driverId}</c>
    /// Redis cache (5 s TTL); first trip of day always allowed; 2nd+ refused if balance &lt;
    /// daily-fee". The master is <c>billing.journal_postings</c> and the read model is
    /// <c>billing.wallets</c> (D-09, §10) — this is a cache in front of the read model, never a
    /// third copy of the number.
    /// </para>
    /// <para>
    /// <b>Debit-invalidated</b> (D5' §9.2: "<c>wallet.debited</c> event clears"), which is
    /// wallet-svc's (C046) to publish and to honour. dispatch-svc reads it and populates it
    /// read-through on a miss (C034); a miss it cannot resolve is <em>not</em> treated as a zero
    /// balance — see D-08's degraded-mode rule.
    /// </para>
    /// </remarks>
    public static string WalletBalance(Guid driverId) => $"wallet:bal:{driverId}";

    /// <summary>Opaque refresh token mirror for O(1) revocation (D-29).</summary>
    public static string RefreshToken(string jti) => $"refresh:{jti}";

    /// <summary>Pre-signed URL to a generated PDPA export ZIP, TTL 30 d (E-06).</summary>
    public static string PdpaExport(Guid requestId) => $"pdpa:export:{requestId}";
}

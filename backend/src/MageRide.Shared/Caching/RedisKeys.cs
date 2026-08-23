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

    /// <summary>
    /// Count of a vehicle's samples inside one D-17 <b>second-line</b> window —
    /// <c>backend/contracts/realtime/mqtt-topics.md</c> §4's "10 msg/s per 10 s", counted across
    /// every position-processor replica (C039).
    /// </summary>
    /// <remarks>
    /// A separate key family from <see cref="VehiclePublishWindow"/> on purpose. That one is the
    /// bridge's per-second observation of the broker's 5 msg/s ceiling and it only <i>reports</i>;
    /// this one is the ten-second window the processor <i>drops</i> on, and the two would report
    /// different rates over the same key if they shared it.
    /// </remarks>
    public static string VehicleIngestWindow(Guid vehicleId, long windowStartUnixSeconds) =>
        $"rate:pos-ingest:{vehicleId}:{windowStartUnixSeconds.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Debounce for the second-line <c>mqtt.rate_violation</c> — one report per vehicle per
    /// cooldown, across every position-processor replica (C039).
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="VehicleRateViolation"/>: sharing that key would let the bridge's
    /// report of the 5 msg/s ceiling suppress the processor's report of the 10 msg/s one, and the
    /// second line exists precisely to be heard when the first has already fired.
    /// </remarks>
    public static string PositionRateViolation(Guid vehicleId) => $"rate:pos-violation:{vehicleId}";

    /// <summary>
    /// The driver a vehicle is currently on standby with — the reverse of
    /// <see cref="DriverAvailability"/>'s <c>vehicleId</c> field (R-08).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in ADD §9.4's key space</b> — a micro-change-set raised in the C039 handoff. §9.4 makes
    /// position-processor-svc the writer of <c>driver:availability:{driverId}</c> and
    /// <c>geo:drivers:available:*</c>, and a position sample carries no driver: the whole telemetry
    /// contract (<c>mqtt-topics.md</c> §2.1) is keyed by <c>vehicleId</c> because EMQX authenticates
    /// a <i>vehicle</i>. Without a reverse binding the attribution in §9.4 is not implementable at
    /// all, which is why C024 left the heartbeat unwritten and C034 landed it in dispatch-svc.
    /// </para>
    /// <para>
    /// Written and deleted by dispatch-svc alone, at the two moments the (driver, vehicle) pair is
    /// established and dissolved — <c>POST /v1/standby/online</c> and going offline. Read by
    /// position-processor-svc on the hot path. A miss means "no Mode C driver is on standby with
    /// this vehicle", which is the ordinary case: <c>telemetry.raw</c> carries every Mode A bus and
    /// every Mode B shared vehicle on the platform.
    /// </para>
    /// </remarks>
    public static string VehicleDriver(Guid vehicleId) => $"veh:driver:{vehicleId}";

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

    /// <summary>
    /// The Mode B entitlement cache for one passenger — a SET of the vehicle ids they may watch
    /// (D-23), checked by fanout-svc on group join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one key on this page D-23 spells out by name. It has <b>no TTL</b>: the invalidation is
    /// the <c>share.granted</c>/<c>share.revoked</c> pair on <c>registry.events</c>, and a TTL would
    /// make an entitled passenger's map go quietly dark on a schedule nothing published.
    /// </para>
    /// <para>
    /// Written and deleted by <b>fanout-svc alone</b> (C041), from those events. The durable truth
    /// is <c>registry.shares</c>; this is a projection of it shaped for one question asked on a
    /// socket connect — "which vehicles may this passenger see" — which no SQL index on that table
    /// answers in the time a WebSocket handshake has.
    /// </para>
    /// </remarks>
    public static string Share(Guid userId) => $"share:{userId}";

    /// <summary>
    /// The ride a Mode C vehicle is currently engaged on, or absent when it is idle (US-7.16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in ADD §9.4's key space</b> — a micro-change-set raised in the C041 handoff. §6's
    /// fanout-svc row requires "Mode C vehicles engaged on an active hire are excluded from public
    /// groups (their live position is sent only to the assigned ride's passenger group)", and the
    /// fan-out plane meets a vehicle, not a ride: the cell stream is keyed by <c>vehicleId</c> and
    /// carries no ride at all. Without this key the exclusion is a per-frame join against
    /// <see cref="VehicleDriver"/> and then <see cref="DriverAvailability"/>, two round trips deep,
    /// on the hottest path the platform has.
    /// </para>
    /// <para>
    /// Written and deleted by fanout-svc alone, from <c>ride.events</c>: set on the accept, cleared
    /// on every terminal. The value is the ride id, because "hide it from the public map" and "send
    /// it to <c>ride:{rideId}</c>" are the same decision and the second one needs the id.
    /// </para>
    /// </remarks>
    public static string VehicleEngagement(Guid vehicleId) => $"veh:engaged:{vehicleId}";

    /// <summary>
    /// When a vehicle's EMQX last will last fired — the <c>offline</c> half of US-7.17.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in ADD §9.4's key space</b> either (C041 handoff). Deliberately <em>not</em> a field on
    /// <see cref="VehicleMeta"/>: that hash is position-processor-svc's and is rewritten on every
    /// accepted sample, so a second writer there would race the hot path for a fact the hot path
    /// never learns.
    /// </para>
    /// <para>
    /// It holds an <b>instant</b>, not a flag, and the visibility rule compares it against the
    /// sample's own timestamp. A vehicle whose broker session died and then came back publishing is
    /// live again the moment a fresher sample lands, with no <c>online</c> message needed — which
    /// matters because a device that crashed and restarted may never send one.
    /// </para>
    /// </remarks>
    public static string VehicleOfflineAt(Guid vehicleId) => $"veh:offline:{vehicleId}";

    /// <summary>
    /// Who is allowed into <c>ride:{rideId}</c> — fanout-svc's participant projection of a ride.
    /// </summary>
    /// <remarks>
    /// <b>Not in ADD §9.4's key space</b> (C041 handoff). <c>signalr-hub.md</c> §2 makes
    /// <c>SubscribeRide</c> "rejected unless the caller is a participant" and fanout-svc holds no
    /// database — asking ride-svc over HTTP on every subscribe would put a synchronous dependency on
    /// the socket path that R-01's outbox exists to avoid. Built from <c>ride.events</c>, which
    /// carries every party on every transition.
    /// </remarks>
    public static string RideParticipants(Guid rideId) => $"fanout:ride:{rideId}";

    /// <summary>
    /// fanout-svc's directed-send channel — D6' §5's "Redis backplane (MVP)".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries only the sends whose target connection could be on any replica: the D-22 revocation,
    /// <c>RideStateChanged</c>, <c>LocationRequestResolved</c>, <c>PackageStatus</c> and the
    /// <c>VehicleRemoved</c> notices. <b>The per-cell position batches never travel on it</b> —
    /// every replica reads the cell streams it has members in and pushes to its own local group, so
    /// re-broadcasting a batch would deliver one copy per replica in the deployment.
    /// </para>
    /// <para>
    /// Redpanda replaces this channel beyond five pods (D6' §5) — a topic with a consumer group per
    /// replica rather than per service, which is the same fan-out with durability nobody needs for a
    /// message whose whole value expires in 200 ms.
    /// </para>
    /// </remarks>
    public const string FanoutControlChannel = "fanout:control";

    /// <summary>
    /// content-svc's cache-purge channel — the invalidation half of D7' §4.2's <c>Cache__Ttl</c>
    /// (C045).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in ADD §9.4's key space</b> — a micro-change-set raised in the C045 handoff. The
    /// C045 deliverable is "aggressive caching with an invalidation path on publish", and content-svc
    /// caches in process: the datasets are a few hundred rows, read on every notification render, and
    /// a Redis round trip per render would be a cache in front of a cache. So the *cache* is local
    /// and only the *purge* is shared, which is what this channel carries — a comma-separated
    /// dataset list, or empty for all of them.
    /// </para>
    /// <para>
    /// Published by content-svc on every admin publish and by its
    /// <c>POST /v1/internal/content/cache/purge</c> route, which exists for the one dataset it serves
    /// and does not own: the launch cities, whose CRUD D3' assigns to admin-bff. Best effort by
    /// design — a subscriber that was down misses the message and falls back to the TTL, which is
    /// the same worst case a deployment with no Redis has.
    /// </para>
    /// </remarks>
    public const string ContentInvalidationChannel = "content:invalidate";

    /// <summary>Opaque refresh token mirror for O(1) revocation (D-29).</summary>
    public static string RefreshToken(string jti) => $"refresh:{jti}";

    /// <summary>
    /// A tombstone for a session that has been revoked, read on every authenticated request
    /// (AL-08, Δ MCS-30).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Presence means revoked; absence means nothing.</b> That direction is the whole design.
    /// The <see cref="RefreshToken"/> mirror beside it is best-effort and its own writer says
    /// "Postgres remains authoritative" — so treating a MISSING key as revocation would sign every
    /// driver on the platform out of a Redis restart. A present key can only have been written by
    /// a revocation, so it is safe to act on and safe to lose.
    /// </para>
    /// <para>
    /// Written with a TTL of one access-token lifetime. After that the token it exists to kill has
    /// expired on its own and the tombstone has nothing left to do, which is what keeps this from
    /// growing without bound.
    /// </para>
    /// </remarks>
    public static string RevokedSession(string jti) => $"session:revoked:{jti}";

    /// <summary>Pre-signed URL to a generated PDPA export ZIP, TTL 30 d (E-06).</summary>
    public static string PdpaExport(Guid requestId) => $"pdpa:export:{requestId}";

    /// <summary>
    /// The plaintext delivery code for a package in transit — the four digits SCR-WT-002 shows an
    /// unregistered recipient (US-20.5, P-07). TTL = the delivery window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in ADD §9.4's key space</b> — a micro-change-set raised in the C066 handoff, the same
    /// shape as the five keys above it. It exists because three components each made a correct local
    /// decision that left one gap between them: ride-svc mints the code at pickup and keeps only the
    /// digest ("in the clear for one hop instead of for the whole booking", C037); notification-svc
    /// pushes it to a recipient who has the app and deliberately leaves it out of the SMS for one who
    /// does not, because D6' I-23.3 has the web page show it "post token validation"; and public-bff,
    /// which serves that page, had nowhere to read it from. The unregistered recipient — the entire
    /// audience of SCR-WT-002 — could not learn their own code.
    /// </para>
    /// <para>
    /// <b>Written by notification-svc alone</b> (C051), in the same handler that mints the
    /// <c>package_recipient</c> token, and read by public-bff alone (C066) for the holder of that
    /// token. Redis rather than a column on purpose: the value is a short-lived credential for one
    /// handover, it expires with the delivery whether or not anything remembers to clear it, it
    /// reaches no backup, and a PDPA erasure has nothing to reach.
    /// </para>
    /// </remarks>
    public static string PackageDeliveryCode(Guid rideId) => $"package:delivery-code:{rideId}";
}

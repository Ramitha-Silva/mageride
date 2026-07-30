namespace MageRide.Shared.Realtime;

/// <summary>
/// Everything fixed about the <c>/hubs/live</c> connection
/// (<c>backend/contracts/realtime/signalr-hub.md</c>, D6' §5) — the server half of the KMP module's
/// <c>lk.mageride.shared.realtime.LiveHub</c>.
/// </summary>
/// <remarks>
/// OpenAPI cannot express a bidirectional hub, so that contract file is normative for this surface
/// the way the <c>*.yaml</c> files are for the REST one. <b>SignalR resolves both method and event
/// names by string</b>, so a typo on either side is not a compile error anywhere — it is a client
/// that silently never hears anything. The names live here once and every call site takes them from
/// here.
/// </remarks>
public static class LiveHub
{
    /// <summary>The hub endpoint, relative to the gateway base URL.</summary>
    public const string Path = "/hubs/live";

    /// <summary>
    /// The query parameter the access token travels in.
    /// </summary>
    /// <remarks>
    /// SignalR's own convention, and unavoidable: a browser <c>WebSocket</c> cannot set an
    /// <c>Authorization</c> header. The credential is the ordinary 30-minute API access token
    /// (D-29) — <b>never</b> the MQTT session JWT, which is a different credential with a different
    /// lifetime and audience (E-02).
    /// </remarks>
    public const string AccessTokenQueryParam = "access_token";

    /// <summary>Server → client ping interval (<c>signalr-hub.md</c> §1).</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>How long the server waits for a client message before closing the connection.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Client → server method names.</summary>
    public static class Methods
    {
        /// <summary><c>JoinGeocells(cells: string[])</c> — subscribe to live frames for those cells.</summary>
        public const string JoinGeocells = "JoinGeocells";

        /// <summary><c>LeaveGeocells(cells: string[])</c> — unsubscribe, with 30 s hysteresis.</summary>
        public const string LeaveGeocells = "LeaveGeocells";

        /// <summary><c>SubscribeRide(rideId)</c> — the caller's own ride (US-6A.12). C041.</summary>
        public const string SubscribeRide = "SubscribeRide";

        /// <summary><c>SubscribeLocRequest(requestId)</c> — the proxy round-trip (P-13). C041.</summary>
        public const string SubscribeLocRequest = "SubscribeLocRequest";
    }

    /// <summary>Server → client event names.</summary>
    public static class Events
    {
        /// <summary>Per-cell batch of <see cref="VehicleFrame"/>, every 2–8 s (US-7.3).</summary>
        public const string VehiclePositions = "VehiclePositions";

        /// <summary><c>{vehicleId, reason}</c> — stale, offline or engaged (US-7.16/7.17, D-22).</summary>
        public const string VehicleRemoved = "VehicleRemoved";

        /// <summary><c>{rideId, state, version, driver?, etaSeconds?}</c> — every ride transition.</summary>
        public const string RideStateChanged = "RideStateChanged";

        /// <summary><c>{rideId, lat, lng, heading}</c> — assigned-ride live position.</summary>
        public const string DriverPosition = "DriverPosition";

        /// <summary><c>{requestId, state, geo?}</c> — the proxy round-trip resolving (P-02/P-13).</summary>
        public const string LocationRequestResolved = "LocationRequestResolved";

        /// <summary><c>{vehicleId}</c> — Mode B unsubscribe (D-22).</summary>
        public const string ShareRevoked = "ShareRevoked";

        /// <summary><c>{rideId, status}</c> — package handoff progress (US-20.7).</summary>
        public const string PackageStatus = "PackageStatus";
    }

    /// <summary>
    /// <c>cell:{h3index}</c> — the public geocell group. Res-7 only.
    /// </summary>
    /// <remarks>
    /// Same string as the Redis stream position-processor-svc writes
    /// (<see cref="Caching.RedisKeys.Cell"/>), so the fan-out step is a projection rather than a
    /// translation and a mismatch cannot hide between the two.
    /// </remarks>
    public static string CellGroup(string h3Index) => Geo.GeoCells.CellGroup(h3Index);

    /// <summary><c>ride:{rideId}</c> — the ride's passenger, its driver, and a proxy booking's booker.</summary>
    public static string RideGroup(Guid rideId) => $"ride:{rideId}";

    /// <summary>
    /// <c>vehicle:{vehicleId}</c> — the one vehicle's own stream: its Mode B entitled watchers
    /// (D-23) and, on the driver home map, its own driver (AL-31).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not in D6' §5.1's group table</b> — a micro-change-set raised in the C041 handoff, and the
    /// only shape in which §5.2 and §11.10 are both satisfiable. §5.2 says a public geocell group
    /// fans out "Mode A + entitled Mode B", which cannot be true of a <em>group</em>: a cell group
    /// has one membership and one message, so a Mode B frame put on it reaches every passenger in
    /// the cell, entitled or not. §11.10's remedy — remove the passenger from the geocell group —
    /// would also stop them seeing the buses, which is the visibility §5.2 grants unconditionally.
    /// </para>
    /// <para>
    /// Splitting the private vehicles onto a group of their own makes both lines hold literally:
    /// entitlement is still "checked on group join" and never per frame, the D-22 revocation is
    /// still one directed <c>RemoveFromGroupAsync</c>, and it now removes exactly the thing that was
    /// granted. It is also what the C041 DoD calls "the vehicle's stream".
    /// </para>
    /// </remarks>
    public static string VehicleGroup(Guid vehicleId) => $"vehicle:{vehicleId}";

    /// <summary><c>booker:{bookerId}:loc-req:{requestId}</c> — the booker who issued a request (P-13).</summary>
    public static string BookerLocationRequestGroup(Guid bookerId, Guid requestId) =>
        $"booker:{bookerId}:loc-req:{requestId}";
}

/// <summary>
/// One vehicle in a <c>VehiclePositions</c> batch (<c>signalr-hub.md</c> §3).
/// </summary>
/// <remarks>
/// The event's argument is a <b>list</b> of these, batched per cell — not one message per fix. A
/// per-fix fan-out would be five messages a second per vehicle multiplied by every subscriber of
/// its cell, which is the cost model ADD §7.4 exists to avoid.
/// </remarks>
/// <param name="VehicleId">The vehicle.</param>
/// <param name="Lat">Degrees, −90…90.</param>
/// <param name="Lng">Degrees, −180…180.</param>
/// <param name="Heading">Course over ground, 0…359.</param>
/// <param name="Speed">Metres per second.</param>
/// <param name="Type">Canonical vehicle type, so the map can pick its marker.</param>
/// <param name="Mode">Operating mode — <c>A</c>, <c>B</c> or <c>C</c>.</param>
public sealed record VehicleFrame(
    Guid VehicleId,
    double Lat,
    double Lng,
    int? Heading = null,
    double? Speed = null,
    string? Type = null,
    string? Mode = null);

/// <summary>
/// <c>VehicleRemoved</c> — a marker the client should drop (<c>signalr-hub.md</c> §3).
/// </summary>
/// <remarks>
/// Sent instead of simply going quiet because a live map cannot tell "this vehicle stopped
/// reporting" from "this batch happened not to contain it": batches carry only what moved, so a
/// client that inferred removal from absence would erase every stationary vehicle every tick.
/// </remarks>
/// <param name="VehicleId">The vehicle to remove.</param>
/// <param name="Reason">One of <see cref="VehicleRemovalReasons"/>.</param>
public sealed record VehicleRemovedEvent(Guid VehicleId, string Reason);

/// <summary>The driver a <see cref="RideStateChangedEvent"/> carries once one is assigned.</summary>
/// <remarks>
/// Deliberately thin. D3' §3.1 writes the member as <c>driver?</c> and says no more; everything a
/// passenger's ride card shows beyond this — name, photo, rating, plate — is a REST read of
/// <c>GET /v1/rides/{id}</c>, which is the surface that already redacts per AL-48 and P-05. A socket
/// frame that carried a phone number would be a second, unaudited disclosure path.
/// </remarks>
public sealed record RideDriverSummary(Guid DriverId, Guid? VehicleId, string? VehicleType);

/// <summary>
/// <c>RideStateChanged</c> — every transition of the ride aggregate (ADD Appendix B.2).
/// </summary>
/// <param name="State">One of the 18 <c>RideState</c> values, spelled as <c>_shared.yaml</c> does.</param>
/// <param name="Version">
/// The same optimistic-concurrency counter the REST responses carry, so a client that has both can
/// tell which is newer. Socket delivery is at-least-once and unordered across reconnects.
/// </param>
/// <param name="EtaSeconds">
/// Absent from fanout-svc. D3' lists it optional; the estimate is query-svc's (C042), computed from
/// the route, and inventing one here would put two different numbers in front of one passenger.
/// </param>
public sealed record RideStateChangedEvent(
    Guid RideId, string State, long Version, RideDriverSummary? Driver = null, int? EtaSeconds = null);

/// <summary>
/// <c>DriverPosition</c> — the assigned ride's live vehicle (US-6A.12), to <c>ride:{rideId}</c> only.
/// </summary>
/// <remarks>
/// The counterpart of US-7.16: once a Mode C vehicle is on hire it leaves the public geocell groups
/// entirely and its position reaches exactly one audience — the people on the ride.
/// </remarks>
public sealed record DriverPositionEvent(Guid RideId, double Lat, double Lng, int? Heading = null);

/// <summary>A coordinate on the socket surface, spelled as the REST contracts spell one.</summary>
public sealed record LiveGeoPoint(double Lat, double Lng);

/// <summary>
/// <c>LocationRequestResolved</c> — the P-02/P-13 proxy round-trip ending.
/// </summary>
/// <param name="Geo">
/// <b><see cref="LocationRequestStates.Confirmed"/> only.</b> A decline transmits nothing — that is
/// P-02's fence, and ride-svc's event carries null by construction on the other two.
/// </param>
public sealed record LocationRequestResolvedEvent(Guid RequestId, string State, LiveGeoPoint? Geo = null);

/// <summary>The three terminals of a location request (<c>signalr-hub.md</c> §3).</summary>
public static class LocationRequestStates
{
    public const string Confirmed = "Confirmed";
    public const string Declined = "Declined";
    public const string Expired = "Expired";
}

/// <summary><c>ShareRevoked</c> — the Mode B grant this passenger held is gone (D-22).</summary>
/// <remarks>
/// Delivered <em>with</em> the directed <c>RemoveFromGroupAsync</c>, not instead of it. The group
/// removal stops the frames; this tells the client to drop the marker, which otherwise sits on the
/// map at its last position looking live.
/// </remarks>
public sealed record ShareRevokedEvent(Guid VehicleId);

/// <summary><c>PackageStatus</c> — a delivery's handoff progress (US-20.7).</summary>
public sealed record PackageStatusEvent(Guid RideId, string Status);

/// <summary>Why a vehicle left the public map (US-7.16/7.17, D-22).</summary>
public static class VehicleRemovalReasons
{
    /// <summary>No fresh sample inside the freshness window.</summary>
    public const string Stale = "stale";

    /// <summary>The broker's last will fired, or the device disconnected cleanly (R-15, T-04).</summary>
    public const string Offline = "offline";

    /// <summary>
    /// A Mode C vehicle went on active hire. It leaves every public geocell group and appears only
    /// in <c>ride:{rideId}</c> from then on (D-22) — showing an engaged taxi on the public map is
    /// how a passenger ends up chasing a car that already has a fare.
    /// </summary>
    public const string Engaged = "engaged";
}

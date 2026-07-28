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

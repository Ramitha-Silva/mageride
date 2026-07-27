package lk.mageride.shared.realtime

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.geo.H3Cell
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

// The `/hubs/live` SignalR contract (`backend/contracts/realtime/signalr-hub.md`, D6' §5).
//
// OpenAPI cannot express a bidirectional hub, so that file is normative for this surface the way
// the *.yaml files are for the REST one, and these names are copied from it verbatim. A typo in a
// method or event name is not a compile error anywhere — SignalR resolves both by string — so the
// names live here once and every call site takes them from here.
//
// THIS MODULE DOES NOT OWN THE SOCKET. The SignalR Java client (Android, C076) and the SignalR
// Swift client (iOS, C094) do. What is shared is the contract they must both speak: the endpoint,
// the credential, the group names, the payload shapes and the recovery sequence.
//
// DEVICES NEVER CONNECT TO THIS HUB. Position ingest is MQTT (`mqtt/MqttTopics`); passenger
// realtime-out is SignalR. D3' §3.3 resolves the split explicitly, and there is no
// MQTT-as-realtime-out anywhere on the platform.

/** Everything fixed about the `/hubs/live` connection. */
public object LiveHub {

    /** The hub endpoint, relative to the gateway base URL. */
    public const val PATH: String = "/hubs/live"

    /**
     * The query parameter the access token travels in.
     *
     * SignalR's own convention, and unavoidable: a browser `WebSocket` cannot set an
     * `Authorization` header, and the Passenger Web subview shares this contract's shape.
     */
    public const val ACCESS_TOKEN_QUERY_PARAM: String = "access_token"

    /** Client → server ping interval. */
    public val KEEP_ALIVE: Duration = 15.seconds

    /** How long the client waits for a server message before treating the connection as dead. */
    public val SERVER_TIMEOUT: Duration = 30.seconds

    /**
     * The URL to connect to.
     *
     * **The credential is the ordinary 30-minute API access token (D-29), never the MQTT session
     * JWT.** They are separate credentials with different lifetimes and audiences (E-02): the MQTT
     * token is bound to a vehicle and survives a failed API refresh precisely so position
     * publishing does not stop mid-ride, which is not a property anything on this hub should
     * inherit. A connection whose token expires mid-stream is closed and the client reconnects
     * with a refreshed one.
     *
     * @param baseUrl Gateway origin, with or without a trailing slash.
     * @param accessToken The API access token from `SessionTokenProvider`.
     */
    public fun url(baseUrl: String, accessToken: String): String =
        "${baseUrl.trimEnd('/')}$PATH?$ACCESS_TOKEN_QUERY_PARAM=$accessToken"

    /** Client → server method names. */
    public object Method {
        /** `JoinGeocells(cells: string[])` — subscribe to live vehicle frames for those cells. */
        public const val JOIN_GEOCELLS: String = "JoinGeocells"

        /** `LeaveGeocells(cells: string[])` — unsubscribe; 30 s hysteresis on boundary churn. */
        public const val LEAVE_GEOCELLS: String = "LeaveGeocells"

        /** `SubscribeRide(rideId)` — live driver position for the caller's own ride (US-6A.12). */
        public const val SUBSCRIBE_RIDE: String = "SubscribeRide"

        /** `SubscribeLocRequest(requestId)` — the booker awaiting a rider's confirmation (P-13). */
        public const val SUBSCRIBE_LOC_REQUEST: String = "SubscribeLocRequest"
    }

    /** Server → client event names. */
    public object Event {
        /** Per-cell batch of [VehicleFrame], every 2–8 s (US-7.3). */
        public const val VEHICLE_POSITIONS: String = "VehiclePositions"

        /** [VehicleRemoved] — stale, offline, or gone on hire (US-7.16/7.17). */
        public const val VEHICLE_REMOVED: String = "VehicleRemoved"

        /** [RideStateChanged] — every ride-aggregate transition (ADD Appendix B.2). */
        public const val RIDE_STATE_CHANGED: String = "RideStateChanged"

        /** [DriverPosition] — assigned-ride live position, to `ride:{rideId}` only. */
        public const val DRIVER_POSITION: String = "DriverPosition"

        /** [LocationRequestResolved] — the proxy round-trip resolving (P-02, P-13). */
        public const val LOCATION_REQUEST_RESOLVED: String = "LocationRequestResolved"

        /** [ShareRevoked] — Mode B unsubscribe (D-22). */
        public const val SHARE_REVOKED: String = "ShareRevoked"

        /** [PackageStatusChanged] — package handoff progress (US-20.7). */
        public const val PACKAGE_STATUS: String = "PackageStatus"
    }

    /**
     * `cell:{h3index}` — the public geocell group.
     *
     * Res-7 only. `fanout-svc` publishes to res-7 groups, so a client that computed its cells at
     * another resolution would join groups that do not exist and see an empty map rather than an
     * error.
     */
    public fun cellGroup(cell: H3Cell): String = "cell:${cell.token}"

    /** `ride:{rideId}` — the ride's passenger, its driver, and a proxy booking's booker. */
    public fun rideGroup(rideId: Ulid): String = "ride:$rideId"

    /** `booker:{bookerId}:loc-req:{requestId}` — the booker who issued a location request (P-13). */
    public fun bookerLocationRequestGroup(bookerId: Ulid, requestId: Ulid): String =
        "booker:$bookerId:loc-req:$requestId"
}

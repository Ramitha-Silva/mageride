package lk.mageride.shared.realtime

/**
 * The `/hubs/live` contract as Swift can reach it (C094).
 *
 * **Why a class rather than reading [LiveHub] from Swift.** Three of the things a client needs are
 * shapes the Objective-C export handles awkwardly or not at all, and getting any of them wrong fails
 * *silently*:
 *
 * 1. **`KEEP_ALIVE` and `SERVER_TIMEOUT` are `kotlin.time.Duration`** — an inline value class the
 *    export flattens to an opaque `Long` whose encoding is a packed nanos/millis pair with a tag
 *    bit, not a count of anything. Read as a number, the keep-alive is about 3.9 × 10^13 seconds and
 *    the client never pings; the socket then dies on the server's own timeout every thirty seconds
 *    for ever. Same wall, and the same remedy, as [lk.mageride.shared.util.IosReconnectBackoff].
 * 2. **`LiveHub.Method` and `LiveHub.Event` are nested objects**, and Kotlin's nested types have no
 *    Objective-C counterpart — the exporter concatenates the names, and whether a `const val` on an
 *    exported object arrives as a class property or an instance one is a property of the compiler
 *    rather than of this codebase. A Swift call site that guessed would compile on one Kotlin
 *    version and not the next.
 * 3. **A method or event name is resolved by string.** A typo is a handler that is never invoked,
 *    not a compile error — which is the whole reason `LiveHub` exists in the first place. Restating
 *    even one of them in Swift would put a second spelling on the platform.
 *
 * So this is the same job [lk.mageride.shared.mqtt.IosMqttPlan] does for the position plane: the
 * Kotlin side computes everything the contract fixes, and the Swift transport spells no name of its
 * own. Nothing here adds a rule — every value is read from [LiveHub].
 */
public class IosLiveHub {

    /** The hub endpoint, relative to the gateway base URL. */
    public val path: String = LiveHub.PATH

    /** The query parameter the access token travels in (SignalR's own convention). */
    public val accessTokenQueryParam: String = LiveHub.ACCESS_TOKEN_QUERY_PARAM

    /** Client → server ping interval, in seconds. */
    public val keepAliveSeconds: Double = LiveHub.KEEP_ALIVE.inWholeMilliseconds / MILLIS_PER_SECOND

    /** How long the client waits for a server message before treating the connection as dead. */
    public val serverTimeoutSeconds: Double = LiveHub.SERVER_TIMEOUT.inWholeMilliseconds / MILLIS_PER_SECOND

    // Client → server methods (`signalr-hub.md` §2).

    /** `JoinGeocells(cells: string[])`. */
    public val methodJoinGeocells: String = LiveHub.Method.JOIN_GEOCELLS

    /** `LeaveGeocells(cells: string[])`. */
    public val methodLeaveGeocells: String = LiveHub.Method.LEAVE_GEOCELLS

    /** `SubscribeRide(rideId)`. */
    public val methodSubscribeRide: String = LiveHub.Method.SUBSCRIBE_RIDE

    /** `SubscribeLocRequest(requestId)`. */
    public val methodSubscribeLocRequest: String = LiveHub.Method.SUBSCRIBE_LOC_REQUEST

    // Server → client events (`signalr-hub.md` §3).

    public val eventVehiclePositions: String = LiveHub.Event.VEHICLE_POSITIONS
    public val eventVehicleRemoved: String = LiveHub.Event.VEHICLE_REMOVED
    public val eventRideStateChanged: String = LiveHub.Event.RIDE_STATE_CHANGED
    public val eventDriverPosition: String = LiveHub.Event.DRIVER_POSITION
    public val eventLocationRequestResolved: String = LiveHub.Event.LOCATION_REQUEST_RESOLVED
    public val eventShareRevoked: String = LiveHub.Event.SHARE_REVOKED
    public val eventPackageStatus: String = LiveHub.Event.PACKAGE_STATUS

    /**
     * All seven server → client events, in `signalr-hub.md` §3's order.
     *
     * The set a client subscribes to. Listed here rather than assembled in Swift so that an event
     * added to the contract reaches both platforms by the same edit.
     */
    public val eventNames: List<String> = listOf(
        eventVehiclePositions,
        eventVehicleRemoved,
        eventRideStateChanged,
        eventDriverPosition,
        eventLocationRequestResolved,
        eventShareRevoked,
        eventPackageStatus,
    )

    private companion object {
        const val MILLIS_PER_SECOND = 1000.0
    }
}

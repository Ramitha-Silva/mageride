package lk.mageride.passenger.live

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.realtime.LiveHub
import lk.mageride.shared.realtime.ShareRevoked
import lk.mageride.shared.realtime.VehicleFrame
import lk.mageride.shared.realtime.VehicleRemoved
import lk.mageride.shared.serialization.MageRideJson

/**
 * Everything the server says, turned into what the map shows.
 *
 * The socket carries deltas and `GET /v1/nearby` carries snapshots, and both end up in the same
 * [LiveVehicleStore] — which is why the resync lives here rather than beside the connection. From
 * a screen's point of view there is one answer to "what is nearby", and it does not matter which
 * transport most recently updated it.
 *
 * **Payloads arrive as JSON text and are decoded by `MageRideJson`.** The SignalR Java client's
 * hub protocol is Gson, and Gson binds an enum by its Kotlin `name()`; C012's enums carry
 * `@SerialName` wire spellings that differ (`three_wheeler`, not `THREE_WHEELER`). Decoding with
 * the platform's own `Json` is what lets the socket and the REST surface share one set of models,
 * which is what `signalr-hub.md` §3 asks for. See [SignalRLiveHubTransport].
 *
 * @param query query-svc, for the snapshot half of D6' §5.4's recovery.
 * @param grid The platform H3 engine, for pruning vehicles in cells the client has left.
 */
internal class LiveHubInbox(private val query: QueryApi, private val grid: H3Grid) {

    private val store = LiveVehicleStore()
    private val mutex = Mutex()

    private val mutableVehicles = MutableStateFlow<List<VehicleFrame>>(emptyList())
    private val mutableEvents = MutableSharedFlow<LiveEvent>(extraBufferCapacity = EVENT_BUFFER)

    /** What is on the map. Replaced wholesale per batch — see [LiveVehicleStore]. */
    val vehicles: StateFlow<List<VehicleFrame>> = mutableVehicles.asStateFlow()

    /** The four directed payloads. See [LiveEvent]. */
    val events: SharedFlow<LiveEvent> = mutableEvents.asSharedFlow()

    /**
     * One hub event.
     *
     * Every decode is wrapped, because a malformed or unrecognised payload must not take down the
     * callback that every other event also arrives on. `MageRideJson` has `ignoreUnknownKeys`, so
     * a field added server-side is not malformed — what this actually guards is a contract change
     * big enough to change a *shape*, which is a deploy problem rather than a reason to lose the
     * map until the app is restarted.
     */
    suspend fun onEvent(name: String, payload: String) {
        runCatching { dispatch(name, payload) }
    }

    /**
     * The snapshot half of the recovery — *"the socket carries deltas, the REST read carries the
     * state"*.
     *
     * A failure is swallowed: the socket is already up and the next per-cell batch is at most
     * eight seconds away, so a failed resync costs freshness rather than the map. The radius is
     * the R-06 view's own ~3 km, which is what the nineteen cells cover.
     */
    suspend fun resync(around: GeoPoint) {
        runCatching { query.getNearbyVehicles(lat = around.lat, lng = around.lng, radiusMetres = SNAPSHOT_RADIUS_M) }
            .onSuccess { response ->
                mutex.withLock { store.onSnapshot(response.vehicles) }
                publish()
            }
    }

    /** Drops every vehicle whose own res-7 cell is no longer subscribed. See [LiveVehicleStore]. */
    suspend fun retainCells(cells: Set<H3Cell>) {
        if (mutex.withLock { store.retainCells(cells, grid) }) publish()
    }

    /** Forgets the map. The passenger signed out, or the app is going. */
    suspend fun clear() {
        mutex.withLock { store.clear() }
        publish()
    }

    private suspend fun dispatch(name: String, payload: String) {
        when (name) {
            LiveHub.Event.VEHICLE_POSITIONS -> {
                val frames = MageRideJson.decodeFromString<List<VehicleFrame>>(payload)
                if (mutex.withLock { store.onPositions(frames) }) publish()
            }

            LiveHub.Event.VEHICLE_REMOVED -> {
                val removed = MageRideJson.decodeFromString<VehicleRemoved>(payload)
                if (mutex.withLock { store.onRemoved(removed) }) publish()
            }

            LiveHub.Event.SHARE_REVOKED -> {
                val revoked = MageRideJson.decodeFromString<ShareRevoked>(payload)
                if (mutex.withLock { store.onShareRevoked(revoked) }) publish()
            }

            LiveHub.Event.RIDE_STATE_CHANGED ->
                mutableEvents.emit(LiveEvent.RideState(MageRideJson.decodeFromString(payload)))

            LiveHub.Event.DRIVER_POSITION ->
                mutableEvents.emit(LiveEvent.DriverMoved(MageRideJson.decodeFromString(payload)))

            LiveHub.Event.LOCATION_REQUEST_RESOLVED ->
                mutableEvents.emit(LiveEvent.LocationRequest(MageRideJson.decodeFromString(payload)))

            LiveHub.Event.PACKAGE_STATUS ->
                mutableEvents.emit(LiveEvent.PackageStatus(MageRideJson.decodeFromString(payload)))
        }
    }

    private suspend fun publish() {
        mutableVehicles.value = mutex.withLock { store.snapshot() }
    }

    internal companion object {

        /**
         * The `GET /v1/nearby` radius, in metres.
         *
         * The R-06 view is res-7 + `ring(2)`, which reaches 2.8–3.3 km; 3 km is inside the
         * contract's 100–20 000 m bounds and matches what the nineteen cells actually cover. The
         * cells remain the subscription — this is only what the resync asks for.
         */
        const val SNAPSHOT_RADIUS_M: Int = 3_000

        /**
         * How many directed events may queue for a screen that is slow to collect.
         *
         * `emit` suspends rather than overflowing, so nothing is dropped; the buffer is what keeps
         * a `RideStateChanged` burst mid-accept from serialising against a view model's fold.
         */
        private const val EVENT_BUFFER = 8
    }
}

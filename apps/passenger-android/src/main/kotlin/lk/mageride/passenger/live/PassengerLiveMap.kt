package lk.mageride.passenger.live

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.domain.geo.H3GridUnavailableException
import lk.mageride.shared.realtime.DriverPosition
import lk.mageride.shared.realtime.LiveHub
import lk.mageride.shared.realtime.LocationRequestResolved
import lk.mageride.shared.realtime.PackageStatusChanged
import lk.mageride.shared.realtime.RideStateChanged
import lk.mageride.shared.realtime.VehicleFrame
import lk.mageride.shared.util.ReconnectBackoff
import kotlin.time.Clock

/** Where the live plane is. Feeds SCR-PA-032's banner alongside the connectivity monitor. */
internal enum class LiveStatus {
    /** No socket, and none being opened — nobody has called [PassengerLiveMap.connect] yet. */
    Disconnected,

    /** Dialling, or waiting out a backoff before dialling again. */
    Connecting,

    /** Up, groups joined, snapshot back-filled. */
    Connected,
}

/**
 * The four directed payloads a screen subscribes to.
 *
 * `VehiclePositions`, `VehicleRemoved` and `ShareRevoked` are deliberately absent: all three are
 * about *what is on the map*, and the map reads [PassengerLiveMap.vehicles] rather than replaying
 * a stream to rebuild a set the store already holds. `ShareRevoked` also reaches a screen through
 * the vehicle disappearing, which is the only consequence it has.
 */
internal sealed interface LiveEvent {

    /** A ride-aggregate transition — SCR-PA-014/015/017/020's own state. */
    data class RideState(val payload: RideStateChanged) : LiveEvent

    /** The assigned driver's live position (US-6A.12) — SCR-PA-015's moving marker. */
    data class DriverMoved(val payload: DriverPosition) : LiveEvent

    /** The proxy round-trip resolving (P-02, P-13) — SCR-PA-010b's pin auto-filling. */
    data class LocationRequest(val payload: LocationRequestResolved) : LiveEvent

    /** Package handoff progress (US-20.7) — SCR-PA-020/021's four-step bar. */
    data class PackageStatus(val payload: PackageStatusChanged) : LiveEvent
}

/**
 * The passenger's whole real-time plane: one `/hubs/live` connection for the process.
 *
 * Three concerns, three objects, and this is the one that owns the socket:
 *
 * - [HubSubscriptions] — which groups the client is in: R-06's nineteen cells, the 30 s boundary
 *   hysteresis, and D6' §5.4's recovery plan.
 * - [LiveHubInbox] — what the server said, decoded into the map's own state, whether it arrived as
 *   a socket frame or as a `GET /v1/nearby` snapshot.
 * - this class — dialling, staying dialled, and the facade a screen sees.
 *
 * **Reconnection is ours, because the Java client has none.** See [SignalRLiveHubTransport]: there
 * is no `withAutomaticReconnect()` on `HttpHubConnectionBuilder`, unlike the JavaScript and .NET
 * clients. So [connect] runs a supervision loop over `ReconnectBackoff` — jittered exponential,
 * 1–60 s, ±25 % (R-09), the same curve the driver app's MQTT client uses, because a regional
 * outage ends for both planes at the same instant. The first retry therefore lands inside 1.25 s,
 * which is what makes the recovery fit in the five seconds SCR-PA-032 promises.
 *
 * @param transport The socket. Faked in tests; see [LiveHubTransport].
 * @param query query-svc, for the snapshot half of the recovery.
 * @param grid The platform H3 engine. Cell ids must be bit-identical to the ones
 *   `position-processor-svc` computes, or the client joins groups nothing publishes to — a failure
 *   that looks exactly like an empty map with no error anywhere.
 * @param scope The lifetime of the connection. The app graph gives it a process-wide one.
 * @param now Wall clock, injected — the boundary hysteresis is a comparison against it.
 * @param newBackoff Overridable so a test can assert the curve rather than sleep through it.
 */
@Suppress("TooManyFunctions") // Nine entry points a screen calls, the supervision loop, one guard.
internal class PassengerLiveMap(
    private val transport: LiveHubTransport,
    query: QueryApi,
    grid: H3Grid,
    private val scope: CoroutineScope,
    now: () -> Timestamp = { Clock.System.now() },
    private val newBackoff: () -> ReconnectBackoff = { ReconnectBackoff() },
) {

    private val inbox = LiveHubInbox(query, grid)

    private val subscriptions = HubSubscriptions(grid, now) { method, arg ->
        // Dropped rather than thrown when the socket is down: every send this app makes is a group
        // membership, and group membership is re-established wholesale by `restore()` on the next
        // connect. Throwing here would make each caller handle a case that already has one answer.
        runCatching { transport.send(method, arg) }
    }

    private val mutableStatus = MutableStateFlow(LiveStatus.Disconnected)

    /** What is on the map. */
    val vehicles: StateFlow<List<VehicleFrame>> get() = inbox.vehicles

    /** The four directed payloads. */
    val events: SharedFlow<LiveEvent> get() = inbox.events

    /** The cells currently joined. **Nineteen** once a position has been fed (R-06). */
    val cells: StateFlow<Set<H3Cell>> get() = subscriptions.cells

    /** Where the socket is. */
    val status: StateFlow<LiveStatus> = mutableStatus.asStateFlow()

    /** Signalled by the transport's `onClosed`; awaited by the supervision loop. */
    private val closed = Channel<Unit>(capacity = Channel.CONFLATED)

    private var supervisor: Job? = null

    @Volatile
    private var lastPoint: GeoPoint? = null

    /**
     * Set once the platform turns out to have no H3 engine, after which the geocell plane is off
     * for the life of the process.
     *
     * A missing native library never turns up later, so retrying on every fix would be one crash
     * per second rather than one. What is lost is the nineteen-cell subscription and with it every
     * vehicle on the map; what is KEPT is the rest of the app — the socket, the ride and package
     * events, booking, and the passenger's own blue dot, none of which need a cell id. Killing the
     * process instead loses all of that too.
     *
     * `com.uber:h3` ships arm64 and arm natives only, so an x86_64 emulator is the ordinary way to
     * reach this; see `extractH3Natives` in the app's build script.
     */
    @Volatile
    private var gridUnavailable = false

    /**
     * Opens the connection and keeps it open until [disconnect].
     *
     * Idempotent: a second call while a supervisor is already running does nothing, which is what
     * lets the shell call it on every composition without tracking whether it already has.
     */
    fun connect() {
        if (supervisor?.isActive == true) return
        supervisor = scope.launch { supervise() }
    }

    /** Closes the connection and forgets the map. The passenger signed out, or the app is going. */
    suspend fun disconnect() {
        supervisor?.cancelAndJoin()
        supervisor = null
        transport.close()
        lastPoint = null
        subscriptions.reset()
        inbox.clear()
        mutableStatus.value = LiveStatus.Disconnected
    }

    /** Feeds a position fix — the R-06 subscription's only input. See [HubSubscriptions.onPosition]. */
    fun onPosition(point: GeoPoint) {
        scope.launch {
            lastPoint = point
            geocells { subscriptions.onPosition(point)?.let { inbox.retainCells(it) } }
        }
    }

    /** Re-evaluates a held boundary crossing without a new fix. See [HubSubscriptions.refresh]. */
    fun refreshCells() {
        scope.launch {
            geocells { subscriptions.refresh()?.let { inbox.retainCells(it) } }
        }
    }

    /**
     * Drops a vehicle the passenger has just unsubscribed from (AL-25, US-4.11).
     *
     * **Not a hub call.** `signalr-hub.md` §2 has four client → server methods and none of them
     * leaves a `vehicle:{vehicleId}` group — membership is the server's, granted at join from the
     * `share:{userId}` entitlement (D-23). What this does is erase the marker already drawn, so
     * *"unsubscribing removes the vehicle from the live map within seconds"* is true on the tap
     * rather than on the round trip. fanout-svc's `share.revoked` arrives behind it and finds
     * nothing left to remove, which is the correct no-op. See [LiveVehicleStore.drop].
     */
    fun dropVehicle(vehicleId: Ulid) {
        scope.launch { inbox.drop(vehicleId) }
    }

    /** `SubscribeRide` — the caller's own ride (US-6A.12). Rejoined on every reconnect. */
    fun watchRide(rideId: Ulid) {
        scope.launch { subscriptions.watchRide(rideId) }
    }

    /** Stops rejoining [rideId] on reconnect. */
    fun stopWatchingRide(rideId: Ulid) {
        scope.launch { subscriptions.stopWatchingRide(rideId) }
    }

    /** `SubscribeLocRequest` — the booker awaiting a rider's confirmation (P-13). */
    fun watchLocationRequest(requestId: Ulid) {
        scope.launch { subscriptions.watchLocationRequest(requestId) }
    }

    /** Stops rejoining [requestId]. Called on resolve, or on the 300 s expiry (P-02). */
    fun stopWatchingLocationRequest(requestId: Ulid) {
        scope.launch { subscriptions.stopWatchingLocationRequest(requestId) }
    }

    /**
     * Runs the one step that needs the H3 engine, and shuts the geocell plane down for good if the
     * platform has none. See [gridUnavailable].
     *
     * The scope this class is given is `SupervisorJob() + Dispatchers.Default`, and a supervisor
     * job stops a failed child cancelling its siblings — it does NOT stop the failure reaching the
     * thread's default handler. Every `H3Grid` call in this class is inside a `scope.launch`, so
     * without this an absent native library is a `FATAL EXCEPTION` on a dispatcher worker.
     */
    private suspend fun geocells(step: suspend () -> Unit) {
        if (gridUnavailable) return
        try {
            step()
        } catch (noGrid: H3GridUnavailableException) {
            gridUnavailable = true
            // The one thing worse than an empty map is an empty map with no error anywhere.
            Log.e(TAG, "No H3 engine on this device — the live map will show no vehicles.", noGrid)
        }
    }

    private suspend fun supervise() {
        val backoff = newBackoff()
        while (currentCoroutineContext().isActive) {
            mutableStatus.value = LiveStatus.Connecting

            // The event callback runs on a SignalR thread, so everything it does is handed to
            // [scope]; the decode itself is the inbox's.
            val opened = runCatching {
                transport.connect(
                    events = HUB_EVENTS,
                    onEvent = { name, payload -> scope.launch { inbox.onEvent(name, payload) } },
                    onClosed = { closed.trySend(Unit) },
                )
            }.isSuccess

            if (!opened) {
                delay(backoff.next())
                continue
            }

            // Opening a connection closes the previous one, and *that* fires its own `onClosed`.
            // Without this drain the signal from the socket just replaced would be waiting in the
            // conflated channel and the receive below would return immediately — a healthy
            // connection torn down one frame after it came up, forever.
            while (closed.tryReceive().isSuccess) {
                // Drain only.
            }

            backoff.reset()
            mutableStatus.value = LiveStatus.Connected
            if (subscriptions.restore()) lastPoint?.let { inbox.resync(it) }

            // Suspends until the transport reports a drop. Nothing polls: SignalR's own keep-alive
            // (15 s) and server timeout (30 s) are what detect a dead socket, and both are set on
            // the connection from `LiveHub`'s constants.
            closed.receive()

            mutableStatus.value = LiveStatus.Disconnected
            delay(backoff.next())
        }
    }

    internal companion object {

        private const val TAG = "PassengerLiveMap"

        /** The seven server → client events of `signalr-hub.md` §3. */
        val HUB_EVENTS: Set<String> = setOf(
            LiveHub.Event.VEHICLE_POSITIONS,
            LiveHub.Event.VEHICLE_REMOVED,
            LiveHub.Event.RIDE_STATE_CHANGED,
            LiveHub.Event.DRIVER_POSITION,
            LiveHub.Event.LOCATION_REQUEST_RESOLVED,
            LiveHub.Event.SHARE_REVOKED,
            LiveHub.Event.PACKAGE_STATUS,
        )
    }
}

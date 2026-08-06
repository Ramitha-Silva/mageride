package lk.mageride.passenger.live

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.geo.CellSubscriptionUpdate
import lk.mageride.shared.domain.geo.GeoCellSubscription
import lk.mageride.shared.domain.geo.GeoView
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.realtime.LiveHub
import lk.mageride.shared.realtime.LiveHubRecovery
import lk.mageride.shared.realtime.LiveMapScope
import lk.mageride.shared.realtime.RecoveryStep

/**
 * Everything this client is subscribed to on `/hubs/live`, and every rule for changing it.
 *
 * **Three memberships, three lifetimes.** The geocells follow the passenger and are held still for
 * thirty seconds after a boundary crossing (ADD §7.4 step 6); a ride group lives as long as the
 * ride; a location-request group lives for P-02's three hundred seconds. Keeping all three here —
 * rather than beside the socket — is what makes the reconnect plan a *read* of this object instead
 * of state scattered across the connection loop.
 *
 * **Nothing here subscribes to a vehicle.** `vehicle:{vehicleId}` groups exist, and
 * `signalr-hub.md` §2.1 says they are *"joined by the server, never asked for"*: a Mode B vehicle
 * appears because fanout-svc checked the `share:{userId}` entitlement at join (D-23), not because
 * a client asked. There is no `SubscribeVehicle` method, and this class is where inventing one
 * would have to happen.
 *
 * Every method takes the mutex, because `GeoCellSubscription`'s own KDoc says to drive it from one
 * coroutine and a position fix, a ride and a reconnect all arrive from different ones.
 *
 * @param grid The platform H3 engine. Cell ids must be bit-identical to the ones
 *   `position-processor-svc` computes, or the client joins groups nothing publishes to — a failure
 *   that looks exactly like an empty map with no error anywhere.
 * @param now Wall clock, injected: the hysteresis is a comparison against it, and a test that
 *   could only wait for real time would have to sleep for thirty seconds to assert the rule.
 * @param send The hub invocation. Wrapped by the caller so a send on a dead socket is a no-op —
 *   group membership is re-established wholesale by [restore] on the next connect.
 */
internal class HubSubscriptions(
    private val grid: H3Grid,
    private val now: () -> Timestamp,
    private val send: suspend (String, Any) -> Unit,
) {

    private val geocells = GeoCellSubscription(grid, GeoView.PASSENGER_3KM)
    private val rides = LinkedHashSet<Ulid>()
    private val locationRequests = LinkedHashSet<Ulid>()
    private val mutex = Mutex()

    private val mutableCells = MutableStateFlow<Set<H3Cell>>(emptySet())

    /** The cells currently joined. **Nineteen** once a position has been fed (R-06). */
    val cells: StateFlow<Set<H3Cell>> = mutableCells.asStateFlow()

    /**
     * Feeds a position fix — the R-06 subscription's only input.
     *
     * The first fix joins nineteen cells. Every later one is compared against the cell already
     * being served: staying inside it changes nothing, and a crossing is applied at most once per
     * `GeoCells.BOUNDARY_HYSTERESIS` (30 s) so a passenger standing on a cell edge does not thrash
     * group membership — every join and leave is a `RemoveFromGroupAsync` on the backplane. All
     * three rules are `GeoCellSubscription`'s; this only sends what it decides.
     *
     * @return The new cell set when membership changed, `null` when it did not.
     */
    suspend fun onPosition(point: GeoPoint): Set<H3Cell>? = apply(mutex.withLock { geocells.onPosition(point, now()) })

    /**
     * Re-evaluates a held boundary crossing without a new fix.
     *
     * A passenger who crosses a cell edge and then **stops moving** still has to end up subscribed
     * to the right nineteen cells, and a stationary handset is exactly when the fused provider
     * stops producing fixes. The map's own tick calls this.
     */
    suspend fun refresh(): Set<H3Cell>? = apply(mutex.withLock { geocells.refresh(now()) })

    /** `SubscribeRide` — the caller's own ride (US-6A.12). Rejoined by [restore] on every reconnect. */
    suspend fun watchRide(rideId: Ulid) {
        if (mutex.withLock { rides.add(rideId) }) send(LiveHub.Method.SUBSCRIBE_RIDE, rideId)
    }

    /** Stops rejoining [rideId]. The hub has no unsubscribe for a ride group; this is the client half. */
    suspend fun stopWatchingRide(rideId: Ulid) {
        mutex.withLock { rides.remove(rideId) }
    }

    /** `SubscribeLocRequest` — the booker awaiting a rider's confirmation (P-13). */
    suspend fun watchLocationRequest(requestId: Ulid) {
        if (mutex.withLock { locationRequests.add(requestId) }) {
            send(LiveHub.Method.SUBSCRIBE_LOC_REQUEST, requestId)
        }
    }

    /** Stops rejoining [requestId]. Called on resolve, or on the 300 s expiry (P-02). */
    suspend fun stopWatchingLocationRequest(requestId: Ulid) {
        mutex.withLock { locationRequests.remove(requestId) }
    }

    /**
     * D6' §5.4 / `signalr-hub.md` §1.1 — rejoins every group after a reconnect.
     *
     * `GeoCellSubscription.onReconnected()` deliberately ignores the hysteresis window: after a
     * drop the server holds no membership at all, and rate-limiting recovery would leave the map
     * blank for up to thirty seconds.
     *
     * @return `true` when the caller should now resync from `GET /v1/nearby`. The order is the
     *   contract, not a preference: a client that snapshots *first* loses every frame published
     *   between the two calls, and those are exactly the frames that moved while it was away.
     */
    suspend fun restore(): Boolean {
        val plan = mutex.withLock {
            val restored = geocells.onReconnected()
            mutableCells.value = restored.cells
            LiveHubRecovery.plan(
                scope = LiveMapScope.PassengerView(restored.cells),
                activeRides = rides.toSet(),
                pendingLocationRequests = locationRequests.toSet(),
            )
        }

        var snapshotDue = false
        plan.forEach { step ->
            when (step) {
                is RecoveryStep.JoinGeocells -> send(LiveHub.Method.JOIN_GEOCELLS, step.cells.tokens())

                is RecoveryStep.SubscribeRide -> send(LiveHub.Method.SUBSCRIBE_RIDE, step.rideId)

                is RecoveryStep.SubscribeLocationRequest ->
                    send(LiveHub.Method.SUBSCRIBE_LOC_REQUEST, step.requestId)

                RecoveryStep.ResyncNearbySnapshot -> snapshotDue = true
            }
        }
        return snapshotDue
    }

    /** Forgets everything. The passenger signed out, or the app is going. */
    suspend fun reset() {
        mutex.withLock {
            geocells.reset()
            rides.clear()
            locationRequests.clear()
        }
        mutableCells.value = emptySet()
    }

    private suspend fun apply(update: CellSubscriptionUpdate): Set<H3Cell>? {
        mutableCells.value = update.cells
        if (!update.changed) return null

        // Leave first, then join. The two sets are disjoint, so the order is not a correctness
        // matter — but leaving first is what keeps the server's per-connection group count at
        // nineteen rather than briefly at twenty-five while a crossing is applied.
        if (update.leave.isNotEmpty()) send(LiveHub.Method.LEAVE_GEOCELLS, update.leave.tokens())
        if (update.join.isNotEmpty()) send(LiveHub.Method.JOIN_GEOCELLS, update.join.tokens())
        return update.cells
    }

    /** The wire form of a cell set — `JoinGeocells(cells: string[])`. */
    private fun Set<H3Cell>.tokens(): Array<String> = map(H3Cell::token).toTypedArray()
}

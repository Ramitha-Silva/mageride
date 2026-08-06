package lk.mageride.passenger.live

import lk.mageride.passenger.map.MapVehicle
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.NearbyVehicle
import lk.mageride.shared.domain.geo.GeoCells
import lk.mageride.shared.domain.geo.H3Cell
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.realtime.ShareRevoked
import lk.mageride.shared.realtime.VehicleFrame
import lk.mageride.shared.realtime.VehicleRemoved

/**
 * What is on the passenger's map right now.
 *
 * **The socket carries deltas, so the client holds the state.** `signalr-hub.md` §4 is explicit:
 * *"batches carry only what moved, so a client that inferred removal from absence would erase
 * every stationary vehicle on every tick"*. A vehicle therefore stays until something says
 * otherwise, and there are exactly four things that can:
 *
 * 1. **`VehicleRemoved`** — stale, offline, or gone on hire (US-7.16/7.17). All three are facts
 *    about the vehicle, decided once per frame by fanout-svc, and all three mean *drop it now*.
 * 2. **`ShareRevoked`** — a Mode B entitlement was withdrawn (D-22). The platform sends a directed
 *    `RemoveFromGroupAsync` in under 200 ms so no further frames arrive; this drops the marker
 *    that is already drawn, which is the half only the client can do. [drop] is the same erasure
 *    performed locally when the passenger themselves unsubscribed (AL-25, C082).
 * 3. **A cell the client left.** fanout-svc emits no removal for a group the client is no longer
 *    in — it has nothing to say to a group it cannot reach — so a passenger who travelled 3 km
 *    would otherwise accumulate every vehicle they had ever seen. [retainCells] is the answer, and
 *    it runs on the same subscription change that sent `LeaveGeocells`.
 * 4. **A resync.** `GET /v1/nearby` is a snapshot, not a delta, so it *replaces* the contents.
 *
 * Every mutator answers whether anything changed, so the caller publishes one state update per
 * batch instead of one per vehicle.
 *
 * Not thread-safe: [PassengerLiveMap] drives it from a single coroutine behind its own mutex.
 */
internal class LiveVehicleStore {

    private val visible = LinkedHashMap<Ulid, VehicleFrame>()

    /** Everything on the map, in the order it was first seen. */
    fun snapshot(): List<VehicleFrame> = visible.values.toList()

    /** A `VehiclePositions` batch. */
    fun onPositions(frames: List<VehicleFrame>): Boolean {
        if (frames.isEmpty()) return false
        frames.forEach { visible[it.vehicleId] = it }
        return true
    }

    /** A `VehicleRemoved`. All three reasons drop the marker; only the copy would differ. */
    fun onRemoved(event: VehicleRemoved): Boolean = visible.remove(event.vehicleId) != null

    /** A `ShareRevoked` — D-22's directed revocation. */
    fun onShareRevoked(event: ShareRevoked): Boolean = visible.remove(event.vehicleId) != null

    /**
     * Drops one vehicle because **this passenger** withdrew their own entitlement (AL-25).
     *
     * The same erasure `onShareRevoked` performs, reached without the socket. An unsubscribe is
     * the one revocation the client learns about *first* — it made it — and `share.revoked` comes
     * back through fanout-svc as confirmation rather than as news. Waiting for it would leave a
     * marker the passenger no longer has a grant for on screen for as long as the round trip
     * takes, and forever if the socket happens to be down at that moment.
     *
     * It cannot resurrect: the grant is gone, so neither a later batch nor a `GET /v1/nearby`
     * resync will carry the vehicle again.
     */
    fun drop(vehicleId: Ulid): Boolean = visible.remove(vehicleId) != null

    /**
     * Replaces the contents with a `GET /v1/nearby` snapshot (D6' §5.4's resync).
     *
     * A replace rather than a merge: the snapshot is authoritative about what is visible *now*,
     * and a vehicle the client held from before a disconnection but which is absent from the
     * snapshot is a vehicle that went stale, went on hire or had its share revoked while the
     * socket was down — none of whose removals the client can have heard.
     *
     * `NearbyVehicle` and `VehicleFrame` are two shapes of one fact (query-svc's snapshot and
     * fanout-svc's delta), and this is the one place they meet. `driverName`, `etaSeconds` and
     * `registrationNumber` are dropped on the way in: the map draws none of them, and carrying a
     * field the socket cannot refresh would leave a stale name beside a live position.
     */
    fun onSnapshot(vehicles: List<NearbyVehicle>): Boolean {
        visible.clear()
        vehicles.forEach { vehicle ->
            visible[vehicle.vehicleId] = VehicleFrame(
                vehicleId = vehicle.vehicleId,
                lat = vehicle.lat,
                lng = vehicle.lng,
                heading = vehicle.heading,
                speed = vehicle.speed,
                type = vehicle.type,
                mode = vehicle.mode,
            )
        }
        return true
    }

    /**
     * Drops every vehicle whose own res-7 cell is no longer subscribed.
     *
     * The cell is recomputed from the vehicle's position through the platform grid rather than
     * remembered from the batch that delivered it: a vehicle moves between cells while the
     * passenger stands still, and the cell that *delivered* a frame is not necessarily the cell
     * the vehicle is in by the time the passenger moves away from it.
     */
    fun retainCells(cells: Set<H3Cell>, grid: H3Grid): Boolean {
        if (cells.isEmpty()) return false
        val gone = visible.values
            .filterNot { grid.cellAt(it.point, GeoCells.VIEW_RESOLUTION) in cells }
            .map(VehicleFrame::vehicleId)
        gone.forEach(visible::remove)
        return gone.isNotEmpty()
    }

    /** Forgets everything — the map was closed, or the passenger signed out. */
    fun clear(): Boolean {
        if (visible.isEmpty()) return false
        visible.clear()
        return true
    }
}

/**
 * The frame as the map draws it (MAP-03, MAP-06).
 *
 * The conversion lives beside the store rather than in the map package because it is where the
 * *live* plane's vocabulary ends: everything above this line thinks in `VehicleFrame`s from a
 * contract, and everything below it thinks in markers. `heading` defaults to 0° when the vehicle
 * did not report one — an arrow pointing north is wrong in a way a passenger can see and ignore,
 * where no marker at all reads as no vehicle.
 */
internal fun VehicleFrame.toMapVehicle(): MapVehicle = MapVehicle(
    vehicleId = vehicleId,
    lat = lat,
    lng = lng,
    heading = (heading ?: 0).toDouble(),
    type = type,
)

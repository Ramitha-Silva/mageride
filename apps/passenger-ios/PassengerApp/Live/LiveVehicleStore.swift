import Foundation
import MageRideShared

/// What is on the passenger's map right now.
///
/// **The socket carries deltas, so the client holds the state.** `signalr-hub.md` §4 is explicit:
/// *"batches carry only what moved, so a client that inferred removal from absence would erase
/// every stationary vehicle on every tick"*. A vehicle therefore stays until something says
/// otherwise, and there are exactly four things that can:
///
/// 1. **`VehicleRemoved`** — stale, offline, or gone on hire (US-7.16/7.17). All three are facts
///    about the vehicle, decided once per frame by fanout-svc, and all three mean *drop it now*.
/// 2. **`ShareRevoked`** — a Mode B entitlement was withdrawn (D-22). The platform sends a directed
///    `RemoveFromGroupAsync` in under 200 ms so no further frames arrive; this drops the marker
///    that is already drawn, which is the half only the client can do. ``drop(_:)`` is the same
///    erasure performed locally when the passenger themselves unsubscribed (AL-25, C100).
/// 3. **A cell the client left.** fanout-svc emits no removal for a group the client is no longer
///    in — it has nothing to say to a group it cannot reach — so a passenger who travelled 3 km
///    would otherwise accumulate every vehicle they had ever seen. ``retain(cells:grid:)`` is the
///    answer, and it runs on the same subscription change that sent `LeaveGeocells`.
/// 4. **A resync.** `GET /v1/nearby` is a snapshot, not a delta, so it *replaces* the contents.
///
/// Every mutator answers whether anything changed, so the caller publishes one state update per
/// batch instead of one per vehicle.
///
/// Not thread-safe: ``LiveHubInbox`` is an actor and drives it from there.
struct LiveVehicleStore {

    /// Insertion-ordered, which is what `LinkedHashMap` gives the Android twin. Swift's `Dictionary`
    /// is unordered, so the order is kept explicitly — a map whose markers reshuffled on every batch
    /// would make MAP-04's interpolation restart from the wrong track.
    private var order: [String] = []
    private var visible: [String: VehicleFrame] = [:]

    /// Everything on the map, in the order it was first seen.
    func snapshot() -> [VehicleFrame] {
        order.compactMap { visible[$0] }
    }

    /// A `VehiclePositions` batch.
    mutating func onPositions(_ frames: [VehicleFrame]) -> Bool {
        guard !frames.isEmpty else { return false }
        for frame in frames {
            if visible[frame.vehicleId] == nil { order.append(frame.vehicleId) }
            visible[frame.vehicleId] = frame
        }
        return true
    }

    /// A `VehicleRemoved`. All three reasons drop the marker; only the copy would differ.
    mutating func onRemoved(_ event: VehicleRemoved) -> Bool {
        remove(event.vehicleId)
    }

    /// A `ShareRevoked` — D-22's directed revocation.
    mutating func onShareRevoked(_ event: ShareRevoked) -> Bool {
        remove(event.vehicleId)
    }

    /// Drops one vehicle because **this passenger** withdrew their own entitlement (AL-25).
    ///
    /// The same erasure ``onShareRevoked(_:)`` performs, reached without the socket. An unsubscribe
    /// is the one revocation the client learns about *first* — it made it — and `share.revoked` comes
    /// back through fanout-svc as confirmation rather than as news. Waiting for it would leave a
    /// marker the passenger no longer has a grant for on screen for as long as the round trip takes,
    /// and for ever if the socket happens to be down at that moment.
    ///
    /// It cannot resurrect: the grant is gone, so neither a later batch nor a `GET /v1/nearby`
    /// resync will carry the vehicle again.
    mutating func drop(_ vehicleId: String) -> Bool {
        remove(vehicleId)
    }

    /// Replaces the contents with a `GET /v1/nearby` snapshot (D6' §5.4's resync).
    ///
    /// A replace rather than a merge: the snapshot is authoritative about what is visible *now*, and
    /// a vehicle the client held from before a disconnection but which is absent from the snapshot
    /// is a vehicle that went stale, went on hire or had its share revoked while the socket was
    /// down — none of whose removals the client can have heard.
    ///
    /// `NearbyVehicle` and `VehicleFrame` are two shapes of one fact (query-svc's snapshot and
    /// fanout-svc's delta), and this is the one place they meet. `driverName`, `etaSeconds` and
    /// `registrationNumber` are dropped on the way in: the map draws none of them, and carrying a
    /// field the socket cannot refresh would leave a stale name beside a live position.
    mutating func onSnapshot(_ vehicles: [NearbyVehicle]) -> Bool {
        order.removeAll()
        visible.removeAll()
        for vehicle in vehicles {
            order.append(vehicle.vehicleId)
            visible[vehicle.vehicleId] = VehicleFrame(
                vehicleId: vehicle.vehicleId,
                lat: vehicle.lat,
                lng: vehicle.lng,
                heading: vehicle.heading,
                speed: vehicle.speed,
                type: vehicle.type,
                mode: vehicle.mode
            )
        }
        return true
    }

    /// Drops every vehicle whose own res-7 cell is no longer subscribed.
    ///
    /// The cell is recomputed from the vehicle's position through the platform grid rather than
    /// remembered from the batch that delivered it: a vehicle moves between cells while the
    /// passenger stands still, and the cell that *delivered* a frame is not necessarily the cell the
    /// vehicle is in by the time the passenger moves away from it.
    mutating func retain(cells: Set<H3Cell>, grid: H3Grid) -> Bool {
        guard !cells.isEmpty else { return false }
        let gone = visible.values
            .filter { !cells.contains(grid.cellAt(point: $0.point, resolution: GeoCells.shared.VIEW_RESOLUTION)) }
            .map(\.vehicleId)
        guard !gone.isEmpty else { return false }
        for id in gone { _ = remove(id) }
        return true
    }

    /// Forgets everything — the map was closed, or the passenger signed out.
    mutating func clear() -> Bool {
        guard !visible.isEmpty else { return false }
        order.removeAll()
        visible.removeAll()
        return true
    }

    private mutating func remove(_ vehicleId: String) -> Bool {
        guard visible.removeValue(forKey: vehicleId) != nil else { return false }
        order.removeAll { $0 == vehicleId }
        return true
    }
}

import Foundation
import MageRideShared

/// The four directed payloads a screen subscribes to.
///
/// `VehiclePositions`, `VehicleRemoved` and `ShareRevoked` are deliberately absent: all three are
/// about *what is on the map*, and the map reads ``PassengerLiveMap/vehicles`` rather than replaying
/// a stream to rebuild a set the store already holds. `ShareRevoked` also reaches a screen through
/// the vehicle disappearing, which is the only consequence it has.
enum LiveEvent {

    /// A ride-aggregate transition — SCR-PI-014/015/017/020's own state.
    case rideState(RideStateChanged)

    /// The assigned driver's live position (US-6A.12) — SCR-PI-015's moving marker.
    case driverMoved(DriverPosition)

    /// The proxy round-trip resolving (P-02, P-13) — SCR-PI-010b's pin auto-filling.
    case locationRequest(LocationRequestResolved)

    /// Package handoff progress (US-20.7) — SCR-PI-020/021's four-step bar.
    case packageStatus(PackageStatusChanged)
}

/// The snapshot half of D6' §5.4's recovery, as one method.
///
/// **A seam rather than `QueryApi` itself, and the reason is the bridge.** `QueryApi` is a Kotlin
/// interface with nine `suspend` methods; implementing one in Swift means implementing all nine, and
/// implementing an *exported suspend* function from Swift means matching whatever completion-handler
/// selector Kotlin/Native generated for it — a shape that is the compiler's business rather than
/// this codebase's. One method the app declares is testable with four lines and cannot drift.
///
/// It is also the narrower truth: the live plane needs exactly one read out of query-svc, and a
/// protocol that says so is a protocol nobody can quietly add a second call to.
protocol NearbySnapshots: AnyObject {

    /// `GET /v1/nearby` — what is visible around a point, right now.
    ///
    /// Answers `nil` on any failure. The socket is already up and the next per-cell batch is at most
    /// eight seconds away, so a failed resync costs freshness rather than the map.
    func nearby(around point: GeoPoint, radiusMetres: Int) async -> [NearbyVehicle]?
}

/// ``NearbySnapshots`` over the real client.
///
/// The whole of what it adds is the two things a Swift call site cannot leave out: **every parameter
/// is passed**, because a Kotlin default argument does not survive the Objective-C export, and the
/// throw is swallowed here rather than at the caller.
///
/// `types` and `modes` are `nil` on purpose. The *platform's* visibility rules are the server's
/// (entitlement, freshness, engagement) and SCR-PI-006's filter is the passenger's — folding the
/// second into this call would make a Mode B vehicle they have no grant for indistinguishable from
/// one they simply switched off. C078 makes the same argument on the Android side.
final class ApiNearbySnapshots: NearbySnapshots {

    private let query: QueryApi

    init(query: QueryApi) {
        self.query = query
    }

    func nearby(around point: GeoPoint, radiusMetres: Int) async -> [NearbyVehicle]? {
        try? await query.getNearbyVehicles(
            lat: point.lat,
            lng: point.lng,
            radiusMetres: KotlinInt(int: Int32(radiusMetres)),
            types: nil,
            modes: nil
        ).vehicles
    }
}

/// Everything the server says, turned into what the map shows.
///
/// The socket carries deltas and `GET /v1/nearby` carries snapshots, and both end up in the same
/// ``LiveVehicleStore`` — which is why the resync lives here rather than beside the connection. From
/// a screen's point of view there is one answer to "what is nearby", and it does not matter which
/// transport most recently updated it.
///
/// **Payloads arrive as JSON text and are decoded by `:shared`.** `IosLiveHubPayloadsKt` is the
/// door; it exists because `MageRideJson.decodeFromString<T>()` is `inline` + `reified` and is not
/// exported to Objective-C at all, and because a `Codable` mirror of `VehicleFrame` in this target
/// is what `signalr-hub.md` §3 forbids. See ``SignalRLiveHubTransport``.
///
/// An `actor` because the store it drives is a value with no lock of its own and the events arrive
/// on the client's own callback queue.
actor LiveHubInbox {

    private let snapshots: NearbySnapshots
    private let grid: H3Grid
    private let contract = IosLiveHub()
    private var store = LiveVehicleStore()

    /// Called with the whole visible set whenever it changes — one update per batch, not one per
    /// vehicle. ``PassengerLiveMap`` hops it to the main actor and publishes it.
    private let onVehicles: ([VehicleFrame]) -> Void

    /// Called with each directed payload, in arrival order.
    private let onEvent: (LiveEvent) -> Void

    init(
        snapshots: NearbySnapshots,
        grid: H3Grid,
        onVehicles: @escaping ([VehicleFrame]) -> Void,
        onEvent: @escaping (LiveEvent) -> Void
    ) {
        self.snapshots = snapshots
        self.grid = grid
        self.onVehicles = onVehicles
        self.onEvent = onEvent
    }

    /// One hub event.
    ///
    /// A payload that does not decode is dropped rather than thrown: it must not take down the
    /// callback every other event also arrives on, and on this platform the decoders answer `nil`
    /// rather than throwing for a second reason — an exception out of a non-suspend Kotlin function
    /// crosses as an uncaught Objective-C exception, which Swift cannot catch (the C091 finding).
    func receive(event name: String, payload: String) {
        switch name {
        case contract.eventVehiclePositions:
            guard let frames = IosLiveHubPayloadsKt.decodeVehicleFrames(json: payload) else { return }
            publishIf(store.onPositions(frames))

        case contract.eventVehicleRemoved:
            guard let removed = IosLiveHubPayloadsKt.decodeVehicleRemoved(json: payload) else { return }
            publishIf(store.onRemoved(removed))

        case contract.eventShareRevoked:
            guard let revoked = IosLiveHubPayloadsKt.decodeShareRevoked(json: payload) else { return }
            publishIf(store.onShareRevoked(revoked))

        case contract.eventRideStateChanged:
            guard let value = IosLiveHubPayloadsKt.decodeRideStateChanged(json: payload) else { return }
            onEvent(.rideState(value))

        case contract.eventDriverPosition:
            guard let value = IosLiveHubPayloadsKt.decodeDriverPosition(json: payload) else { return }
            onEvent(.driverMoved(value))

        case contract.eventLocationRequestResolved:
            guard let value = IosLiveHubPayloadsKt.decodeLocationRequestResolved(json: payload) else { return }
            onEvent(.locationRequest(value))

        case contract.eventPackageStatus:
            guard let value = IosLiveHubPayloadsKt.decodePackageStatusChanged(json: payload) else { return }
            onEvent(.packageStatus(value))

        default:
            // An event this build does not know. `signalr-hub.md` §3 has seven; a later one arrives
            // here and is ignored, which is what a client should do with news it has no screen for.
            return
        }
    }

    /// The snapshot half of the recovery — *"the socket carries deltas, the REST read carries the
    /// state"*.
    ///
    /// A failure is swallowed: the socket is already up and the next per-cell batch is at most eight
    /// seconds away, so a failed resync costs freshness rather than the map. The radius is the R-06
    /// view's own ~3 km, which is what the nineteen cells cover.
    func resync(around point: GeoPoint) async {
        guard let vehicles = await snapshots.nearby(
            around: point,
            radiusMetres: LiveHubInbox.snapshotRadiusMetres
        ) else { return }

        publishIf(store.onSnapshot(vehicles))
    }

    /// Drops every vehicle whose own res-7 cell is no longer subscribed. See ``LiveVehicleStore``.
    func retain(cells: Set<H3Cell>) {
        publishIf(store.retain(cells: cells, grid: grid))
    }

    /// Drops one vehicle the passenger just unsubscribed from (AL-25).
    func drop(vehicleId: String) {
        publishIf(store.drop(vehicleId))
    }

    /// Forgets the map. The passenger signed out, or the app is going.
    func clear() {
        _ = store.clear()
        onVehicles(store.snapshot())
    }

    private func publishIf(_ changed: Bool) {
        guard changed else { return }
        onVehicles(store.snapshot())
    }

    /// The `GET /v1/nearby` radius, in metres.
    ///
    /// The R-06 view is res-7 + `ring(2)`, which reaches 2.8–3.3 km; 3 km is inside the contract's
    /// 100–20 000 m bounds and matches what the nineteen cells actually cover. The cells remain the
    /// subscription — this is only what the resync asks for.
    static let snapshotRadiusMetres = 3_000
}

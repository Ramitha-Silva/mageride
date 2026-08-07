import Combine
import Foundation
import MageRideShared

/// What tapping a marker did (AL-23, US-7.4).
///
/// Three modes, three different answers, and the middle one is the fence: **a Mode B marker does not
/// open the popup.** It is a vehicle the passenger may or may not be entitled to ride, and the
/// question a tap asks is *"may I subscribe?"* — which is SCR-PI-024's, with the vehicle already
/// filled in.
enum MarkerTap: Equatable {

    /// SCR-PI-007. Mode A only — a bus or a train. The sheet opens from the model's own state; the
    /// case exists so a caller can tell it apart from the two that navigate or do nothing.
    case showPopup(vehicleId: String)

    /// SCR-PI-024, vehicle id pre-filled (US-4.6).
    case requestModeBAccess(vehicleId: String)

    /// Nothing happens.
    ///
    /// US-7.4: *"standby on-demand vehicles do not show info when tapped"*. An idle Mode C tuk on the
    /// map is a vehicle a passenger books through SCR-PI-009, not one they inspect — and its
    /// driver's name is not theirs to see until the ride is accepted (US-7.12). A tap on a marker
    /// that has since left the map lands here too.
    case ignored
}

/// Why the map has nothing on it (US-7.14).
///
/// *"an in-app message when no vehicles of my selected type are active in my area, instead of seeing
/// an empty map with no context"* — and the context is **which** of these it is, which is why this
/// is four values rather than a boolean.
enum EmptyReason {

    /// Something is on the map.
    case none

    /// The filter is off — the passenger did this and can undo it.
    case filteredOut

    /// Nothing is nearby. The genuine *"no {type} active nearby"*.
    case nothingNearby

    /// The socket is down, so what is drawn is last-known and possibly nothing (US-15.2).
    case offline
}

/// The half of SCR-PI-007 the live plane cannot answer.
///
/// `VehicleFrame` is a **position** — id, coordinate, heading, type, mode — because that is what
/// fans out to nineteen geocell groups every few seconds, and putting a driver's name in it would
/// put a driver's name on every frame of every vehicle. The popup's other three fields come from
/// `GET /v1/nearby`, fetched once, when a marker is actually tapped.
///
/// - `etaSeconds` is seconds **to the querying passenger**, which is why the lookup is centred on
///   their fix rather than on the vehicle.
/// - `driverName` is Mode A's alone here. US-7.12 makes a Mode C driver's name conditional on the
///   ride being accepted, and this screen has no ride.
/// - `registrationNumber` is the plate — the wireframe's `NB-4521`.
struct VehicleDetail: Equatable {
    var etaSeconds: Int?
    var driverName: String?
    var registrationNumber: String?
}

/// SCR-PI-010's state.
struct LiveMapState {

    /// What to draw, already filtered.
    var vehicles: [MapVehicle] = []

    /// SCR-PI-006's answer.
    var filter = MapFilter()

    /// The passenger's own position — MAP-02's circle and §0.3's blue dot.
    var fix: MapFix?

    /// Where the live plane is; drives the reconnect chip and the dimming (SCR-PI-032).
    var status: LiveStatus = .disconnected

    /// US-7.13's ★ Home / ★ Work chips.
    var shortcuts: [SavedAddress] = []

    /// The vehicle SCR-PI-007 is open for, or `nil`.
    var selected: VehicleFrame?

    /// What query-svc knows about ``selected`` beyond its position. `nil` while the lookup is in
    /// flight, and for a vehicle the snapshot did not come back with.
    var detail: VehicleDetail?

    /// The wireframe's *"Recent"* rows, from §2.2's local table.
    var recents: [GeocodedPlace] = []

    /// Everything the plane says is visible, **unfiltered**. Held so that toggling a chip re-draws
    /// from what is already in hand — the wireframe calls the filter *"instant"*, and a re-query
    /// would put a network round trip behind a switch.
    var lastFrames: [VehicleFrame] = []

    /// Whether what is drawn is last-known rather than live — SCR-PI-032's dimmed map.
    var stale: Bool { status != .connected }

    /// US-7.14's message, or ``EmptyReason/none``.
    var emptyReason: EmptyReason {
        if !vehicles.isEmpty { return .none }
        if stale { return .offline }
        if filter.showsNothing { return .filteredOut }
        return .nothingNearby
    }

    /// Where the camera opens, and where it returns to. Colombo Fort before the first fix.
    var camera: MapCamera {
        guard let fix else { return .colombo }
        return MapCamera(lat: fix.lat, lng: fix.lng)
    }
}

/// SCR-PI-010 — the live map, and the app's home.
///
/// **This screen owns the R-06 subscription's input and nothing else about it.** The socket, the
/// nineteen cells, the 30 s hysteresis and the reconnect are ``PassengerLiveMap``'s (C094); what the
/// map does is feed it a position and read back what to draw. That split is why the cell arithmetic
/// is tested with no server and this class is tested with no map.
///
/// **The filter is applied here rather than in the plane.** The plane holds what the *platform* says
/// is visible — entitlement, freshness, engagement — and the filter holds what the *passenger* asked
/// to see. Folding the two would make a Mode B vehicle the passenger has no grant for
/// indistinguishable from one they simply switched off.
@MainActor
final class LiveMapModel: ObservableObject {

    @Published private(set) var state = LiveMapState()

    private let live: PassengerLiveMap
    private let locations: PassengerLocationSource
    private let places: PassengerPlaces
    private let snapshots: NearbySnapshots
    private let recents: RecentPlaces

    private var subscriptions: Set<AnyCancellable> = []
    private var detailLookup: Task<Void, Never>?
    private var ticker: Task<Void, Never>?

    /// - Parameters:
    ///   - live: The process's one live plane. Already connected by the shell.
    ///   - locations: The fix the nineteen cells are computed from.
    ///   - places: US-7.13's saved addresses.
    ///   - snapshots: `GET /v1/nearby`. **The same seam the recovery resync uses** — one read, one
    ///     door; see ``loadDetail(for:)``.
    ///   - recents: §2.2's local table.
    init(
        live: PassengerLiveMap,
        locations: PassengerLocationSource,
        places: PassengerPlaces,
        snapshots: NearbySnapshots,
        recents: RecentPlaces
    ) {
        self.live = live
        self.locations = locations
        self.places = places
        self.snapshots = snapshots
        self.recents = recents
    }

    deinit {
        detailLookup?.cancel()
        ticker?.cancel()
    }

    /// Subscribes to everything the screen draws. Idempotent — SwiftUI may run `.task` again after a
    /// scene change, and two location subscriptions would keep the blue status-bar pill lit twice.
    func start() {
        guard subscriptions.isEmpty else { return }

        // The plane is already connected — the shell does that. What it has no position for until
        // now is the passenger, and without one it joins no cells at all.
        locations.fixes
            .sink { [weak self] fix in
                guard let self else { return }
                self.live.onPosition(fix.point)
                self.state.fix = MapFix(lat: fix.lat, lng: fix.lng, accuracyMetres: fix.accuracyMetres)
            }
            .store(in: &subscriptions)

        live.$vehicles
            .sink { [weak self] frames in self?.absorb(frames: frames) }
            .store(in: &subscriptions)

        live.$status
            .sink { [weak self] status in self?.state.status = status }
            .store(in: &subscriptions)

        startCellTick()
        Task { await loadShortcuts() }
        Task { await reloadRecents() }
    }

    /// Stops the location subscription and the tick. Called when the map goes away.
    ///
    /// The socket is deliberately **not** touched: it is the process's, and SCR-PI-015 is still
    /// watching a ride on it. The fix source is cold, though, and holding it open behind a screen
    /// nobody is looking at is what keeps the blue pill lit in a pocket.
    func stop() {
        subscriptions.removeAll()
        ticker?.cancel()
        ticker = nil
    }

    /// Re-reads §2.2's recents.
    ///
    /// Called whenever the map comes back to the front, because the row is written on **another**
    /// screen (SCR-PI-008) and a local table has no change feed a publisher could subscribe to.
    func reloadRecents() async {
        state.recents = await recents.recent()
    }

    /// SCR-PI-006's mode rows. Instant and client-side — no re-query.
    func setMode(_ mode: ModeToken, enabled: Bool) {
        apply(filter: state.filter.withMode(mode, enabled: enabled))
    }

    /// SCR-PI-006's type chips.
    func setType(_ type: VehicleToken, enabled: Bool) {
        apply(filter: state.filter.withType(type, enabled: enabled))
    }

    /// A marker was tapped — MAP-07, routed by mode (AL-23).
    ///
    /// Answers what should happen rather than doing it: opening the sheet is this class's,
    /// navigating to SCR-PI-024 is the screen's, and a tap on a marker that has since left the map
    /// is neither.
    @discardableResult
    func onMarkerTapped(_ vehicleId: String) -> MarkerTap {
        // Matched against what is **drawn** first, so a marker the filter has hidden cannot be
        // tapped through, and then against the plane for the frame itself.
        guard state.vehicles.contains(where: { $0.vehicleId == vehicleId }),
              let vehicle = state.lastFrames.first(where: { $0.vehicleId == vehicleId })
        else {
            return .ignored
        }

        switch ModeToken.forMode(vehicle.mode) {
        // Bus and train — distance, ETA, driver, plate (US-7.4, US-7.11).
        case .a:
            state.selected = vehicle
            state.detail = nil
            detailLookup?.cancel()
            detailLookup = Task { await loadDetail(for: vehicle) }
            return .showPopup(vehicleId: vehicle.vehicleId)

        // AL-23 / US-4.6. The vehicle id is what SCR-PI-024 pre-fills.
        case .b:
            return .requestModeBAccess(vehicleId: vehicle.vehicleId)

        // US-7.4 — a standby vehicle shows nothing, and neither does one with no mode at all.
        case .c, nil:
            return .ignored
        }
    }

    /// SCR-PI-007 dismissed.
    func dismissPopup() {
        detailLookup?.cancel()
        detailLookup = nil
        state.selected = nil
        state.detail = nil
    }

    // MARK: -

    /// Re-applies the filter to the frames the plane last published.
    private func absorb(frames: [VehicleFrame]) {
        state.lastFrames = frames
        state.vehicles = frames.filter(state.filter.allows).map(\.asMapVehicle)

        // A vehicle that left the map cannot stay open in a popup over it.
        if let open = state.selected, !frames.contains(where: { $0.vehicleId == open.vehicleId }) {
            state.selected = nil
            state.detail = nil
        }
    }

    /// Re-applies a changed filter to the frames already held, with no re-query.
    private func apply(filter: MapFilter) {
        state.filter = filter
        state.vehicles = state.lastFrames.filter(filter.allows).map(\.asMapVehicle)
    }

    /// The popup's ETA, driver and plate — the fields the socket does not carry.
    ///
    /// **Centred on the passenger, not on the vehicle.** `NearbyVehicle.etaSeconds` is defined as
    /// *"seconds to the querying passenger"*, so a lookup centred on the bus would answer roughly
    /// zero and the popup would tell every passenger their bus was already here. Without a fix there
    /// is no *"to the passenger"* at all, and the popup says the ETA is unavailable rather than
    /// inventing a reference point.
    ///
    /// Best effort: this is three lines of detail on a sheet that already shows the vehicle and the
    /// distance, so a failure leaves those standing rather than replacing the sheet with an error.
    ///
    /// **Δ the Android twin passes `modes = [A]` on this call and this one passes none**, because
    /// the seam it goes through is ``NearbySnapshots`` — the same one D6' §5.4's resync uses, so
    /// `GET /v1/nearby` has one caller shape in this app rather than two. It is a query parameter
    /// rather than a rule: the answer is matched by vehicle id, and no Mode B or Mode C marker
    /// reaches this method at all.
    private func loadDetail(for vehicle: VehicleFrame) async {
        guard let fix = state.fix else { return }

        let origin = GeoPoint(lat: fix.lat, lng: fix.lng)
        let metres = GeoDistanceKt.distanceMetres(from: origin, to: vehicle.point)
        // The contract's band is 100–20 000 m, and the radius has to reach past the vehicle or the
        // snapshot comes back without the one thing it was asked for.
        let wanted = Int(metres) + Self.radiusMarginMetres
        let radius = min(max(wanted, Self.minimumRadiusMetres), Self.maximumRadiusMetres)

        let visible = await snapshots.nearby(around: origin, radiusMetres: radius)
        guard !Task.isCancelled else { return }
        guard let found = visible?.first(where: { $0.vehicleId == vehicle.vehicleId }) else { return }
        // The popup may have been dismissed, or another marker tapped, while this was in flight — in
        // which case the answer is about a vehicle nobody is looking at.
        guard state.selected?.vehicleId == vehicle.vehicleId else { return }

        state.detail = VehicleDetail(
            // A nullable Kotlin `Int` crosses as a `KotlinInt?`; `int32Value` is the read half, the
            // same one `VehicleFrame.asMapVehicle` uses for a heading.
            etaSeconds: found.etaSeconds.map { Int($0.int32Value) },
            driverName: found.driverName,
            registrationNumber: found.registrationNumber
        )
    }

    /// US-7.13's shortcuts.
    ///
    /// Best effort: the chips are a convenience and a passenger with no saved addresses simply has
    /// none, which is also what a failure looks like. C101 owns the screen that creates them.
    private func loadShortcuts() async {
        guard let saved = try? await places.savedAddresses() else { return }
        state.shortcuts = saved
    }

    /// The map's own tick, which is the only thing that lands a **held** boundary crossing.
    ///
    /// ADD §7.4 step 6 holds a crossing for thirty seconds, and `CLLocationManager` here has a 250 m
    /// `distanceFilter` — so a passenger who steps over a cell edge and then stands still produces no
    /// further fixes at all and the held crossing would never be re-evaluated. That is precisely what
    /// ``PassengerLiveMap/refreshCells()`` exists for, and its own documentation says *"SCR-PI-010's
    /// own tick calls this"*.
    ///
    /// **The Android twin has no such tick and that is a defect, not a Section C delta** — recorded
    /// in the C096 handoff. Half the hysteresis window, so a held crossing lands inside one of them;
    /// the call is arithmetic against a clock and costs nothing when nothing has moved.
    private func startCellTick() {
        ticker?.cancel()
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(Self.cellTickSeconds) * 1_000_000_000)
                guard !Task.isCancelled else { return }
                await MainActor.run {
                    guard let self else { return }
                    self.live.refreshCells()
                }
            }
        }
    }

    /// Half of `GeoCells.BOUNDARY_HYSTERESIS`, which is thirty seconds. The constant itself is a
    /// `kotlin.time.Duration` and must never be read as a number across this bridge — see
    /// `IosGeoCells.kt`.
    private static let cellTickSeconds = 15

    /// The `GET /v1/nearby` band, and enough margin that the vehicle is inside it.
    private static let radiusMarginMetres = 200
    private static let minimumRadiusMetres = 100
    private static let maximumRadiusMetres = 20_000
}

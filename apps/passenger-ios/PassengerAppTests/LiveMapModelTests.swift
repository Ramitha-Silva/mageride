import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-010 and the two sheets over it, with no map and no server.
///
/// The socket, the nineteen cells and the hysteresis are C094's and are asserted in
/// ``PassengerLiveMapTests``. What this suite owns is the layer above: **what a passenger sees and
/// what a tap does** — the client-side filter, AL-23's routing by mode, US-7.16's engaged vehicle
/// leaving the map, and US-7.14's reason for an empty one.
///
/// The live plane here is the **real** ``PassengerLiveMap`` over ``FakeLiveHubTransport``, not a stub
/// of it: every rule under test is about the boundary between the two, and a stubbed plane would let
/// this file assert a shape production does not have. C078 makes the same call on the Android side.
final class LiveMapModelTests: XCTestCase {

    private var transport: FakeLiveHubTransport!
    private var snapshots: FakeNearbySnapshots!
    private var locations: FakePassengerLocationSource!
    private var places: FakePassengerPlaces!
    private var recents: FakeRecentPlaces!
    private var live: PassengerLiveMap!

    /// Every model this suite built, so ``tearDown()`` can stop its location subscription and its
    /// cell tick. A model left running wakes up inside the next class's test.
    private var models: [LiveMapModel] = []

    @MainActor
    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
        transport = FakeLiveHubTransport()
        snapshots = FakeNearbySnapshots()
        locations = FakePassengerLocationSource()
        places = FakePassengerPlaces()
        places.saved = [HomeFixtures.home, HomeFixtures.work]
        recents = FakeRecentPlaces([HomeFixtures.nugegoda])
        live = PassengerLiveMap(transport: transport, snapshots: snapshots, grid: SharedH3Grid())
    }

    @MainActor
    override func tearDown() {
        models.forEach { $0.stop() }
        models = []
        live = nil
        super.tearDown()
    }

    // MARK: - The subscription's input

    /// The screen owns the R-06 subscription's **input** and nothing else about it. Until a fix
    /// arrives the plane is connected to a socket and subscribed to nothing at all, which draws an
    /// empty map that no amount of waiting fixes.
    @MainActor
    func testThePassengersFixIsWhatJoinsTheNineteenCells() async {
        let model = await connectedModel()

        locations.emit(PassengerFix(lat: HomeFixtures.colombo.lat, lng: HomeFixtures.colombo.lng, accuracyMetres: 12))

        await eventually("nineteen cells") { await MainActor.run { self.live.cells.count } == 19 }
        await eventually("a fix on screen") { await MainActor.run { model.state.fix != nil } }
        XCTAssertEqual(model.state.fix?.accuracyMetres, 12, "MAP-02's circle radius comes from the fix")
    }

    // MARK: - The filter

    @MainActor
    func testABatchOfFramesIsDrawnThroughTheFilter() async {
        let model = await connectedModel()

        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        XCTAssertEqual(
            Set(model.state.vehicles.map(\.vehicleId)),
            [HomeFixtures.busId, HomeFixtures.vanId, HomeFixtures.tukId]
        )
        XCTAssertEqual(model.state.emptyReason, EmptyReason.none)
    }

    /// SCR-PI-006's own state line calls the filter *"instant"*. This asserts the *client-side* half
    /// literally: the frames are already held, so switching a mode off changes the map without a
    /// single further call to any service. A re-query here would put a network round trip — and an
    /// offline failure — behind a switch.
    @MainActor
    func testTogglingAModeRedrawsFromWhatIsAlreadyInHand() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }
        // The shortcut read is a background call of its own, so wait for it: otherwise it lands
        // *between* the two counts on a loaded host and this test fails for a call the toggle did
        // not make.
        await eventually("shortcuts read") { await MainActor.run { !model.state.shortcuts.isEmpty } }
        let readsBefore = (places.savedReads, places.searches.count, snapshots.calls.count)

        model.setMode(.c, enabled: false)

        XCTAssertEqual(Set(model.state.vehicles.map(\.vehicleId)), [HomeFixtures.busId, HomeFixtures.vanId])
        XCTAssertEqual(places.savedReads, readsBefore.0, "a filter toggle is not a query")
        XCTAssertEqual(places.searches.count, readsBefore.1)
        XCTAssertEqual(snapshots.calls.count, readsBefore.2)
        XCTAssertEqual(model.state.lastFrames.count, 3, "the unfiltered set is kept, so it can come back")

        model.setMode(.c, enabled: true)
        XCTAssertEqual(model.state.vehicles.count, 3)
    }

    /// The filter is re-applied to **every** batch rather than to the first one. A screen that
    /// filtered once and then appended would show a type the passenger had switched off the moment
    /// that vehicle next moved.
    @MainActor
    func testAFilterHiddenVehicleStaysHiddenWhenTheNextBatchLands() async {
        let model = await connectedModel()
        model.setType(.threeWheeler, enabled: false)

        await deliverThreeVehicles()
        await eventually("batch absorbed") { await MainActor.run { model.state.lastFrames.count } == 3 }

        XCTAssertEqual(Set(model.state.vehicles.map(\.vehicleId)), [HomeFixtures.busId, HomeFixtures.vanId])
    }

    // MARK: - AL-23

    /// US-7.4 / SCR-PI-007 — a bus is public transport and its details are public.
    @MainActor
    func testTappingAModeAMarkerOpensThePopup() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        let tap = model.onMarkerTapped(HomeFixtures.busId)

        XCTAssertEqual(tap, .showPopup(vehicleId: HomeFixtures.busId))
        XCTAssertEqual(model.state.selected?.vehicleId, HomeFixtures.busId, "the sheet opens from state")

        model.dismissPopup()
        XCTAssertNil(model.state.selected)
    }

    /// AL-23 / US-4.6. The fence: a private vehicle never opens SCR-PI-007. The question a tap asks
    /// is *"may I subscribe to this?"*, and SCR-PI-024 is where it is asked — with the id already
    /// filled in, because a passenger has no other way to name a vehicle they can see but do not own.
    @MainActor
    func testTappingAModeBMarkerAsksForAccessWithTheVehiclePreFilled() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        let tap = model.onMarkerTapped(HomeFixtures.vanId)

        XCTAssertEqual(tap, .requestModeBAccess(vehicleId: HomeFixtures.vanId))
        XCTAssertNil(model.state.selected, "no popup was opened over the map")
    }

    /// US-7.4, verbatim: *"standby on-demand vehicles do not show info when tapped"*. An idle tuk is
    /// booked through SCR-PI-009, not inspected — and its driver's name is not the passenger's to
    /// see until the ride is accepted (US-7.12).
    @MainActor
    func testTappingAStandbyOnDemandMarkerDoesNothing() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        XCTAssertEqual(model.onMarkerTapped(HomeFixtures.tukId), .ignored)
        XCTAssertEqual(model.onMarkerTapped(HomeFixtures.departedId), .ignored, "and neither does one that left")
        XCTAssertNil(model.state.selected)
    }

    /// A marker the passenger has switched off is not on screen, so it cannot be tapped through.
    @MainActor
    func testAFilteredOutMarkerCannotBeTapped() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        model.setMode(.a, enabled: false)

        XCTAssertEqual(model.onMarkerTapped(HomeFixtures.busId), .ignored)
        XCTAssertNil(model.state.selected)
    }

    // MARK: - What leaves the map

    /// US-7.16 / D-22 — a Mode C vehicle that accepts a ride leaves every public geocell group and
    /// lives in `ride:{rideId}` until it is free. Leaving it drawn is how a passenger ends up walking
    /// towards a taxi that already has a fare.
    @MainActor
    func testAnEngagedOnDemandVehicleDisappearsDuringItsHire() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        await transport.deliver(
            event: IosLiveHub().eventVehicleRemoved,
            payload: HomeFixtures.removed(HomeFixtures.tukId, reason: "engaged")
        )
        await eventually("two drawn") { await MainActor.run { model.state.vehicles.count } == 2 }

        XCTAssertEqual(Set(model.state.vehicles.map(\.vehicleId)), [HomeFixtures.busId, HomeFixtures.vanId])
    }

    /// The corner the popup makes possible: SCR-PI-007 is open on a bus and the bus goes stale. A
    /// sheet left up would keep showing a distance to a vehicle the platform has stopped tracking —
    /// which is worse than no sheet, because it looks live.
    @MainActor
    func testAVehicleThatLeavesTheMapClosesThePopupOverIt() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }
        model.onMarkerTapped(HomeFixtures.busId)
        XCTAssertEqual(model.state.selected?.vehicleId, HomeFixtures.busId)

        await transport.deliver(
            event: IosLiveHub().eventVehicleRemoved,
            payload: HomeFixtures.removed(HomeFixtures.busId, reason: "stale")
        )
        await eventually("the sheet closed") { await MainActor.run { model.state.selected == nil } }

        XCTAssertEqual(Set(model.state.vehicles.map(\.vehicleId)), [HomeFixtures.vanId, HomeFixtures.tukId])
        XCTAssertNil(model.state.detail)
    }

    // MARK: - US-7.14 and SCR-PI-032

    /// *"An in-app message when no vehicles of my selected type are active in my area, instead of
    /// seeing an empty map with no context."* The context is this distinction: an outage, a filter
    /// the passenger set, or a genuinely quiet area each ask for a different response, and only the
    /// middle one is theirs to undo.
    @MainActor
    func testAnEmptyMapSaysWhichKindOfEmptyItIs() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }

        model.setMode(.a, enabled: false)
        model.setMode(.b, enabled: false)
        model.setMode(.c, enabled: false)
        XCTAssertEqual(model.state.emptyReason, EmptyReason.filteredOut)

        model.setMode(.a, enabled: true)
        XCTAssertEqual(Set(model.state.vehicles.map(\.vehicleId)), [HomeFixtures.busId])
    }

    /// SCR-PI-032 / US-15.2. What is drawn is last-known and is marked as such; nothing is erased,
    /// because a passenger who has lost signal still wants to know where the bus was. `stale` is what
    /// fades the marker layers — see ``MageRideMap`` `dimmed`.
    ///
    /// Asserted on the state rather than by dropping the socket: the plane's own reconnect is R-09's
    /// and lands inside 1.25 s (``PassengerLiveMapTests`` pins that), so a test that dropped the
    /// connection would be racing a recovery it does not own.
    @MainActor
    func testAMapThatIsNotConnectedIsStaleButIsNotCleared() {
        var drawn = LiveMapState()
        drawn.vehicles = [MapVehicle(vehicleId: HomeFixtures.busId, lat: 6.9344, lng: 79.8428)]
        drawn.status = .connecting

        XCTAssertTrue(drawn.stale, "anything but connected is last-known")
        XCTAssertFalse(drawn.vehicles.isEmpty, "last-known positions stay on the map")
        XCTAssertEqual(drawn.emptyReason, EmptyReason.none, "there is something drawn, so there is no notice")

        // And when there is nothing drawn, the reason is the outage rather than the area — a
        // reconnecting map has no idea whether anything is nearby and must not claim it does.
        var empty = drawn
        empty.vehicles = []
        XCTAssertEqual(empty.emptyReason, EmptyReason.offline)

        var connected = LiveMapState()
        connected.status = .connected
        XCTAssertEqual(connected.emptyReason, EmptyReason.nothingNearby)
    }

    /// The camera opens on Colombo Fort and follows the first fix — a map that opened on `0, 0`
    /// before the satellite answered would show the Gulf of Guinea.
    @MainActor
    func testTheCameraOpensOnColomboUntilThereIsAFix() {
        var state = LiveMapState()
        XCTAssertEqual(state.camera, MapCamera.colombo)

        state.fix = MapFix(lat: 6.85, lng: 79.92)
        XCTAssertEqual(state.camera, MapCamera(lat: 6.85, lng: 79.92))
    }

    // MARK: - The sheet

    /// US-7.13's ★ Home / ★ Work. Best effort — a passenger with none simply has no chips, and that
    /// is also what a failed call looks like, which is why the search bar beside them does not depend
    /// on it.
    @MainActor
    func testTheShortcutChipsAreThePassengersSavedAddresses() async {
        let model = await connectedModel()

        await eventually("chips read") { await MainActor.run { !model.state.shortcuts.isEmpty } }

        XCTAssertEqual(model.state.shortcuts.map(\.label), ["Home", "Work"])
    }

    @MainActor
    func testAFailedShortcutReadLeavesTheSheetStanding() async {
        places.savedFailure = HomeFakeError.unreachable
        let model = await connectedModel()

        await eventually("recents read") { await MainActor.run { !model.state.recents.isEmpty } }

        XCTAssertTrue(model.state.shortcuts.isEmpty, "no chips, and no error either")
    }

    /// **The bug this test exists for.** The ★ chips are written on **another** screen (SCR-PI-026)
    /// and `GET /v1/me/saved-addresses` has no change feed either, so the chips need the same
    /// re-read the recents get. Leaving it to ``LiveMapModel/start()`` would make an address book
    /// depend on whether SwiftUI restarted a `.task` on the way back from the screen that wrote it —
    /// on the Android twin, where the model outlives the trip outright, an address the passenger had
    /// just saved had no chip until the process was restarted. Reported from a handset.
    @MainActor
    func testASavedAddressGetsItsChipOnTheNextAppearance() async {
        let model = await connectedModel()
        await eventually("opening chips") { await MainActor.run { !model.state.shortcuts.isEmpty } }
        XCTAssertEqual(model.state.shortcuts.map(\.label), ["Home", "Work"])

        // Away to SCR-PI-026, an address saved, and back.
        places.saved = [HomeFixtures.home, HomeFixtures.work, HomeFixtures.gym]
        await model.loadShortcuts()

        XCTAssertEqual(model.state.shortcuts.map(\.label), ["Home", "Work", "Gym"])
    }

    /// §2.2's `place_recents` is local-only and has no change feed, and the row is written on
    /// **another** screen (SCR-PI-008). So coming back to the map re-reads it — otherwise a place the
    /// passenger just searched for would be missing from the list of places they searched.
    @MainActor
    func testTheRecentRowsAreTheLocalTableAndAreReReadOnAppear() async {
        let model = await connectedModel()
        await eventually("opening rows") { await MainActor.run { !model.state.recents.isEmpty } }
        XCTAssertEqual(model.state.recents.map(\.displayName), [HomeFixtures.nugegoda.displayName])

        await recents.remember(HomeFixtures.maharagama)
        await model.reloadRecents()

        XCTAssertEqual(model.state.recents.first?.displayName, HomeFixtures.maharagama.displayName, "newest first")
        XCTAssertEqual(model.state.recents.count, 2)
    }

    // MARK: - SCR-PI-007's other three fields

    /// The fields the socket cannot carry. `VehicleFrame` is a position — putting a driver's name in
    /// it would put a driver's name on every frame of every vehicle, across nineteen geocell groups —
    /// so the popup asks `GET /v1/nearby` for them once, when a marker is actually tapped.
    ///
    /// **Centred on the passenger**, because `NearbyVehicle.etaSeconds` is defined as seconds to the
    /// *querying passenger*: a lookup centred on the bus would answer roughly zero and tell every
    /// passenger their bus had already arrived.
    @MainActor
    func testThePopupFillsItsDriverAndPlateFromTheSnapshot() async {
        snapshots.response = [HomeFixtures.busDetail()]
        let model = await connectedModel()
        locations.emit(PassengerFix(lat: HomeFixtures.colombo.lat, lng: HomeFixtures.colombo.lng))
        await deliverThreeVehicles()
        await eventually("drawn with a fix") {
            await MainActor.run { model.state.vehicles.count == 3 && model.state.fix != nil }
        }
        let callsBefore = snapshots.calls.count

        model.onMarkerTapped(HomeFixtures.busId)
        await eventually("detail landed") { await MainActor.run { model.state.detail != nil } }

        XCTAssertEqual(model.state.detail?.driverName, "K. Perera")
        XCTAssertEqual(model.state.detail?.registrationNumber, "NB-4521")
        XCTAssertEqual(snapshots.calls.count, callsBefore + 1)
        XCTAssertEqual(snapshots.calls.last?.point.lat ?? 0, HomeFixtures.colombo.lat, accuracy: 0.0001)
        XCTAssertEqual(snapshots.calls.last?.point.lng ?? 0, HomeFixtures.colombo.lng, accuracy: 0.0001)
    }

    /// *"Seconds to the querying passenger"* has no meaning without a passenger position, so the
    /// lookup is not made at all rather than made against an invented reference point. The sheet
    /// still opens — the vehicle, its type and its mode are all known.
    @MainActor
    func testThePopupOpensWithoutAFixAndSimplyHasNoDetail() async {
        let model = await connectedModel()
        await deliverThreeVehicles()
        await eventually("three drawn") { await MainActor.run { model.state.vehicles.count } == 3 }
        let callsBefore = snapshots.calls.count

        model.onMarkerTapped(HomeFixtures.busId)
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(model.state.selected?.vehicleId, HomeFixtures.busId)
        XCTAssertNil(model.state.detail)
        XCTAssertEqual(snapshots.calls.count, callsBefore, "nothing to centre the ETA on")
    }

    /// A snapshot that came back without the tapped vehicle leaves the sheet standing with the
    /// distance it computed itself — three lines of detail are not worth replacing a sheet with an
    /// error.
    @MainActor
    func testASnapshotThatMissesTheVehicleLeavesTheSheetStanding() async {
        snapshots.response = []
        let model = await connectedModel()
        locations.emit(PassengerFix(lat: HomeFixtures.colombo.lat, lng: HomeFixtures.colombo.lng))
        await deliverThreeVehicles()
        await eventually("drawn with a fix") {
            await MainActor.run { model.state.vehicles.count == 3 && model.state.fix != nil }
        }

        model.onMarkerTapped(HomeFixtures.busId)
        try? await Task.sleep(nanoseconds: 100_000_000)

        XCTAssertEqual(model.state.selected?.vehicleId, HomeFixtures.busId)
        XCTAssertNil(model.state.detail)
    }

    /// The H3 engine answered every call this suite made. A non-zero count means a coordinate or a
    /// resolution was refused and substituted — see ``SharedH3Grid`` `failures`.
    @MainActor
    func testTheGridNeverHadToRefuseACall() {
        XCTAssertEqual(SharedH3Grid.failures, 0)
    }

    // MARK: -

    @MainActor
    private func connectedModel() async -> LiveMapModel {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }

        let model = LiveMapModel(
            live: live,
            locations: locations,
            places: places,
            snapshots: snapshots,
            recents: recents
        )
        model.start()
        // Stopped in `tearDown`, not by a teardown block: `stop()` is main-actor isolated and
        // `addTeardownBlock` takes a nonisolated closure.
        models.append(model)
        return model
    }

    @MainActor
    private func deliverThreeVehicles() async {
        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: HomeFixtures.threeVehicles)
    }
}

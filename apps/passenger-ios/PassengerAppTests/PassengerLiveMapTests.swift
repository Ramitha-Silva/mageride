import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// R-06, the 30 s hysteresis, D6' §5.4's recovery order, and what takes a vehicle off the map.
///
/// Every one of these is a rule the platform depends on and none of them is visible when it breaks:
/// a client that joins the wrong cells, or the right cells at the wrong resolution, sees an empty map
/// with no error anywhere. That is what this file exists for.
///
/// The H3 engine is the **real** one (`SharedH3Grid` over the vendored library), because the cell
/// count is the assertion — a fake grid returning nineteen of anything would prove nothing.
final class PassengerLiveMapTests: XCTestCase {

    private var log: CallLog!
    private var transport: FakeLiveHubTransport!
    private var snapshots: FakeNearbySnapshots!
    private var grid: SharedH3Grid!
    private var live: PassengerLiveMap!

    @MainActor
    override func setUp() {
        super.setUp()
        SharedH3Grid.resetFailures()
        log = CallLog()
        transport = FakeLiveHubTransport(log: log)
        snapshots = FakeNearbySnapshots(log: log)
        grid = SharedH3Grid()
        live = PassengerLiveMap(transport: transport, snapshots: snapshots, grid: grid)
    }

    @MainActor
    override func tearDown() {
        live = nil
        super.tearDown()
    }

    // MARK: - R-06

    /// **The DoD's first line: exactly nineteen cells.** res-7 + `ring(2)` is `1 + 3k(k+1)` = 19,
    /// and the earlier ADD text claiming res-8 + `ring(1)` is the superseded figure — it reaches
    /// about a kilometre, so a passenger would see a third of the vehicles they should.
    @MainActor
    func testTheFirstPositionJoinsExactlyNineteenCells() async {
        live.onPosition(LiveFixtures.colombo)

        await eventually("nineteen cells") { await self.transport.cells(of: self.joinMethod)?.count == 19 }
        await eventually("published") { await MainActor.run { self.live.cells.count } == 19 }
    }

    /// The tokens are the canonical lowercase hex `cell:{h3index}` carries, and they are res 7 —
    /// `fanout-svc` publishes to res-7 groups, so a client at another resolution joins groups that do
    /// not exist.
    @MainActor
    func testEveryJoinedCellIsAWellFormedResolutionSevenToken() async {
        live.onPosition(LiveFixtures.colombo)
        await eventually("joined") { await self.transport.cells(of: self.joinMethod) != nil }

        let tokens = await transport.cells(of: joinMethod) ?? []
        for token in tokens {
            XCTAssertEqual(token, token.lowercased(), "a group name must be canonical hex")
            let cell = H3Cell.companion.parseOrNull(token: token)
            XCTAssertNotNil(cell, "\(token) is not a well-formed cell index")
            XCTAssertEqual(cell?.resolution, GeoCells.shared.VIEW_RESOLUTION)
        }
    }

    /// Staying inside the same cell changes nothing. A passenger walking down a street produces a
    /// fix every few seconds and none of them is a `RemoveFromGroupAsync` on the backplane.
    @MainActor
    func testStayingInsideOneCellSendsNothingFurther() async {
        live.onPosition(LiveFixtures.colombo)
        await eventually("first join") { await self.transport.cells(of: self.joinMethod) != nil }
        await transport.clearSent()

        // A few metres away — well inside a hexagon ~1.2 km across.
        live.onPosition(GeoPoint(lat: LiveFixtures.colombo.lat + 0.0002, lng: LiveFixtures.colombo.lng))
        try? await Task.sleep(nanoseconds: 100_000_000)

        let methods = await transport.methods
        XCTAssertTrue(methods.isEmpty, "membership changed without a boundary crossing: \(methods)")
    }

    /// **ADD §7.4 step 6.** A crossing inside the thirty-second window is *held*, not applied — a
    /// passenger standing on a cell edge whose fixes alternate would otherwise join and leave the
    /// same six groups every few seconds.
    @MainActor
    func testABoundaryCrossingInsideTheWindowIsHeld() async {
        let clock = MutableClock()
        let live = PassengerLiveMap(
            transport: transport,
            snapshots: snapshots,
            grid: grid,
            now: { clock.now }
        )

        live.onPosition(LiveFixtures.colombo)
        await eventually("first join") { await self.transport.cells(of: self.joinMethod) != nil }
        await transport.clearSent()

        // Ten seconds later, three cells away.
        clock.advance(seconds: 10)
        live.onPosition(LiveFixtures.borella)
        try? await Task.sleep(nanoseconds: 100_000_000)

        let held = await transport.methods
        XCTAssertTrue(held.isEmpty, "a crossing inside the 30 s window must be held: \(held)")

        // Past the window, and re-evaluated without a new fix — which is the case that matters,
        // because a passenger who crosses an edge and stops moving produces no more fixes at all.
        clock.advance(seconds: 25)
        live.refreshCells()

        await eventually("the held crossing lands") { await self.transport.cells(of: self.joinMethod) != nil }
        let after = await transport.methods
        XCTAssertTrue(after.contains(leaveMethod), "a crossing leaves the cells it left")
        XCTAssertTrue(after.contains(joinMethod))
        // Leave before join: it keeps the server's per-connection group count at nineteen rather
        // than briefly at twenty-five.
        XCTAssertLessThan(
            after.firstIndex(of: leaveMethod) ?? .max,
            after.firstIndex(of: joinMethod) ?? .min
        )
    }

    /// Rule 3 — crossing *back* cancels the held crossing. This is the case the hysteresis is for.
    @MainActor
    func testCrossingBackCancelsTheHeldCrossing() async {
        let clock = MutableClock()
        let live = PassengerLiveMap(transport: transport, snapshots: snapshots, grid: grid, now: { clock.now })

        live.onPosition(LiveFixtures.colombo)
        await eventually("first join") { await self.transport.cells(of: self.joinMethod) != nil }
        await transport.clearSent()

        clock.advance(seconds: 5)
        live.onPosition(LiveFixtures.borella)     // held
        clock.advance(seconds: 5)
        live.onPosition(LiveFixtures.colombo)     // back — the held crossing is moot
        clock.advance(seconds: 60)
        live.refreshCells()
        try? await Task.sleep(nanoseconds: 150_000_000)

        let methods = await transport.methods
        XCTAssertTrue(methods.isEmpty, "no group churn at all for a passenger on a boundary: \(methods)")
    }

    // MARK: - D6' §5.4

    /// **The order is the contract, not a preference.** A client that snapshots *first* loses every
    /// frame published between the two calls — exactly the ones that moved while it was away.
    @MainActor
    func testRecoveryRejoinsTheGroupsBeforeItSnapshots() async {
        live.onPosition(LiveFixtures.colombo)
        await eventually("first join") { await self.transport.cells(of: self.joinMethod) != nil }

        snapshots.response = []
        log.clear()

        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await eventually("resynced") { self.snapshots.calls.count == 1 }

        let sequence = log.all
        let firstJoin = sequence.firstIndex(of: joinMethod)
        let snapshot = sequence.firstIndex(of: CallLog.nearbySnapshot)
        XCTAssertNotNil(firstJoin, "the recovery rejoins the geocells")
        XCTAssertNotNil(snapshot, "the recovery snapshots")
        XCTAssertLessThan(firstJoin ?? .max, snapshot ?? .min, "JoinGeocells must precede GET /v1/nearby")
        XCTAssertEqual(snapshots.calls.first?.radiusMetres, LiveHubInbox.snapshotRadiusMetres)
        XCTAssertEqual(snapshots.calls.first?.point.lat, LiveFixtures.colombo.lat, accuracy: 0.0001)
    }

    /// The `GET /v1/nearby` radius is the R-06 view's own ~3 km, inside the contract's 100–20 000 m
    /// bounds, and it is what nineteen res-7 cells actually cover.
    func testTheSnapshotRadiusIsTheViewsOwn() {
        XCTAssertEqual(LiveHubInbox.snapshotRadiusMetres, 3_000)
    }

    /// A reconnect re-joins **everything**, ignoring the hysteresis window: after a drop the server
    /// holds no membership at all, and rate-limiting recovery would leave the map blank for up to
    /// thirty seconds.
    @MainActor
    func testAReconnectRejoinsEveryGroupRegardlessOfTheWindow() async {
        live.onPosition(LiveFixtures.colombo)
        live.watchRide("R1")
        live.watchLocationRequest("LR1")
        await eventually("subscribed") { await self.transport.methods.count >= 3 }

        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await transport.clearSent()

        await transport.drop()
        await eventually("re-dialled") { await self.transport.connects >= 2 }
        await eventually("rejoined") { await self.transport.methods.contains(self.joinMethod) }

        let methods = await transport.methods
        XCTAssertEqual(await transport.cells(of: joinMethod)?.count, 19)
        XCTAssertTrue(methods.contains(subscribeRideMethod), "the ride group is rejoined")
        XCTAssertTrue(methods.contains(subscribeLocRequestMethod), "the location request is rejoined")
    }

    /// A ride that has been stopped is not re-joined. The hub has no unsubscribe for a ride group,
    /// so this is the client half — without it a finished ride is re-subscribed for the life of the
    /// process.
    @MainActor
    func testAStoppedRideIsNotRejoined() async {
        live.onPosition(LiveFixtures.colombo)
        live.watchRide("R1")
        await eventually("subscribed") { await self.transport.methods.contains(self.subscribeRideMethod) }
        live.stopWatchingRide("R1")
        try? await Task.sleep(nanoseconds: 50_000_000)

        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await transport.clearSent()
        await transport.drop()
        await eventually("rejoined") { await self.transport.methods.contains(self.joinMethod) }

        let methods = await transport.methods
        XCTAssertFalse(methods.contains(subscribeRideMethod), "a stopped ride must not be rejoined")
    }

    /// **There is no `SubscribeVehicle`.** `signalr-hub.md` §2.1 says `vehicle:{vehicleId}` groups
    /// are *"joined by the server, never asked for"* — a Mode B vehicle is visible because fanout-svc
    /// checked the `share:{userId}` entitlement at join (D-23). This pins the four methods that do
    /// exist, so inventing a fifth is a failing test rather than a review comment.
    @MainActor
    func testTheClientOnlyEverInvokesTheFourContractMethods() async {
        live.onPosition(LiveFixtures.colombo)
        live.watchRide("R1")
        live.watchLocationRequest("LR1")
        live.dropVehicle("V1")
        await eventually("settled") { await self.transport.methods.count >= 3 }
        try? await Task.sleep(nanoseconds: 100_000_000)

        let contract = IosLiveHub()
        let allowed = Set([
            contract.methodJoinGeocells,
            contract.methodLeaveGeocells,
            contract.methodSubscribeRide,
            contract.methodSubscribeLocRequest,
        ])
        let used = Set(await transport.methods)
        XCTAssertTrue(used.isSubset(of: allowed), "invoked something outside the contract: \(used.subtracting(allowed))")
    }

    /// All seven server → client events are subscribed to, from `LiveHub` rather than from a list in
    /// this app — a typo in an event name is a handler that is never invoked, not a compile error.
    @MainActor
    func testEverySevenHubEventsAreSubscribed() async {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }

        let subscribed = await transport.subscribedEvents
        XCTAssertEqual(Set(subscribed), Set(IosLiveHub().eventNames))
        XCTAssertEqual(subscribed.count, 7)
    }

    // MARK: - What is on the map

    /// A batch carries only what moved, so a vehicle stays until something says otherwise.
    @MainActor
    func testABatchAddsVehiclesAndAbsenceRemovesNothing() async {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }

        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1", "V2"]))
        await eventually("two on the map") { await MainActor.run { self.live.vehicles.count } == 2 }

        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1"]))
        try? await Task.sleep(nanoseconds: 100_000_000)

        let count = await MainActor.run { self.live.vehicles.count }
        XCTAssertEqual(count, 2, "absence from a batch must never remove a vehicle")
    }

    /// `three_wheeler` is the wire spelling, and it is the value a Gson binding got wrong on the
    /// Android side. Decoding through `:shared`'s own `Json` is what makes it work; this asserts it
    /// arrived as the enum rather than as a fallback.
    @MainActor
    func testAWireSpelledVehicleTypeDecodes() async {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }

        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1"]))
        await eventually("decoded") { await MainActor.run { self.live.vehicles.count } == 1 }

        let type = await MainActor.run { self.live.vehicles.first?.type }
        XCTAssertEqual(type?.wire, "three_wheeler")
    }

    /// `VehicleRemoved` and `ShareRevoked` both drop the marker. All the reasons mean *drop it now*.
    @MainActor
    func testARemovalAndARevocationBothDropTheMarker() async {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1", "V2"]))
        await eventually("two on the map") { await MainActor.run { self.live.vehicles.count } == 2 }

        await transport.deliver(event: IosLiveHub().eventVehicleRemoved, payload: LiveFixtures.vehicleRemoved("V1"))
        await eventually("one removed") { await MainActor.run { self.live.vehicles.count } == 1 }

        await transport.deliver(event: IosLiveHub().eventShareRevoked, payload: LiveFixtures.shareRevoked("V2"))
        await eventually("both gone") { await MainActor.run { self.live.vehicles.isEmpty } }
    }

    /// AL-25 / US-4.11 — *"unsubscribing removes the vehicle from the live map within seconds"*, and
    /// it is true on the tap rather than on the round trip. **Not a hub call**: the hub has no method
    /// that leaves a vehicle group.
    @MainActor
    func testUnsubscribingDropsTheMarkerWithoutASend() async {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1"]))
        await eventually("on the map") { await MainActor.run { self.live.vehicles.count } == 1 }
        await transport.clearSent()

        live.dropVehicle("V1")

        await eventually("dropped") { await MainActor.run { self.live.vehicles.isEmpty } }
        let methods = await transport.methods
        XCTAssertTrue(methods.isEmpty, "dropping a vehicle is a local erasure, not a hub call")
    }

    /// A malformed payload must not take down the callback every other event arrives on. The
    /// decoders answer `nil` rather than throwing, which on this platform also matters because an
    /// exception out of Kotlin would be uncatchable.
    @MainActor
    func testAMalformedPayloadIsDroppedAndTheSocketKeepsWorking() async {
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }

        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: "{ not json")
        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1"]))

        await eventually("still working") { await MainActor.run { self.live.vehicles.count } == 1 }
    }

    /// Signing out drops the socket and forgets the map — the socket's credential was that session's
    /// access token (D-29), and the next passenger's first frame must not be drawn over the previous
    /// one's vehicles.
    @MainActor
    func testDisconnectClosesTheSocketAndForgetsTheMap() async {
        live.onPosition(LiveFixtures.colombo)
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        await transport.deliver(event: IosLiveHub().eventVehiclePositions, payload: LiveFixtures.vehiclePositions(["V1"]))
        await eventually("on the map") { await MainActor.run { self.live.vehicles.count } == 1 }

        await live.disconnect()

        XCTAssertEqual(live.status, .disconnected)
        XCTAssertTrue(live.cells.isEmpty)
        await eventually("map forgotten") { await MainActor.run { self.live.vehicles.isEmpty } }
        let closes = await transport.closes
        XCTAssertGreaterThanOrEqual(closes, 1)
    }

    /// `connect()` is idempotent — the shell calls it from `.task`, which SwiftUI may run again
    /// after a scene change, and two supervisors would be two sockets.
    @MainActor
    func testConnectIsIdempotent() async {
        live.connect()
        live.connect()
        await eventually("connected") { await MainActor.run { self.live.status } == .connected }
        try? await Task.sleep(nanoseconds: 150_000_000)

        let connects = await transport.connects
        XCTAssertEqual(connects, 1)
    }

    /// The H3 engine answered every call this suite made. A non-zero count means a coordinate or a
    /// resolution was refused and substituted — see ``SharedH3Grid/failures``.
    func testTheGridNeverHadToRefuseACall() {
        XCTAssertEqual(SharedH3Grid.failures, 0)
    }

    // MARK: -

    private var joinMethod: String { IosLiveHub().methodJoinGeocells }
    private var leaveMethod: String { IosLiveHub().methodLeaveGeocells }
    private var subscribeRideMethod: String { IosLiveHub().methodSubscribeRide }
    private var subscribeLocRequestMethod: String { IosLiveHub().methodSubscribeLocRequest }
}

/// A clock a test winds by hand.
///
/// The hysteresis is a comparison against wall time, and a test that could only wait for real time
/// would have to sleep for thirty seconds per assertion. `Timestamp` is `kotlin.time.Instant` and is
/// built through the one door Swift may build one from — see `IosInstantKt`.
final class MutableClock: @unchecked Sendable {

    private var millis: Int64

    /// A fixed instant with no significance beyond being in the past: what matters is the deltas.
    init(epochMillis: Int64 = 1_780_000_000_000) {
        self.millis = epochMillis
    }

    var now: Timestamp { IosInstantKt.timestampFromEpochMillis(millis: millis) }

    func advance(seconds: Int64) {
        millis += seconds * 1_000
    }
}

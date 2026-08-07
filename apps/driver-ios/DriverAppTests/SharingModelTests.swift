import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-028 · sharing management (Mode B), per vehicle** — AL-35's scope rule, and the two
/// services behind it.
@MainActor
final class SharingModelTests: XCTestCase {

    private var identity = FakeDriverIdentity()
    private var sharing = FakeSharingRepository()

    private let modeB = summary(vehicleId: testVehicleId, mode: ServiceMode.b, fleetName: "Ceylon Fleet")
    private let modeA = summary(vehicleId: testOtherVehicleId, registrationNumber: "VN-3321", mode: ServiceMode.a)
    private let modeC = summary(vehicleId: "01JVEHICLE0000000000000C", registrationNumber: "TUK-1", mode: ServiceMode.c)

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        sharing = FakeSharingRepository()
    }

    private func makeModel() -> SharingModel {
        SharingModel(identity: identity, sharing: sharing)
    }

    // MARK: - Which vehicles are offered

    /// `POST /v1/vehicles/{id}/share` is documented for a **Mode A/B** vehicle and the request queue is
    /// literally `/v1/mode-b/…`; a Mode C standby tuk has no subscribers and nothing to share.
    func testOnlyModeAAndModeBVehiclesAreOffered() async {
        identity.live = LiveVehicle(vehicles: [modeC, modeB, modeA], live: modeC)
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.vehicles.map(\.vehicleId), [modeB.vehicleId, modeA.vehicleId])
        XCTAssertEqual(
            model.state.selectedVehicleId,
            modeB.vehicleId,
            "the live vehicle is Mode C and cannot be shared, so the first shareable one is taken"
        )
        XCTAssertFalse(model.state.hasNoShareableVehicle)
    }

    func testADriverWithOnlyModeCVehiclesSeesTheEmptyStateRatherThanADeadSelector() async {
        identity.live = LiveVehicle(vehicles: [modeC], live: modeC)
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(model.state.hasNoShareableVehicle)
        XCTAssertTrue(sharing.requestReads.isEmpty, "nothing is read for a vehicle that cannot be shared")
    }

    /// A driver assigned a fleet van is looking at that van's requests, not at the other vehicle they
    /// happen to own.
    func testTheLiveVehicleIsPreferredWhenItIsShareable() async {
        identity.live = LiveVehicle(vehicles: [modeB, modeA], live: modeA)
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.selectedVehicleId, modeA.vehicleId)
    }

    // MARK: - AL-35 · the selector is a scope, not a filter

    /// Both endpoints take the vehicle in the path, so a change **empties** the lists and re-reads.
    /// A queue seen under the wrong chip is the one thing the rule forbids.
    func testChangingTheVehicleEmptiesBothListsAndRereadsThatVehiclesOwn() async {
        identity.live = LiveVehicle(vehicles: [modeB, modeA], live: modeB)
        sharing.requestsByVehicle = [modeB.vehicleId: [accessRequest()], modeA.vehicleId: []]
        sharing.granteesByVehicle = [modeB.vehicleId: [subscriber()], modeA.vehicleId: []]
        let model = makeModel()
        await model.refresh()
        XCTAssertEqual(model.state.requests.count, 1)
        XCTAssertEqual(model.state.grantees.count, 1)

        await model.select(vehicleId: modeA.vehicleId)

        XCTAssertTrue(model.state.requests.isEmpty, "the previous vehicle's queue is gone")
        XCTAssertTrue(model.state.grantees.isEmpty)
        XCTAssertEqual(sharing.requestReads, [modeB.vehicleId, modeA.vehicleId])
        XCTAssertEqual(sharing.granteeReads, [modeB.vehicleId, modeA.vehicleId])
    }

    /// Only pending requests and only active grants are drawn: a rejected row that stayed in the queue
    /// would offer a second decision on a decision already made.
    func testOnlyPendingRequestsAndActiveGrantsAreDrawn() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        sharing.requestsByVehicle = [
            modeB.vehicleId: [
                accessRequest(),
                accessRequest(requestId: "01JREQUEST00000000000002", status: AccessRequestStatus.rejected),
            ],
        ]
        sharing.granteesByVehicle = [
            modeB.vehicleId: [
                subscriber(),
                subscriber(userId: "01JPASSENGER000000000002", status: GrantStatus.unsubscribed),
            ],
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.requests.map(\.requestId), [testRequestId])
        XCTAssertEqual(model.state.grantees.map(\.userId), [testPassengerId])
    }

    // MARK: - Granting

    /// **US-4.3b: a new grant does not join the grantee list.** Visibility begins when the passenger
    /// accepts, so the screen acknowledges the offer and leaves the roster alone.
    func testAGrantClearsTheFormAndDoesNotClaimASubscriber() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        let model = makeModel()
        await model.refresh()
        model.onUserIdChange(testPassengerId)
        model.onExpiryChange(timestamp(Date(timeIntervalSince1970: 1_781_999_999)))
        XCTAssertTrue(model.state.canGrant)

        await model.grant()

        XCTAssertEqual(sharing.grants.map(\.userId), [testPassengerId])
        XCTAssertEqual(sharing.grants.map(\.vehicleId), [modeB.vehicleId])
        XCTAssertNotNil(sharing.grants.first?.expiresAt, "the chosen expiry is sent (US-4.2)")
        XCTAssertEqual(model.state.grantedTo, testPassengerId)
        XCTAssertEqual(model.state.userId, "", "the form clears; a second grant is a second invitation")
        XCTAssertNil(model.state.expiresAt)
        XCTAssertTrue(model.state.grantees.isEmpty, "US-4.3b — nobody can see the vehicle yet")
    }

    /// A mistyped id is answered at the keyboard rather than by a `404` after an attested POST.
    func testAMalformedUserIdIsRefusedBeforeItIsSent() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        let model = makeModel()
        await model.refresh()

        model.onUserIdChange("PAX-90431")

        XCTAssertTrue(model.state.isUserIdRejected)
        XCTAssertFalse(model.state.canGrant)
        await model.grant()
        XCTAssertTrue(sharing.grants.isEmpty)
    }

    /// `expiresAt` omitted is an open-ended grant, which is what the contract says an absent one means.
    func testAnOpenEndedGrantSendsNoExpiry() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        let model = makeModel()
        await model.refresh()
        model.onUserIdChange(testPassengerId)

        await model.grant()

        XCTAssertNil(sharing.grants.first?.expiresAt)
    }

    // MARK: - The decision

    /// Accepting moves a row out of the queue **and** into the roster — one transaction across two
    /// services — so both lists are re-read rather than moved locally.
    func testAcceptingReReadsBothListsRatherThanMovingARowLocally() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        sharing.requestsByVehicle = [modeB.vehicleId: [accessRequest()]]
        let model = makeModel()
        await model.refresh()

        await model.decide(requestId: testRequestId, isAccepted: true)

        XCTAssertEqual(sharing.accepts, [testRequestId])
        XCTAssertTrue(sharing.rejects.isEmpty)
        XCTAssertEqual(sharing.requestReads, [modeB.vehicleId, modeB.vehicleId])
        XCTAssertEqual(sharing.granteeReads, [modeB.vehicleId, modeB.vehicleId])
        XCTAssertNil(model.state.busyRequestId)
    }

    func testRejectingSendsTheOtherRouteAndNothingElse() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        sharing.requestsByVehicle = [modeB.vehicleId: [accessRequest()]]
        let model = makeModel()
        await model.refresh()

        await model.decide(requestId: testRequestId, isAccepted: false)

        XCTAssertEqual(sharing.rejects, [testRequestId])
        XCTAssertTrue(sharing.accepts.isEmpty)
    }

    /// A failed decision leaves the row where it was, so the driver can look at it again.
    func testAFailedDecisionKeepsTheRowAndResolvesItsOwnCopy() async {
        identity.live = LiveVehicle(vehicles: [modeB], live: modeB)
        sharing.requestsByVehicle = [modeB.vehicleId: [accessRequest()]]
        sharing.nextDecisionFailure = apiFailure(code: "conflict")
        let model = makeModel()
        await model.refresh()

        await model.decide(requestId: testRequestId, isAccepted: true)

        XCTAssertEqual(model.state.requests.map(\.requestId), [testRequestId])
        XCTAssertEqual(model.state.errorKey, "error_already_done")
        XCTAssertNil(model.state.busyRequestId)
    }
}

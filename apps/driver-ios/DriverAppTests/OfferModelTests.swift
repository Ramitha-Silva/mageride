import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-014's fifteen seconds** (US-6A.2/6A.3, R-02, E-01).
///
/// The DoD asks for one thing above all — *"the 15 s offer countdown and expiry behave identically to
/// Android"* — and the two halves of that are here: the ring is derived from the **deadline** rather
/// than from when this device saw the push, and reaching zero frees the slot locally rather than
/// sending a decline the server would answer `410` to.
@MainActor
final class OfferModelTests: XCTestCase {

    private var slot: FakeOfferSlot!
    private var rides: FakeActiveRideRepository!

    override func setUp() {
        super.setUp()
        slot = FakeOfferSlot()
        rides = FakeActiveRideRepository()
    }

    private func makeModel(tick: TimeInterval = 0.02) -> OfferModel {
        OfferModel(slot: slot, rides: rides, tick: tick)
    }

    /// The TTL the ring is scaled against is the offer window D5' §3.5 fixes, and `RideOffer.TTL` is
    /// the same fifteen seconds on the Kotlin side.
    func testTheRingIsScaledAgainstTheFifteenSecondWindow() {
        XCTAssertEqual(OfferUiState.ttl, 15)
        XCTAssertEqual(OfferUiState.urgent, 5, "D2': the last five seconds pulse")
    }

    /// **A push that took two seconds to arrive shows thirteen seconds of ring, not fifteen.**
    func testAnOfferAlreadyPartWayThroughOpensWithAShortRing() async {
        let model = makeModel()
        model.start()

        slot.emit(.live(rideOffer(secondsLeft: 9)))
        await settle()

        XCTAssertTrue(model.state.isLive)
        XCTAssertLessThanOrEqual(model.state.remaining, 9)
        XCTAssertGreaterThan(model.state.remaining, 8)
        XCTAssertLessThan(model.state.progress, 0.65)
        XCTAssertFalse(model.state.isUrgent)
    }

    func testTheLastFiveSecondsAreUrgent() async {
        let model = makeModel()
        model.start()

        slot.emit(.live(rideOffer(secondsLeft: 4)))
        await settle()

        XCTAssertTrue(model.state.isUrgent)
    }

    /// Reaching zero frees the slot **locally**: the server has already released the driver, so a
    /// decline now would be a `410` for nothing.
    func testReachingZeroExpiresTheSlotWithoutTellingTheServer() async {
        let model = makeModel(tick: 0.01)
        model.start()

        slot.emit(.live(rideOffer(secondsLeft: 0.05)))
        await settle(for: 0.4)

        XCTAssertEqual(slot.expireCount, 1)
        XCTAssertEqual(slot.declineCount, 0, "an expired offer is never declined")
        XCTAssertTrue(model.state.outcome is OfferOutcomeExpired)
        XCTAssertEqual(model.state.remaining, 0)
        XCTAssertEqual(model.state.secondsLeft, 0)
    }

    /// The enrichment read is one `GET /v1/rides/{rideId}` inside the window, and its version goes
    /// straight to the slot so the accept does not spend a second round trip on `GET …/state` (R-14).
    func testTheEnrichmentReadFillsTheBadgesAndHandsOverTheVersion() async {
        rides.detailToReturn = rideDetail(version: 7, kind: RideKind.proxy, packageSize: PackageSize.m)
        let model = makeModel()
        model.start()

        slot.emit(.live(rideOffer()))
        await settle()

        XCTAssertEqual(model.state.detail?.version, 7)
        XCTAssertEqual(slot.versions, [7])
        XCTAssertEqual(model.state.fareMinor, 48_000)
    }

    /// A driver has fifteen seconds; an offer they can still accept with no badges is worth more than
    /// an error over a countdown.
    func testAFailedEnrichmentReadIsSilentAndLeavesTheOfferAcceptable() async {
        rides.nextFailure = apiFailure(code: "internal-error", status: 500)
        let model = makeModel()
        model.start()

        slot.emit(.live(rideOffer()))
        await settle()

        XCTAssertNil(model.state.detail)
        XCTAssertTrue(model.state.isLive)
        XCTAssertNil(model.state.outcome)
        XCTAssertEqual(model.state.fareMinor, 48_000, "the push's rendered fare stands in")
    }

    /// The same offer arriving twice must not re-read the ride or restart the ring.
    func testARepeatedOfferDoesNotReEnrichOrRestartTheCountdown() async {
        let model = makeModel()
        model.start()
        let offer = rideOffer(secondsLeft: 12)

        slot.emit(.live(offer))
        await settle()
        let detail = model.state.detail

        slot.emit(.live(offer))
        await settle()

        XCTAssertEqual(slot.versions.count, 1, "one version handover, not two")
        XCTAssertEqual(detail?.rideId, model.state.detail?.rideId)
    }

    func testAcceptingReportsTheSlotsOwnOutcome() async {
        slot.acceptOutcome = OfferOutcomeWon(ride: rideDetail())
        let model = makeModel()
        model.start()
        slot.emit(.live(rideOffer()))

        await model.accept()

        XCTAssertEqual(slot.acceptCount, 1)
        XCTAssertEqual((model.state.outcome as? OfferOutcomeWon)?.ride.rideId, testRideId)
        XCTAssertFalse(model.state.isDeciding)
    }

    func testRejectingReportsTheSlotsOwnOutcome() async {
        let model = makeModel()
        model.start()
        slot.emit(.live(rideOffer()))

        await model.reject()

        XCTAssertEqual(slot.declineCount, 1)
        XCTAssertTrue(model.state.outcome is OfferOutcomeDeclined)
    }

    /// The slot going Idle after a decline, an expiry or a loss is what takes the takeover down.
    func testTheSlotGoingIdleTakesTheTakeoverDown() async {
        let model = makeModel()
        model.start()
        slot.emit(.live(rideOffer()))
        await settle()
        XCTAssertTrue(model.state.isLive)

        slot.emit(.idle)

        XCTAssertFalse(model.state.isLive)
        XCTAssertNil(model.state.detail)
    }

    /// **`Taken` and `Expired` are never collapsed** — one says somebody was faster and the other says
    /// nobody was, and only the first needs explaining before the driver goes back to standby.
    func testOnlyTheEndingsThatNeedExplainingStayOnScreen() {
        XCTAssertEqual(OfferTakeover.outcomeMessageKey(OfferOutcomeTaken.shared), "offer_taken")
        XCTAssertEqual(OfferTakeover.outcomeMessageKey(OfferOutcomeWalletBlocked.shared), "offer_wallet_blocked")
        XCTAssertNil(OfferTakeover.outcomeMessageKey(OfferOutcomeExpired.shared), "auto-dismisses")
        XCTAssertNil(OfferTakeover.outcomeMessageKey(OfferOutcomeDeclined.shared), "auto-dismisses")
        XCTAssertNil(OfferTakeover.outcomeMessageKey(nil))
    }

    func testEveryOutcomeHasItsOwnIdentitySoTheScreenActsOnEachOnce() {
        let ids = [
            OfferTakeover.outcomeId(OfferOutcomeWon(ride: rideDetail())),
            OfferTakeover.outcomeId(OfferOutcomeTaken.shared),
            OfferTakeover.outcomeId(OfferOutcomeExpired.shared),
            OfferTakeover.outcomeId(OfferOutcomeDeclined.shared),
            OfferTakeover.outcomeId(OfferOutcomeWalletBlocked.shared),
            OfferTakeover.outcomeId(nil),
        ]
        XCTAssertEqual(Set(ids).count, ids.count)
    }

    /// Lets the model's detached tasks run. Awaiting a `Task.sleep` on the main actor yields to them,
    /// which is what a countdown and an enrichment read need to make progress.
    private func settle(for seconds: TimeInterval = 0.1) async {
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
    }
}

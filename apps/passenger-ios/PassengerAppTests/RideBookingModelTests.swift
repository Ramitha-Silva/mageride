import Foundation
import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-009's two lists, and the booking.
///
/// Every assertion here is one of the cluster's fences: AL-19's price-only tier, AL-18's tracked
/// (never booked) bus, AL-55's muted degradation, and R-18's `clientRequestId`.
final class RideBookingModelTests: XCTestCase {

    private var bookings: FakeBookingRepository!
    private var keys: FakePinnedIdempotencyKeys!
    private var preferences: FakeAppPreferences!
    private var draft: BookingDraft!

    @MainActor
    override func setUp() {
        super.setUp()
        bookings = FakeBookingRepository()
        keys = FakePinnedIdempotencyKeys()
        preferences = FakeAppPreferences()
        draft = BookingDraft(preferences: preferences, lastFix: LastKnownFix())
        draft.begin(dropoff: BookingFixtures.nugegoda, pickup: BookingFixtures.colombo)
    }

    // MARK: - AL-19

    /// **A Mode C tier carries a price and nothing else.** D5' §BR-23.3 suppresses *"minutes away"*
    /// and *"distance to driver"* before a driver is matched, and the type is the enforcement: a
    /// card cannot render a field ``TierQuote`` does not have.
    ///
    /// Asserted on the *shape* rather than on a screen, so adding one fails here rather than
    /// appearing in front of a passenger.
    func testAModeCTierCarriesAPriceAndNothingElse() {
        let quote = TierQuote(vehicleType: RideVehicleType.sedan, amountMinor: 85_000, token: "T")

        let mirror = Mirror(reflecting: quote)
        XCTAssertEqual(Set(mirror.children.compactMap(\.label)), ["vehicleType", "amountMinor", "token"])
    }

    @MainActor
    func testTheTiersAreQuotedCheapestFirstWhateverOrderTheyAnswerIn() async {
        let model = await started()

        await eventually("tiers") { await MainActor.run { model.state.tiers.count } == 6 }

        XCTAssertEqual(model.state.tiers.map(\.vehicleType.wire), RideBookingModel.passengerTiers.map(\.wire))
    }

    /// A tier whose estimate failed is **left out** rather than shown priceless. A card with no
    /// price is a card a passenger will tap.
    @MainActor
    func testAFailedEstimateLeavesTheWholeScreenStanding() async {
        bookings.estimateFailure = BookingFakeError.unreachable
        let model = await started()

        await eventually("routes still load") { await MainActor.run { !model.state.tiersLoading } }

        XCTAssertTrue(model.state.tiers.isEmpty)
        XCTAssertNil(model.state.errorKey, "a missing price is not an error banner")
    }

    // MARK: - AL-18 / AL-55

    /// Selecting a public route drops the tier, empties the payment decision and changes the CTA.
    /// There is no fare on a bus.
    @MainActor
    func testAPublicRouteIsTrackedAndNeverBooked() async {
        bookings.options = TransitOptionsResponse(
            options: [BookingFixtures.option(legs: [BookingFixtures.leg()])],
            feedVersion: "v1",
            coverage: TransitCoverage.active
        )
        let model = await started()
        await eventually("routes") { await MainActor.run { !model.state.routes.isEmpty } }

        model.selectRoute(model.state.routes[0])

        XCTAssertTrue(model.state.isPublicSelected)
        XCTAssertFalse(model.state.canBook, "Book Now cannot fire on a bus")
        XCTAssertNil(draft.state.vehicleType, "the tier is dropped from the draft")

        model.book()
        XCTAssertTrue(bookings.requested.isEmpty, "and nothing was posted")
    }

    /// **AL-55.** transit-svc unreachable and transit-svc with no feed are the *same* muted row: a
    /// passenger does not care which, and the private tiers work either way.
    @MainActor
    func testTransitBeingDownIsAMutedRowRatherThanAnError() async {
        bookings.transitFailure = BookingFakeError.unreachable
        let model = await started()

        await eventually("tiers regardless") { await MainActor.run { model.state.tiers.count } == 6 }

        XCTAssertTrue(model.state.publicUnavailable)
        XCTAssertNil(model.state.errorKey, "nothing blocks on GTFS coverage")
        XCTAssertFalse(model.state.tiers.isEmpty, "and the private half quotes normally")
    }

    @MainActor
    func testNoFeedReadsTheSameWayAsAnOutage() async {
        bookings.options = TransitOptionsResponse(options: [], feedVersion: nil, coverage: TransitCoverage.noFeed)
        let model = await started()

        await eventually("settled") { await MainActor.run { !model.state.routesLoading } }

        XCTAssertTrue(model.state.publicUnavailable)
    }

    /// The walk hint is drawn only when the passenger is genuinely off-route. BR-23.2's 400 m halt
    /// radius is the same figure the server uses for *"direct"*, so the two agree about the same
    /// passenger.
    @MainActor
    func testTheWalkHintIsDrawnOnlyWhenTheHaltIsFarEnoughAway() async {
        bookings.options = TransitOptionsResponse(
            options: [BookingFixtures.option(legs: [BookingFixtures.leg()])],
            feedVersion: "v1",
            coverage: TransitCoverage.active
        )
        let model = await started()
        await eventually("routes") { await MainActor.run { !model.state.routes.isEmpty } }

        model.selectRoute(model.state.routes[0])
        await eventually("route drawn") { await MainActor.run { model.state.walkHalt != nil } }
        XCTAssertEqual(model.state.walkHalt?.haltName, "Pamankada")
        XCTAssertEqual(model.state.walkPolyline.count, 2)

        // Standing at the halt: no line, no hint, and the route still draws.
        bookings.route = BookingFixtures.route(stops: [BookingFixtures.nearHalt])
        model.selectRoute(model.state.routes[0])
        await eventually("re-drawn") { await MainActor.run { model.state.walkHalt == nil } }
        XCTAssertTrue(model.state.walkPolyline.isEmpty)
        XCTAssertFalse(model.state.routePolyline.isEmpty, "the GTFS shape is still decoded")
    }

    // MARK: - The booking

    /// **R-18.** The `clientRequestId` **is** the idempotency key: ride-svc dedupes on
    /// `(passengerId, clientRequestId)`, and `ApiBookingRepository` sends the one as the other so
    /// that the transport's own retry of a timed-out send is a *replay* rather than a second ride.
    ///
    /// One id per attempt, minted by the screen rather than by the transport — a key the transport
    /// invented would be a different one on the replay, which is the bug R-18 exists to prevent.
    @MainActor
    func testTheBookingCarriesOneMintedClientRequestIdPerAttempt() async {
        let model = await started()
        await eventually("tiers") { await MainActor.run { !model.state.tiers.isEmpty } }
        model.selectTier(model.state.tiers[0])

        model.book()
        await eventually("booked") { await MainActor.run { model.state.booked != nil } }

        XCTAssertEqual(keys.issued, 1, "one id per attempt, from the generator the pipeline uses")
        XCTAssertEqual(bookings.requested.first?.clientRequestId, keys.value)
    }

    /// A second attempt after a failure is a second *attempt*, and gets its own id — the dedupe that
    /// matters is the transport's replay of one send, which carries the id it already had.
    @MainActor
    func testARetryAfterAFailureIsANewAttempt() async {
        bookings.requestFailure = BookingFakeError.unreachable
        let model = await started()
        await eventually("tiers") { await MainActor.run { !model.state.tiers.isEmpty } }
        model.selectTier(model.state.tiers[0])

        model.book()
        await eventually("failed") { await MainActor.run { model.state.errorKey != nil } }
        bookings.requestFailure = nil
        model.book()
        await eventually("landed") { await MainActor.run { model.state.booked != nil } }

        XCTAssertEqual(bookings.requested.count, 2)
        XCTAssertEqual(keys.issued, 2)
    }

    /// The booking carries the tier's own token — the thing that stops a client naming its own fare
    /// — and clears the draft, so the next booking does not inherit this one's rider.
    @MainActor
    func testABookingCarriesItsTokenAndThenClearsTheDraft() async {
        let model = await started()
        await eventually("tiers") { await MainActor.run { !model.state.tiers.isEmpty } }
        model.selectTier(model.state.tiers[0])

        model.book()
        await eventually("booked") { await MainActor.run { model.state.booked != nil } }

        XCTAssertEqual(bookings.requested.first?.fareEstimateToken, bookings.estimateToken)
        XCTAssertEqual(bookings.requested.first?.kind, RideKind.passenger)
        XCTAssertNil(draft.state.dropoff, "the draft is gone the moment the booking is a ride")
    }

    /// P-01 / P-05 — a proxy booking travels with the rider's name and number, and `isProxy` is
    /// derived from the kind rather than taken, so the two cannot disagree.
    @MainActor
    func testAProxyBookingCarriesTheRiderAndSaysItIsOne() async throws {
        draft.update {
            $0.bookingFor = .someoneElse
            $0.riderName = "Nimal"
            $0.riderPhone = BookingFixtures.riderPhone
        }
        let model = await started()
        await eventually("tiers") { await MainActor.run { !model.state.tiers.isEmpty } }
        model.selectTier(model.state.tiers[0])

        model.book()
        await eventually("booked") { await MainActor.run { model.state.booked != nil } }

        let sent = try XCTUnwrap(bookings.requested.first)
        XCTAssertEqual(sent.kind, RideKind.proxy)
        // `RideRequest.isProxy` collides with `NSObject.isProxy()`. Unwrapped and annotated, the
        // exported property is the only candidate that type-checks.
        let isProxy: KotlinBoolean? = sent.isProxy()
        XCTAssertEqual(isProxy?.boolValue, true)
        XCTAssertEqual(sent.riderName, "Nimal")
        XCTAssertEqual(sent.riderPhone, PhoneNumber.toE164(BookingFixtures.riderPhone))
    }

    /// A parcel quotes on the sizes P-06 says fit, not on the six passenger tiers.
    func testAParcelIsQuotedOnTheVehiclesItsSizeFits() {
        var parcel = BookingDraftState()
        parcel.subject = .parcel

        parcel.packageSize = PackageSize.s
        XCTAssertEqual(
            RideBookingModel.tiers(for: parcel).map(\.wire),
            [RideVehicleType.motorbike.wire, RideVehicleType.threeWheeler.wire]
        )

        parcel.packageSize = PackageSize.l
        let large = RideBookingModel.tiers(for: parcel).map(\.wire)
        XCTAssertEqual(large, [RideVehicleType.van.wire, RideVehicleType.miniTruck.wire, RideVehicleType.truck.wire])
        // AL-09's delivery-only pair appears here and nowhere else in the app.
        XCTAssertFalse(RideBookingModel.passengerTiers.map(\.wire).contains(RideVehicleType.truck.wire))
    }

    // MARK: -

    @MainActor
    private func started() async -> RideBookingModel {
        let model = RideBookingModel(draft: draft, bookings: bookings, keys: keys)
        model.start()
        return model
    }
}

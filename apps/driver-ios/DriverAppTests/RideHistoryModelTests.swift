import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-030 · ride history and the rate-passenger sheet** (US-8.8, US-18.2, AL-35).
///
/// Three rules carry the screen: the per-row detail fan-out (without which *"Rate ★"* is drawn on a
/// trip already rated), *"a Mode A/B session has no single passenger to rate"*, and the one route on
/// the platform that writes a driver-to-passenger rating.
@MainActor
final class RideHistoryModelTests: XCTestCase {

    private var identity = FakeDriverIdentity()
    private var history = FakeRideHistoryRepository()

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        history = FakeRideHistoryRepository()
        history.ride = rideDetail(riderId: testPassengerId)
    }

    private func makeModel() -> RideHistoryModel {
        RideHistoryModel(identity: identity, history: history)
    }

    // MARK: - The list and its fan-out

    /// `TripSummary` has neither distance nor rating, so a detail is read **per row** — and it is that
    /// read which decides whether the row offers to rate or shows the stars already left.
    func testEveryRowsDetailIsReadAndFoldedIn() async {
        history.summaries = [tripSummary(), tripSummary(tripId: "01JTRIP00000000000000002")]
        history.details = [
            testTripId: tripDetail(distanceKm: 8),
            "01JTRIP00000000000000002": tripDetail(tripId: "01JTRIP00000000000000002", rating: 5),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(Set(history.detailReads), [testTripId, "01JTRIP00000000000000002"])
        XCTAssertEqual(model.state.trips.first?.distanceKm, 8)
        XCTAssertEqual(model.state.trips.last?.rating, 5)
        XCTAssertFalse(model.state.trips.last?.isRateable ?? true, "a trip already rated is not rateable")
        XCTAssertTrue(model.state.trips.first?.isRateable ?? false)
    }

    /// **A failed detail is not a failed screen.** The row keeps its summary and simply shows no
    /// distance; losing a whole history because one polyline query timed out is the wrong trade on a
    /// roadside connection.
    func testARowWhoseDetailFailedKeepsItsSummary() async {
        history.summaries = [tripSummary()]
        history.details = [:]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.trips.count, 1)
        XCTAssertNil(model.state.trips.first?.distanceKm)
        XCTAssertNil(model.state.errorKey, "one dead detail is not a screen failure")
    }

    /// `distanceKm` is **absent** on the coarser geometry sources rather than understated, so the
    /// caption drops the part rather than printing a lower bound as a measurement.
    func testACaptionDropsThePartsItsSourceDidNotSend() async {
        history.summaries = [tripSummary(fareMinor: nil)]
        history.details = [testTripId: tripDetail(distanceKm: nil, geometrySource: GeometrySource.operational)]
        let model = makeModel()

        await model.refresh()

        let caption = model.state.trips.first?.captionText ?? ""
        XCTAssertFalse(caption.contains("km"), "no distance was sent: \(caption)")
        XCTAssertFalse(caption.contains(MoneyFormat.prefix), "a session has no fare: \(caption)")
    }

    /// A Mode A/B session is a vehicle's journey rather than one person's trip: it has no single
    /// passenger, and `DriverRatingInput` requires one. That is not a gap; it is what a bus journey is.
    func testASessionIsNeverRateable() async {
        history.summaries = [tripSummary(plane: TripPlane.session)]
        history.details = [testTripId: tripDetail()]
        let model = makeModel()

        await model.refresh()

        XCTAssertFalse(model.state.trips.first?.isRateable ?? true)
        await model.openRating(tripId: testTripId)
        XCTAssertNil(model.state.rating, "the sheet does not open on a session")
        XCTAssertTrue(history.rideReads.isEmpty)
    }

    // MARK: - The sheet

    /// The passenger is named by a second read, because a trip summary never says who the other party
    /// to the ride was.
    func testOpeningTheSheetNamesThePassengerFromTheRide() async {
        history.summaries = [tripSummary()]
        history.details = [testTripId: tripDetail()]
        let model = makeModel()
        await model.refresh()

        await model.openRating(tripId: testTripId)

        XCTAssertEqual(history.rideReads, [testTripId])
        XCTAssertEqual(model.state.rating?.passengerId, testPassengerId)
        XCTAssertEqual(model.state.rating?.passengerName, "Nimal")
        XCTAssertEqual(model.state.rating?.stars, 5, "the wireframe opens on five filled stars")
        XCTAssertTrue(model.state.rating?.canSubmit ?? false)
    }

    /// **P-01 — a proxy booking for an unregistered rider.** There is no `iam.users` row to be the
    /// `ratee_id`, so the CTA stays dead rather than posting a rating about nobody.
    func testARideWithNoRegisteredRiderCannotBeRated() async {
        history.ride = rideDetail(riderId: nil)
        history.summaries = [tripSummary()]
        history.details = [testTripId: tripDetail()]
        let model = makeModel()
        await model.refresh()

        await model.openRating(tripId: testTripId)

        XCTAssertNil(model.state.rating?.passengerId)
        XCTAssertFalse(model.state.rating?.canSubmit ?? true)
        await model.submitRating()
        XCTAssertTrue(history.ratings.isEmpty)
    }

    /// **The platform's only driver-rates-passenger route is session-scoped** — `ride.yaml` declares
    /// none at all. This pins the subject id so the gap cannot be quietly "fixed" by inventing a route.
    func testTheRatingIsSentToTheOneRouteThatExistsAndUpdatesTheRowLocally() async {
        history.summaries = [tripSummary()]
        history.details = [testTripId: tripDetail()]
        let model = makeModel()
        await model.refresh()
        await model.openRating(tripId: testTripId)
        model.onStarsChange(4)
        model.onCommentChange("  Polite  ")

        await model.submitRating()

        XCTAssertEqual(history.ratings.count, 1)
        XCTAssertEqual(history.ratings.first?.subjectId, testTripId, "the trip id is the subject")
        XCTAssertEqual(history.ratings.first?.passengerId, testPassengerId)
        XCTAssertEqual(history.ratings.first?.stars, 4)
        XCTAssertNil(model.state.rating, "the sheet closes")
        XCTAssertEqual(model.state.trips.first?.rating, 4, "the row shows the stars without a re-read")
        XCTAssertFalse(model.state.trips.first?.isRateable ?? true, "and cannot be rated a second time")
    }

    /// `trip-state.yaml`'s `RatingInput.stars` is `1..5`, so nothing outside it can be chosen.
    func testTheStarsAreClampedToTheContractsRange() async {
        history.summaries = [tripSummary()]
        history.details = [testTripId: tripDetail()]
        let model = makeModel()
        await model.refresh()
        await model.openRating(tripId: testTripId)

        model.onStarsChange(0)
        XCTAssertEqual(model.state.rating?.stars, 1)
        model.onStarsChange(9)
        XCTAssertEqual(model.state.rating?.stars, 5)
    }

    /// A refused rating — which is what a Mode C ride gets today, because the route it reaches takes a
    /// `sessionId` — leaves the sheet up with copy rather than dropping the tap.
    func testARefusedRatingKeepsTheSheetAndSaysWhy() async {
        history.summaries = [tripSummary()]
        history.details = [testTripId: tripDetail()]
        history.nextRatingFailure = apiFailure(code: "not-found", status: 404)
        let model = makeModel()
        await model.refresh()
        await model.openRating(tripId: testTripId)

        await model.submitRating()

        XCTAssertNotNil(model.state.rating, "the sheet stays up")
        XCTAssertFalse(model.state.rating?.isSubmitting ?? true)
        XCTAssertEqual(model.state.errorKey, "error_not_found")
        XCTAssertNil(model.state.trips.first?.rating, "nothing was recorded")
    }

    // MARK: - The empty state

    func testADriverWithNoTripsSeesTheEmptyState() async {
        history.summaries = []
        let model = makeModel()

        await model.refresh()

        XCTAssertTrue(model.state.isEmpty)
    }

    /// `GET /v1/trips/{driverId}` is driver-scoped, so with no session there is nothing to ask for.
    func testNoSessionMeansNoRead() async {
        identity.driverId = nil
        let model = makeModel()

        await model.refresh()

        XCTAssertFalse(model.state.isLoading)
        XCTAssertTrue(history.detailReads.isEmpty)
    }

    /// `★★★★☆` — the row's stars, drawn the way the wireframe draws them.
    func testTheStarsAreDrawnFilledThenEmpty() {
        XCTAssertEqual(RatingStars.text(5), "★★★★★")
        XCTAssertEqual(RatingStars.text(3), "★★★☆☆")
        XCTAssertEqual(RatingStars.text(9), "★★★★★", "a rating outside the range still draws five")
    }
}

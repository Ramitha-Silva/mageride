import MageRideShared
import XCTest

@testable import DriverApp

/// Where a `ride_offer` push becomes the live offer — the envelope half, which is pure and testable
/// with no session and no push service.
///
/// **The money parse is the part that matters.** notification-svc renders the fare for the SMS
/// fallback and puts the same string on the push, so the only number available before the ride is read
/// is `1,240.00`. Parsing it through a `Double` is exactly the bug C012's *"money is `Long` minor
/// units, never `Double`"* fence exists to prevent.
@MainActor
final class OfferInboxTests: XCTestCase {

    // MARK: - The envelope

    func testAnOfferNeedsBothIds() {
        XCTAssertNil(OfferInbox.offer(from: [:], driverId: testDriverId, now: Date()))
        XCTAssertNil(
            OfferInbox.offer(from: [OfferInbox.keyOfferId: testOfferId], driverId: testDriverId, now: Date())
        )
        XCTAssertNil(
            OfferInbox.offer(from: [OfferInbox.keyRideId: testRideId], driverId: testDriverId, now: Date())
        )
        XCTAssertNil(
            OfferInbox.offer(
                from: [OfferInbox.keyOfferId: "  ", OfferInbox.keyRideId: testRideId],
                driverId: testDriverId,
                now: Date()
            )
        )
    }

    func testTheDeadlineOnTheEnvelopeWinsOverTheLocalTtl() {
        let sent = Date(timeIntervalSince1970: 1_800_000_000)
        let offer = OfferInbox.offer(
            from: [
                OfferInbox.keyOfferId: testOfferId,
                OfferInbox.keyRideId: testRideId,
                OfferInbox.keyExpiresAt: "2027-01-15T09:41:13Z",
            ],
            driverId: testDriverId,
            now: sent
        )

        let deadline = offer.map { IosInstantKt.timestampEpochMillis(instant: $0.expiresAt) }
        XCTAssertEqual(deadline, 1_800_006_073_000, "2027-01-15T09:41:13Z, to the millisecond")
    }

    /// A push whose deadline this build cannot read still starts a fifteen-second clock: losing the
    /// ring is better than losing the ride.
    func testAnUnreadableDeadlineFallsBackToTheFifteenSecondWindow() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let offer = OfferInbox.offer(
            from: [
                OfferInbox.keyOfferId: testOfferId,
                OfferInbox.keyRideId: testRideId,
                OfferInbox.keyExpiresAt: "not a timestamp",
            ],
            driverId: testDriverId,
            now: now
        )

        let deadline = offer.map { IosInstantKt.timestampEpochMillis(instant: $0.expiresAt) }
        XCTAssertEqual(deadline, 1_800_000_015_000)
    }

    func testTheDriverIdOnTheOfferIsTheSignedInDriver() {
        let offer = OfferInbox.offer(
            from: [OfferInbox.keyOfferId: testOfferId, OfferInbox.keyRideId: testRideId],
            driverId: testDriverId,
            now: Date()
        )
        XCTAssertEqual(offer?.driverId, testDriverId)
        XCTAssertEqual(offer?.offerId, testOfferId)
        XCTAssertEqual(offer?.rideId, testRideId)
    }

    // MARK: - The rendered fare

    func testARenderedFareBecomesExactMinorUnits() {
        XCTAssertEqual(OfferInbox.rupeesToMinor("1,240.00"), 124_000)
        XCTAssertEqual(OfferInbox.rupeesToMinor("480"), 48_000)
        XCTAssertEqual(OfferInbox.rupeesToMinor("480.5"), 48_050, "one decimal digit is tenths of a rupee")
        XCTAssertEqual(OfferInbox.rupeesToMinor("0.01"), 1)
        XCTAssertEqual(OfferInbox.rupeesToMinor("-25.50"), -2_550)
    }

    func testAFareThisCannotReadIsAbsentRatherThanZero() {
        XCTAssertNil(OfferInbox.rupeesToMinor(nil))
        XCTAssertNil(OfferInbox.rupeesToMinor(""))
        XCTAssertNil(OfferInbox.rupeesToMinor("   "))
        XCTAssertNil(OfferInbox.rupeesToMinor("Rs 480"))
        XCTAssertNil(OfferInbox.rupeesToMinor("1.2.3"))
    }

    /// The rounding-free path: a value that a `Double` would not represent exactly still comes back
    /// exactly.
    func testALargeFareSurvivesWithNoFloatingPointDrift() {
        XCTAssertEqual(OfferInbox.rupeesToMinor("9,007,199,254.74"), 900_719_925_474)
    }
}

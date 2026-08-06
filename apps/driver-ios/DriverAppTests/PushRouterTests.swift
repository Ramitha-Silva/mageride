import XCTest

@testable import DriverApp

/// The deep links C051 mints, and what they open.
///
/// The same table as `apps/driver-android/.../push/PushRouter.kt` and the same assertions as its
/// `NavigationShellTest` — a `mageride://` host added on the server with no client route, or a
/// client that started trusting the URI it was handed, are both build failures here.
final class PushRouterTests: XCTestCase {

    /// `DeepLinks` in `Notification.Api/Messaging/EventHandlers.cs` mints exactly four.
    func testTheFourDeepLinksNotificationSvcMintsResolveToScreens() {
        XCTAssertEqual(PushRouter.resolve("mageride://ride/01JQ9F8Z"), .activeRide(rideId: "01JQ9F8Z"))
        XCTAssertEqual(PushRouter.resolve("mageride://wallet"), .wallet)
        XCTAssertEqual(PushRouter.resolve("mageride://documents"), .documents)

        // A package delivery IS a ride (R-01 keeps one aggregate), so both links land on the same
        // screen. The distinction the passenger app makes has no driver-side counterpart.
        XCTAssertEqual(PushRouter.resolve("mageride://package/01JQ9F8Z"), .activeRide(rideId: "01JQ9F8Z"))
    }

    /// **A deep link is resolved, never trusted.** An unrecognised or hostile value opens nothing.
    func testAnythingThatIsNotOursOpensNothing() {
        XCTAssertNil(PushRouter.resolve(nil))
        XCTAssertNil(PushRouter.resolve(""))
        XCTAssertNil(PushRouter.resolve("mageride://"))
        XCTAssertNil(PushRouter.resolve("mageride:///"))
        XCTAssertNil(PushRouter.resolve("https://mageride.lk/ride/01JQ"), "a scheme that is not ours")
        XCTAssertNil(PushRouter.resolve("mageride://settings"), "a host with no screen")
        XCTAssertNil(PushRouter.resolve("mageride://ride"), "a ride link with no ride")
        XCTAssertNil(PushRouter.resolve("mageride://ride/"), "…and one with an empty ride")
    }

    func testSurroundingWhitespaceDoesNotChangeWhatALinkOpens() {
        XCTAssertEqual(PushRouter.resolve("  mageride://wallet \n"), .wallet)
    }

    /// E-01's offer is a takeover the dashboard owns, so it routes to Home **whatever deeplink it
    /// carries** — `offer.created` mints the *passenger's* ride link, and following that would put
    /// the driver on a ride they have not accepted.
    func testARideOfferAlwaysOpensHome() {
        let offer = PushMessage(
            kind: PushMessage.kindRideOffer,
            deeplink: "mageride://ride/01JQ9F8Z",
            notificationId: "n1",
            data: [:]
        )
        XCTAssertEqual(PushRouter.route(for: offer), .home)
    }

    /// US-6A.15 — D2' §SCR-DI-018: "30-min reminder push deep-links here". It carries no deeplink to
    /// follow, so the routing is on the **type**, which D5' §14.4 and notification-svc's catalogue
    /// both fix.
    func testTheScheduledReminderRoutesOnItsTypeBecauseItCarriesNoLink() {
        let reminder = PushMessage(
            kind: PushMessage.kindScheduledReminder,
            deeplink: nil,
            notificationId: "n2",
            data: [:]
        )
        XCTAssertEqual(PushRouter.route(for: reminder), .scheduledRides)
    }

    func testAnyOtherKindFollowsItsDeeplinkAndOpensNothingWithout() {
        let wallet = PushMessage(kind: "WALLET_LOW", deeplink: "mageride://wallet", notificationId: nil, data: [:])
        XCTAssertEqual(PushRouter.route(for: wallet), .wallet)

        let silent = PushMessage(kind: "SOMETHING_NEW", deeplink: nil, notificationId: nil, data: [:])
        XCTAssertNil(PushRouter.route(for: silent))
    }

    /// notification-svc writes `kind`, `deeplink` and `notificationId` on every push; a non-string
    /// value is dropped rather than stringified, because a numeric `fare` arriving as JSON is a
    /// contract question and not something to guess at inside a delivery handler.
    func testAPayloadIsReadOffTheApnsUserInfo() {
        let message = PushMessage.from(userInfo: [
            "aps": ["alert": "…"],
            "kind": "ride_offer",
            "deeplink": "mageride://ride/01JQ",
            "notificationId": "01JN",
            "fare": 148000,
        ])
        XCTAssertEqual(message.kind, "ride_offer")
        XCTAssertEqual(message.deeplink, "mageride://ride/01JQ")
        XCTAssertEqual(message.notificationId, "01JN")
        XCTAssertNil(message.data["fare"], "a non-string extra is dropped, not coerced")
    }

    /// Two categories, mirroring the Android channels: the unit a user silences must not put a
    /// fifteen-second ride offer and a wallet reminder in one bucket.
    func testAnOfferGetsItsOwnCategoryAndEverythingElseShares() {
        XCTAssertEqual(PushCategory.category(for: PushMessage.kindRideOffer), PushCategory.rideOffers)
        XCTAssertEqual(PushCategory.category(for: "WALLET_LOW"), PushCategory.general)
        XCTAssertEqual(PushCategory.category(for: nil), PushCategory.general)
    }

    @MainActor
    func testTheRouterKeepsItsValueUntilItIsConsumed() {
        let router = PushRouter()
        XCTAssertNil(router.pending)

        router.offer(uri: "mageride://wallet")
        XCTAssertEqual(router.pending, .wallet, "a push before the shell exists is still delivered")

        router.consume()
        XCTAssertNil(router.pending, "…and is not delivered twice")
    }
}

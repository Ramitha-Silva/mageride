import XCTest

@testable import PassengerApp

/// Every way a push becomes a destination, and every way it must not.
///
/// The tables are `static` and pure, so this needs no running app — the same split the Android side
/// makes.
final class PushRouterTests: XCTestCase {

    // MARK: - The two kinds that carry no deep link

    /// P-02 / P-13. **The one push on this surface with no `deeplink` at all** —
    /// `LocationRequestAsync` writes `{kind, requestId, bookerName, ttl}` and nothing else, because
    /// SCR-PI-011 is a *silent* data message the app renders itself. The route is built from
    /// `data.requestId`.
    func testALocationRequestRoutesFromItsRequestId() {
        let message = PushMessage(
            kind: PushMessage.kindLocationRequest,
            deeplink: nil,
            notificationId: "N1",
            data: ["kind": PushMessage.kindLocationRequest, "requestId": "LR1", "ttl": "300"]
        )
        XCTAssertEqual(PushRouter.route(for: message), .confirmPickup(requestId: "LR1"))
    }

    /// A location request with no id opens nothing. Inventing a
    /// `mageride://pickup-confirm/{id}` host would be a client claiming a link the server does not
    /// mint; opening SCR-PI-011 with an empty id would be a screen that cannot confirm anything.
    func testALocationRequestWithNoIdOpensNothing() {
        for value in [nil, ""] as [String?] {
            var data = ["kind": PushMessage.kindLocationRequest]
            if let value { data["requestId"] = value }
            let message = PushMessage(kind: PushMessage.kindLocationRequest, deeplink: nil, notificationId: nil, data: data)
            XCTAssertNil(PushRouter.route(for: message))
        }
    }

    /// US-6A.15 / US-10.9 — *"your ride is in 30 minutes"*. It carries no deeplink either:
    /// `DeepLinks` mints four URIs and none names a scheduled ride, so the routing is on the **type**
    /// and SCR-PI-022's Scheduled tab is where an upcoming ride lives.
    func testTheScheduledReminderRoutesToTrips() {
        let message = PushMessage(
            kind: PushMessage.kindScheduledReminder,
            deeplink: nil,
            notificationId: "N2",
            data: ["kind": PushMessage.kindScheduledReminder]
        )
        XCTAssertEqual(PushRouter.route(for: message), .trips)
    }

    // MARK: - The four links notification-svc mints

    func testTheRideLinkOpensTheActiveRide() {
        XCTAssertEqual(PushRouter.resolve("mageride://ride/R1"), .activeRide(rideId: "R1"))
    }

    /// R-01 keeps one ride aggregate, but this side has two screens for a parcel where the driver
    /// side has one. Which to draw is a fact about the ride, so both audiences land on one
    /// destination — C099 reads the ride and picks.
    func testThePackageLinkOpensOneDestinationForBothParties() {
        XCTAssertEqual(PushRouter.resolve("mageride://package/R1"), .packageTracking(rideId: "R1"))
    }

    /// **`mageride://wallet` and `mageride://documents` are the driver's and resolve to nothing
    /// here.** A passenger has no wallet screen and no documents to expire, and notification-svc
    /// never addresses either to a passenger. Resolving them to "the nearest passenger screen" would
    /// open something arbitrary the first time an operator mis-targeted a broadcast.
    func testTheDriversTwoLinksOpenNothingOnThisSurface() {
        XCTAssertNil(PushRouter.resolve("mageride://wallet"))
        XCTAssertNil(PushRouter.resolve("mageride://documents"))
    }

    // MARK: - A link is resolved, not trusted

    func testAnUnrecognisedOrMalformedLinkOpensNothing() {
        let refused = [
            nil, "", "   ",
            "https://mageride.lk/ride/R1",
            "mageride://",
            "mageride:///",
            "mageride://nowhere/R1",
            "mageride://ride",
            "mageride://ride/",
            "ride/R1",
            "MAGERIDE://ride/R1",
        ] as [String?]

        for value in refused {
            XCTAssertNil(PushRouter.resolve(value), "\(value ?? "nil") should open nothing")
        }
    }

    /// Whitespace around a link is the server's formatting, not a different link.
    func testAPaddedLinkStillResolves() {
        XCTAssertEqual(PushRouter.resolve("  mageride://ride/R1\n"), .activeRide(rideId: "R1"))
    }

    /// A push with a kind the app does not special-case falls through to its deeplink, which is what
    /// makes every future notification type work without a client change.
    func testAnUnknownKindFallsThroughToItsDeeplink() {
        let message = PushMessage(
            kind: "SOMETHING_NEW",
            deeplink: "mageride://ride/R9",
            notificationId: "N3",
            data: [:]
        )
        XCTAssertEqual(PushRouter.route(for: message), .activeRide(rideId: "R9"))
    }

    // MARK: - Reading a payload

    /// APNs has no `data` envelope, so notification-svc's keys sit beside `aps` rather than inside
    /// it. Non-string values are dropped rather than stringified — a numeric field arriving as a
    /// JSON number is a contract question.
    func testAPayloadIsReadOffTheTopLevelAndKeepsOnlyStrings() {
        let message = PushMessage.from(userInfo: [
            "aps": ["alert": ["title": "Driver arriving"]],
            "kind": "RIDE_STATE",
            "deeplink": "mageride://ride/R1",
            "notificationId": "N4",
            "fare": 1240,
            42: "not a string key",
        ])

        XCTAssertEqual(message.kind, "RIDE_STATE")
        XCTAssertEqual(message.deeplink, "mageride://ride/R1")
        XCTAssertEqual(message.notificationId, "N4")
        XCTAssertNil(message.data["fare"])
        XCTAssertNil(message.data["aps"])
        XCTAssertEqual(message.data.count, 3)
    }

    // MARK: - The router as the shell sees it

    @MainActor
    func testAPendingDestinationSurvivesUntilItIsConsumed() {
        let router = PushRouter()
        XCTAssertNil(router.pending)

        router.offer(uri: "mageride://ride/R1")
        XCTAssertEqual(router.pending, .activeRide(rideId: "R1"))

        router.consume()
        XCTAssertNil(router.pending)
    }

    /// A push that opens nothing must not clear one that does — the delivery handler offers every
    /// push to the router, including the ones with no screen behind them.
    @MainActor
    func testAPushThatOpensNothingLeavesAPendingDestinationAlone() {
        let router = PushRouter()
        router.offer(uri: "mageride://ride/R1")

        router.offer(uri: "mageride://wallet")

        XCTAssertEqual(router.pending, .activeRide(rideId: "R1"))
    }

    /// The two categories, and the one kind that is not "general".
    func testTheNotificationCategorySplitFollowsTheServersPriority() {
        XCTAssertEqual(PushCategory.category(for: PushMessage.kindLocationRequest), PushCategory.rides)
        XCTAssertEqual(PushCategory.category(for: PushMessage.kindScheduledReminder), PushCategory.general)
        XCTAssertEqual(PushCategory.category(for: nil), PushCategory.general)
    }
}

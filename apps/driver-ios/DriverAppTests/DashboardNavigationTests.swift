import XCTest

@testable import DriverApp

/// Cluster 3's navigation contracts — where an offer lands, where a ride lands, and AL-31's fence.
///
/// All four are the kind of thing that is correct on the day it is written and quietly wrong a
/// component later: a hamburger reintroduced, a fifteen-second offer turned into a route a driver can
/// swipe out of, a ride pushed onto the wrong tab's stack.
@MainActor
final class DashboardNavigationTests: XCTestCase {

    /// **SCR-DI-014 is not a route.** A driver who swiped out of an offer they meant to accept has lost
    /// it, and fifteen seconds is not long enough to navigate anywhere. The offer belongs to Home, and
    /// this is what `PushRouter` means by routing a `ride_offer` push there.
    func testTheDispatchTakeoverIsNotADestination() {
        XCTAssertFalse(
            DriverRoute.staticRoutes.contains { $0.path.contains("offer") },
            "SCR-DI-014 is a takeover Home presents, not a destination"
        )
        XCTAssertEqual(PushRouter.route(for: offerPush()), .home)
    }

    /// A `ride_offer` push has to reach **both** subscribers: the router opens Home, the inbox fills
    /// the slot Home draws from. Either alone is a dashboard with nothing on it, or a takeover behind
    /// the wrong screen.
    func testARideOfferPushCarriesEverythingTheInboxNeeds() {
        let message = offerPush()
        XCTAssertEqual(PushRouter.route(for: message), .home)
        XCTAssertNotNil(OfferInbox.offer(from: message.data, driverId: testDriverId, now: Date()))
    }

    /// Cluster 3's four destinations all belong to Home's stack except the Menu tab's own root, which
    /// is its tab.
    func testTheDashboardDestinationsBelongToHomeAndTheMenuRootToMenu() {
        XCTAssertEqual(DriverRoute.home.tab, .home)
        XCTAssertEqual(DriverRoute.directional.tab, .home)
        XCTAssertEqual(DriverRoute.activeRide(rideId: testRideId).tab, .home)
        XCTAssertEqual(DriverRoute.menu.tab, .menu)
    }

    /// **AL-31.** The Menu tab is the drawer's whole replacement, so everything it opens stays on its
    /// own stack rather than throwing the driver onto Home.
    func testOpeningAMenuRowStaysOnTheMenuTab() {
        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.open(.menu)

        navigator.open(MenuDestination.myVehicles.route)

        XCTAssertEqual(navigator.tab, .menu)
        XCTAssertEqual(navigator.paths[.menu]?.count, 1)
        XCTAssertNil(navigator.paths[.home], "a Menu row must not push onto Home")
    }

    /// Winning an offer pushes the ride onto Home's own stack — Home is the tab's root and there is
    /// nothing under it to replace — and finishing pops straight back to the standby map.
    func testARideIsPushedOnHomeAndPoppedWhenItEnds() {
        let navigator = DriverNavigator()
        navigator.preSession = nil

        navigator.open(.activeRide(rideId: testRideId))
        XCTAssertEqual(navigator.tab, .home)
        XCTAssertEqual(navigator.paths[.home]?.count, 1)

        navigator.pop()
        XCTAssertEqual(navigator.paths[.home]?.count, 0)
    }

    /// The two takeovers SCR-DI-015 opens are presented over everything, tab bar included: a driver on
    /// an alarm screen must not be one tap from their wallet.
    func testTheCallAndSosScreensArePresentedRatherThanPushed() {
        XCTAssertTrue(DriverRoute.voipCall(rideId: testRideId).isFullScreenTakeover)
        XCTAssertTrue(DriverRoute.sos(rideId: testRideId).isFullScreenTakeover)
        XCTAssertFalse(DriverRoute.activeRide(rideId: testRideId).isFullScreenTakeover)

        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.open(.sos(rideId: testRideId))

        XCTAssertEqual(navigator.takeover, .sos(rideId: testRideId))
        XCTAssertNil(navigator.paths[.home])
    }

    private func offerPush() -> PushMessage {
        let data = [
            PushMessage.Keys.kind: PushMessage.kindRideOffer,
            OfferInbox.keyOfferId: testOfferId,
            OfferInbox.keyRideId: testRideId,
            OfferInbox.keyFare: "480.00",
        ]
        return PushMessage(
            kind: PushMessage.kindRideOffer,
            deeplink: nil,
            notificationId: "01JNOTIFY00000000000000001",
            data: data
        )
    }
}

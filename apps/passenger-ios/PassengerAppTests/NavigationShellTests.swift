import XCTest

@testable import PassengerApp

/// The route table, the tab set, and the two Section C deltas that shape this shell.
///
/// **The Android route table is typed out here and compared**, which is what turns *"keep both in
/// step"* into a build failure. A route added to `apps/passenger-android/.../nav/PassengerRoute.kt`
/// and not here is a passenger who can reach a screen on one platform and not the other; a *path*
/// that differs is a deep link that resolves on one platform and not the other.
final class NavigationShellTests: XCTestCase {

    /// `PassengerRoute.kt`'s `Static` list, path for path, in its order.
    private let androidStaticPaths = [
        "splash",
        "onboarding",
        "login",
        "profile-setup",
        "permissions/location",
        "map",
        "search",
        "booking",
        "booking/proxy-rider",
        "booking/package",
        "booking/schedule",
        "trips",
        "subscriptions",
        "addresses",
        "settings",
        "settings/profile",
        "support",
    ]

    /// `PassengerRoute.kt`'s `Patterns` list, with the argument names stripped — what a
    /// parameterised destination's `path` looks like once an id is substituted.
    private let androidParameterisedPaths = [
        "pickup-confirm/{id}",
        "ride/{id}/finding",
        "ride/{id}",
        "ride/{id}/payment-method",
        "ride/{id}/pay",
        "ride/{id}/summary",
        "ride/{id}/rate",
        "package/{id}",
        "trips/{id}",
        "private-transport?vehicleId={id}",
        "subscriptions/{id}/pay",
        "subscriptions/{id}/payments",
        "call/{id}",
        "sos/{id}",
    ]

    /// **The one route this table has and the Android one does not**, and it is the Section C delta
    /// `passenger_ios.html` states rather than a divergence: SCR-PI-033 is the Menu *tab* over a
    /// `List` here and a modal drawer there, so `PassengerTab.MenuTab` carries no route on Android.
    /// See ``PassengerTab``.
    private let iosOnlyPaths = ["menu"]

    func testTheStaticRouteTableMatchesAndroid() {
        let ours = PassengerRoute.staticRoutes.map(\.path)
        XCTAssertEqual(
            Set(ours).subtracting(iosOnlyPaths),
            Set(androidStaticPaths),
            "static route tables have drifted"
        )
        XCTAssertEqual(Set(ours).intersection(iosOnlyPaths), Set(iosOnlyPaths))
    }

    func testEveryParameterisedRouteMatchesAndroidsPattern() {
        let ours = [
            PassengerRoute.confirmPickup(requestId: "{id}"),
            .findingDriver(rideId: "{id}"),
            .activeRide(rideId: "{id}"),
            .paymentMethod(rideId: "{id}"),
            .payFare(rideId: "{id}"),
            .tripSummary(rideId: "{id}"),
            .rateDriver(rideId: "{id}"),
            .packageTracking(rideId: "{id}"),
            .tripDetails(tripId: "{id}"),
            .modeBRequest(vehicleId: "{id}"),
            .subscriptionPayment(subscriptionId: "{id}"),
            .subscriptionPayments(subscriptionId: "{id}"),
            .voipCall(rideId: "{id}"),
            .sos(rideId: "{id}"),
        ].map(\.path)

        XCTAssertEqual(Set(ours), Set(androidParameterisedPaths), "parameterised routes have drifted")
    }

    /// AL-23's whole point: opened from a Mode B marker the request arrives pre-filled, opened from
    /// the Menu tab it does not. One destination, two entry points — and on Android that costs an
    /// optional query argument the `composable(…)` has to declare nullable, where here it is simply
    /// an optional associated value.
    func testTheModeBRequestCarriesAnOptionalVehicleId() {
        XCTAssertEqual(PassengerRoute.modeBRequest(vehicleId: nil).path, "private-transport")
        XCTAssertEqual(PassengerRoute.modeBRequest(vehicleId: "").path, "private-transport")
        XCTAssertEqual(PassengerRoute.modeBRequest(vehicleId: "V1").path, "private-transport?vehicleId=V1")
    }

    func testNoTwoDestinationsShareAPath() {
        let all = PassengerRoute.staticRoutes.map(\.path)
        XCTAssertEqual(Set(all).count, all.count, "two static destinations resolve to one path")
    }

    // MARK: - The tab bar

    /// D2' §C maps Android's `NavigationBar` onto SwiftUI's `TabView`. Four tabs, in the wireframe's
    /// order.
    func testThereAreExactlyFourTabsInTheWireframesOrder() {
        XCTAssertEqual(PassengerTab.allCases, [.map, .trips, .support, .menu])
        XCTAssertEqual(PassengerTab.allCases.map(\.route.path), ["map", "trips", "support", "menu"])
    }

    /// **Δ Section C.** `PassengerTab.MenuTab.route` is `null` on Android because the drawer opens
    /// *over* whatever is on screen; here SCR-PI-033 is a destination. This assertion is the record
    /// of that difference — if a later component makes Menu route-less to "match Android", it fails
    /// and the wireframe's `Δ iOS` clause is what settles the argument.
    func testTheMenuTabIsADestinationOnThisPlatform() {
        XCTAssertEqual(PassengerTab.menu.route, .menu)
        XCTAssertTrue(PassengerRoute.staticRoutes.contains(.menu))
    }

    /// SCR-PI-033's five rows, in the wireframe's two groups and its order.
    func testTheMenuRowsAreTheWireframesFive() {
        XCTAssertEqual(
            PassengerMenuDestination.primary,
            [.privateTransport, .subscriptions, .savedAddresses, .settings]
        )
        XCTAssertEqual(PassengerMenuDestination.secondary, [.support])
        XCTAssertEqual(
            PassengerMenuDestination.allCases.count,
            PassengerMenuDestination.primary.count + PassengerMenuDestination.secondary.count,
            "a row was added to the enum and not to a group"
        )
    }

    /// *"Private transport"* is entered with no vehicle id (AL-23) — from the menu there is no
    /// marker to read one from.
    func testThePrivateTransportRowCarriesNoVehicleId() {
        XCTAssertEqual(PassengerMenuDestination.privateTransport.route, .modeBRequest(vehicleId: nil))
    }

    /// Every menu row goes somewhere the route table declares.
    func testEveryMenuRowResolvesToARegisteredDestination() {
        // `staticRoutes` omits the parameterised cases deliberately — its own doc comment says so —
        // and `privateTransport` is entered as the bare `modeBRequest(vehicleId: nil)`, whose path
        // carries no parameter. Added here rather than to `staticRoutes`, which is the placeholder
        // registry's key set and stays one row per drawable screen.
        let known = Set(PassengerRoute.staticRoutes.map(\.path))
            .union([PassengerRoute.modeBRequest(vehicleId: nil).path])
        for row in PassengerMenuDestination.allCases {
            XCTAssertTrue(known.contains(row.route.path), "\(row) points at an unregistered path")
        }
    }

    // MARK: - Presentation

    /// The five cluster-1 screens replace the tab bar entirely — `passenger_ios.html` draws no tab
    /// bar on any of them.
    func testExactlyTheClusterOneScreensArePreSession() {
        let preSession = PassengerRoute.staticRoutes.filter(\.isPreSession).map(\.path)
        XCTAssertEqual(
            Set(preSession),
            ["splash", "onboarding", "login", "profile-setup", "permissions/location"]
        )
    }

    /// Two takeovers, and both for the same reason: a passenger on an alarm screen must not be one
    /// tap from their trip history.
    func testOnlyTheCallAndTheAlarmAreFullScreenTakeovers() {
        XCTAssertTrue(PassengerRoute.voipCall(rideId: "R1").isFullScreenTakeover)
        XCTAssertTrue(PassengerRoute.sos(rideId: "R1").isFullScreenTakeover)
        XCTAssertTrue(PassengerRoute.staticRoutes.allSatisfy { !$0.isFullScreenTakeover })
        XCTAssertFalse(PassengerRoute.activeRide(rideId: "R1").isFullScreenTakeover)
    }

    /// A pre-session destination is never also a takeover — the shell presents them differently and
    /// a route that were both would be drawn twice.
    func testNoRouteIsBothPreSessionAndATakeover() {
        for route in PassengerRoute.staticRoutes {
            XCTAssertFalse(route.isPreSession && route.isFullScreenTakeover, "\(route.path)")
        }
    }

    // MARK: - The navigator

    @MainActor
    func testOpeningARouteSwitchesToItsTabAndPushes() {
        let navigator = PassengerNavigator()
        navigator.preSession = nil

        navigator.open(.tripDetails(tripId: "T1"))

        XCTAssertEqual(navigator.tab, .trips)
        XCTAssertEqual(navigator.paths[.trips]?.count, 1)
        XCTAssertNil(navigator.preSession)
    }

    /// A tab's own root is "select the tab", not "push a second copy of it". Without this a
    /// `mageride://package/{id}` link taken while Trips is showing stacks Trips on Trips and the back
    /// button goes nowhere visible.
    @MainActor
    func testOpeningATabsOwnRootPopsRatherThanPushes() {
        let navigator = PassengerNavigator()
        navigator.preSession = nil
        navigator.open(.tripDetails(tripId: "T1"))

        navigator.open(.trips)

        XCTAssertEqual(navigator.tab, .trips)
        XCTAssertEqual(navigator.paths[.trips]?.count, 0)
    }

    /// A takeover is presented, never pushed — so it does not appear on any tab's stack and cannot
    /// be swiped back to.
    @MainActor
    func testATakeoverIsPresentedRatherThanPushed() {
        let navigator = PassengerNavigator()
        navigator.preSession = nil

        navigator.open(.sos(rideId: "R1"))

        XCTAssertEqual(navigator.takeover, .sos(rideId: "R1"))
        XCTAssertTrue(navigator.paths.values.allSatisfy(\.isEmpty))
    }

    /// C014 raises `RouteToLogin` for every way a session can end, and what is on the stacks belongs
    /// to a passenger who is no longer signed in.
    @MainActor
    func testResetDropsEveryStackAndTheTakeover() {
        let navigator = PassengerNavigator()
        navigator.preSession = nil
        navigator.open(.tripDetails(tripId: "T1"))
        navigator.open(.voipCall(rideId: "R1"))

        navigator.reset(to: .login)

        XCTAssertEqual(navigator.preSession, .login)
        XCTAssertNil(navigator.takeover)
        XCTAssertTrue(navigator.paths.values.allSatisfy(\.isEmpty))
        XCTAssertEqual(navigator.tab, .map)
    }

    /// `replaceTop` is `popUpTo(current) { inclusive = true }` — what stops a swipe back from the
    /// active ride re-opening *"finding a driver"* for a ride that already has one.
    @MainActor
    func testReplaceTopDoesNotGrowTheStack() {
        let navigator = PassengerNavigator()
        navigator.preSession = nil
        navigator.open(.findingDriver(rideId: "R1"))
        let depth = navigator.paths[.map]?.count

        navigator.replaceTop(with: .activeRide(rideId: "R1"))

        XCTAssertEqual(navigator.paths[.map]?.count, depth)
    }

    /// A second tap on the selected tab pops it to its root, as iOS users expect.
    @MainActor
    func testTappingTheSelectedTabPopsItToRoot() {
        let navigator = PassengerNavigator()
        navigator.preSession = nil
        navigator.open(.tripDetails(tripId: "T1"))

        navigator.tabSelection.wrappedValue = .trips

        XCTAssertEqual(navigator.paths[.trips]?.count, 0)
    }
}

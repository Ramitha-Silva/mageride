import XCTest

@testable import DriverApp

/// The shell's navigation contracts: AL-31's tab bar, the route table, and its parity with Android.
///
/// All three are the kind of thing that is correct on the day it is written and quietly wrong six
/// components later — a drawer reintroduced by a screen group that wanted one, a route renamed on
/// one platform only. Asserting the tables is what makes either a build failure rather than a bug
/// report.
final class NavigationShellTests: XCTestCase {

    func testTheTabBarIsTheFourTabsAndMenuIsOneOfThem() {
        // AL-31: "The dashboard has NO top-left hamburger; navigation is the bottom-nav Menu tab."
        // The Menu TAB is the drawer's whole replacement, so it has to be a peer of the other three
        // rather than a corner affordance.
        XCTAssertEqual(
            DriverTab.allCases.map(\.route),
            [.home, .jobs, .wallet, .menu],
            "the driver_ios.html wireframe's [Home][Jobs][Wallet][≡], in order"
        )
        XCTAssertTrue(DriverTab.allCases.contains(.menu), "AL-31's Menu tab")
        XCTAssertEqual(DriverTab.allCases.count, 4)
    }

    func testEveryTabHasADistinctSymbolAndLabel() {
        XCTAssertEqual(Set(DriverTab.allCases.map(\.symbolName)).count, DriverTab.allCases.count)
        XCTAssertEqual(Set(DriverTab.allCases.map(\.labelKey)).count, DriverTab.allCases.count)
    }

    func testEveryStaticRouteHasADistinctPath() {
        let paths = DriverRoute.staticRoutes.map(\.path)
        XCTAssertEqual(Set(paths).count, paths.count, "two destinations share a path")
        XCTAssertTrue(paths.allSatisfy { !$0.isEmpty }, "a destination has no path")
    }

    /// **The parity fence, as a test.** These are byte-for-byte the `path` values in
    /// `apps/driver-android/src/main/kotlin/lk/mageride/driver/nav/DriverRoute.kt`, typed out here
    /// rather than derived — the C067 handoff's ask ("keep both in step; a divergence shows up as
    /// two apps that look almost the same") only means something if a divergence fails a build.
    ///
    /// A route added on Android goes here in the same commit as the iOS screen group that needs it.
    func testTheRouteTableMatchesTheAndroidShellsPathForPath() {
        let android = [
            "splash",
            "onboarding/lang-city",
            "login",
            "profile-setup",
            "permissions",
            "vehicle/onboard",
            "document/capture",
            "vehicle/onboard/status",
            "vehicles",
            "home",
            "standby/directional",
            "jobs",
            "jobs/scheduled",
            "driver/level",
            "earnings",
            "wallet",
            "wallet/top-up",
            "wallet/request-credit",
            "wallet/transfer",
            "wallet/history",
            "menu",
            "documents",
            "profile",
            "support",
            "vehicle/tracker",
            "sharing",
            "history",
            "notifications",
        ]
        XCTAssertEqual(DriverRoute.staticRoutes.map(\.path), android)
    }

    /// The three parameterised destinations, whose Android counterparts are `ride/{rideId}`,
    /// `call/{rideId}` and `sos/{rideId}`.
    func testTheParameterisedRoutesCarryTheirRideId() {
        XCTAssertEqual(DriverRoute.activeRide(rideId: "01JQ").path, "ride/01JQ")
        XCTAssertEqual(DriverRoute.voipCall(rideId: "01JQ").path, "call/01JQ")
        XCTAssertEqual(DriverRoute.sos(rideId: "01JQ").path, "sos/01JQ")
        XCTAssertNotEqual(DriverRoute.activeRide(rideId: "a"), DriverRoute.activeRide(rideId: "b"))
    }

    /// Cluster 1 replaces the whole tab bar — `driver_ios.html` draws no tab bar on any of it — and
    /// the two takeovers cover it. Everything else lives inside a tab.
    func testTheTabBarIsHiddenExactlyWhereTheWireframeHidesIt() {
        let preSession: [DriverRoute] = [.splash, .languageCity, .login, .profileSetup, .permissions]
        for route in preSession {
            XCTAssertTrue(route.isPreSession, "\(route.path) must replace the tab bar")
            XCTAssertFalse(route.isFullScreenTakeover)
        }

        // SCR-DI-005 is a viewfinder; SCR-DI-031 and SCR-DI-032 are takeovers with no tab bar — a
        // driver on an alarm screen must not be one tap from their wallet.
        let takeovers: [DriverRoute] = [.documentCapture, .voipCall(rideId: "r"), .sos(rideId: "r")]
        for route in takeovers {
            XCTAssertTrue(route.isFullScreenTakeover, "\(route.path) must cover the tab bar")
            XCTAssertFalse(route.isPreSession)
        }

        for route in DriverRoute.staticRoutes where !preSession.contains(route) && route != .documentCapture {
            XCTAssertFalse(route.isFullScreenTakeover, "\(route.path) is an ordinary pushed screen")
        }
    }

    /// Every tab's own route belongs to that tab, or selecting it would push it onto another.
    func testEachTabsRootBelongsToThatTab() {
        for tab in DriverTab.allCases {
            XCTAssertEqual(tab.route.tab, tab)
        }
    }

    /// The wallet cluster hangs off the wallet tab, the jobs cluster off jobs, and everything the
    /// Menu reaches off Menu — so a deep link never lands a driver in a stack they cannot back out
    /// of into something sensible.
    func testDestinationsAreGroupedUnderTheTabThatReachesThem() {
        XCTAssertEqual(DriverRoute.walletTopUp.tab, .wallet)
        XCTAssertEqual(DriverRoute.walletHistory.tab, .wallet)
        XCTAssertEqual(DriverRoute.scheduledRides.tab, .jobs)
        XCTAssertEqual(DriverRoute.earnings.tab, .jobs)
        XCTAssertEqual(DriverRoute.profile.tab, .menu)
        XCTAssertEqual(DriverRoute.trackerPairing.tab, .menu)
        XCTAssertEqual(DriverRoute.vehicles.tab, .menu)
        XCTAssertEqual(DriverRoute.directional.tab, .home)
        XCTAssertEqual(DriverRoute.activeRide(rideId: "r").tab, .home)
    }

    // MARK: - The navigator

    @MainActor
    func testOpeningATabsOwnRouteSelectsItRatherThanPushingIt() {
        let navigator = DriverNavigator()
        navigator.preSession = nil

        navigator.open(.wallet)
        XCTAssertEqual(navigator.tab, .wallet)
        XCTAssertEqual(navigator.paths[.wallet]?.count ?? 0, 0, "a tab root must not stack on itself")

        navigator.open(.walletHistory)
        XCTAssertEqual(navigator.paths[.wallet]?.count, 1)
    }

    @MainActor
    func testOpeningACrossTabDestinationSwitchesTabsFirst() {
        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.tab = .home

        navigator.open(.scheduledRides)
        XCTAssertEqual(navigator.tab, .jobs)
        XCTAssertEqual(navigator.paths[.jobs]?.count, 1)
        XCTAssertEqual(navigator.paths[.home]?.count ?? 0, 0)
    }

    @MainActor
    func testATakeoverIsPresentedAndAPreSessionRouteReplacesTheRoot() {
        let navigator = DriverNavigator()
        navigator.preSession = nil

        navigator.open(.sos(rideId: "01JQ"))
        XCTAssertEqual(navigator.takeover, .sos(rideId: "01JQ"))
        XCTAssertEqual(navigator.paths[.home]?.count ?? 0, 0)

        navigator.open(.login)
        XCTAssertEqual(navigator.preSession, .login)
    }

    /// C014 raises `RouteToLogin` for every way a session can end — logout, a failed refresh,
    /// `403 device-revoked` (AL-08), PDPA erasure — and what is on the stacks belongs to a driver
    /// who is no longer signed in.
    @MainActor
    func testResettingToLoginDropsEveryStackAndAnyTakeover() {
        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.open(.walletHistory)
        navigator.open(.sos(rideId: "01JQ"))

        navigator.reset(to: .login)

        XCTAssertEqual(navigator.preSession, .login)
        XCTAssertNil(navigator.takeover)
        XCTAssertTrue(navigator.paths.values.allSatisfy { $0.isEmpty })
        XCTAssertEqual(navigator.tab, .home)
    }

    /// A second tap on the selected tab pops it, which is what an iOS user expects and what the
    /// Android shell expresses as `popUpTo(Home) { saveState = true }`.
    @MainActor
    func testTappingTheSelectedTabPopsItToItsRoot() {
        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.open(.walletHistory)
        XCTAssertEqual(navigator.paths[.wallet]?.count, 1)

        navigator.tabSelection.wrappedValue = .wallet
        XCTAssertEqual(navigator.paths[.wallet]?.count ?? 0, 0)
    }
}

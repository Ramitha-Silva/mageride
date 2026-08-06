import XCTest

@testable import DriverApp

/// The first-run gate (AL-27, US-2.21, D1' B.7 phase 1).
///
/// This is the one piece of C086 that decides what a driver sees on every cold start, and three
/// separate callers ask it — the splash, the login screen after a verify, and Profile Setup after a
/// save. A table test is what keeps them agreeing.
final class OnboardingRouterTests: XCTestCase {

    func testAFreshInstallGoesToLanguageAndCity() {
        XCTAssertEqual(
            OnboardingRouter.next(
                signedIn: false,
                firstRunComplete: false,
                profileComplete: false,
                permissionsAcknowledged: false
            ),
            .languageCity
        )
    }

    /// Checked **first**, even for a driver who is already signed in: a driver who has not chosen a
    /// language would otherwise meet a screen in whatever locale the handset is set to, which for
    /// most drivers here is not one of the three.
    func testLanguageAndCityComeBeforeEverythingElse() {
        XCTAssertEqual(
            OnboardingRouter.next(
                signedIn: true,
                firstRunComplete: false,
                profileComplete: true,
                permissionsAcknowledged: true
            ),
            .languageCity
        )
    }

    func testASignedOutDriverWhoHasChosenGoesToLogin() {
        XCTAssertEqual(
            OnboardingRouter.next(
                signedIn: false,
                firstRunComplete: true,
                profileComplete: false,
                permissionsAcknowledged: false
            ),
            .login
        )
    }

    /// US-2.21 / Change 6/22: driver identity precedes Home.
    func testASignedInDriverWithNoProfileGoesToProfileSetup() {
        XCTAssertEqual(
            OnboardingRouter.next(
                signedIn: true,
                firstRunComplete: true,
                profileComplete: false,
                permissionsAcknowledged: true
            ),
            .profileSetup
        )
    }

    func testPermissionsAreTheLastGate() {
        XCTAssertEqual(
            OnboardingRouter.next(
                signedIn: true,
                firstRunComplete: true,
                profileComplete: true,
                permissionsAcknowledged: false
            ),
            .permissions
        )
    }

    /// **AL-27's fence, as a test.** Nothing about a vehicle is an input here, so nothing about a
    /// vehicle can stand between Profile Setup and Home — a driver reaches the dashboard with none
    /// registered, and the Mode-C wizard (C087) is optional and reached from the Menu.
    func testADriverWithNoVehicleStillReachesHome() {
        XCTAssertEqual(
            OnboardingRouter.next(
                signedIn: true,
                firstRunComplete: true,
                profileComplete: true,
                permissionsAcknowledged: true
            ),
            .home
        )
    }

    /// Every destination is a route the shell already registered — C085 fixed the table before any
    /// screen group existed, and a router that invented a case would not compile.
    func testEveryDestinationMapsToAPreSessionRouteExceptHome() {
        for destination in OnboardingDestination.allCases {
            if destination == .home {
                XCTAssertFalse(destination.route.isPreSession, "Home is inside the tab bar")
            } else {
                XCTAssertTrue(destination.route.isPreSession, "\(destination.route.path) has no tab bar")
            }
        }
    }

    /// The five paths, byte-for-byte the Android shell's. `NavigationShellTests` asserts the whole
    /// table; this asserts that cluster 1's router points at the right rows of it.
    func testTheDestinationsCarryTheAndroidPaths() {
        XCTAssertEqual(OnboardingDestination.languageCity.route.path, "onboarding/lang-city")
        XCTAssertEqual(OnboardingDestination.login.route.path, "login")
        XCTAssertEqual(OnboardingDestination.profileSetup.route.path, "profile-setup")
        XCTAssertEqual(OnboardingDestination.permissions.route.path, "permissions")
        XCTAssertEqual(OnboardingDestination.home.route.path, "home")
    }
}

import SwiftUI
import XCTest

@testable import DriverApp

/// The three moves cluster 2 makes that are not an ordinary push.
///
/// `DriverNavHost.kt` writes them as `navigate(x) { popUpTo(current) { inclusive = true } }`;
/// ``DriverNavigator/replaceTop(with:)`` is the same thing on a `NavigationPath`. What it buys is
/// what a swipe back does afterwards — a driver who has just submitted Step 4/4 must not be able to
/// swipe back into it.
@MainActor
final class VehicleNavigationTests: XCTestCase {

    /// The wizard hands over to SCR-DI-006 and leaves nothing of itself on the stack.
    func testSubmittingTheWizardReplacesItRatherThanStackingOnIt() {
        let navigator = DriverNavigator()

        navigator.open(.vehicles)
        navigator.open(.vehicleOnboarding)
        XCTAssertEqual(navigator.paths[.menu]?.count, 2)

        navigator.replaceTop(with: .vehicleOnboardingStatus)

        XCTAssertEqual(navigator.tab, .menu)
        XCTAssertEqual(navigator.paths[.menu]?.count, 2, "the wizard was replaced, not stacked on")
    }

    /// SCR-DI-006's *"Go to My Vehicles"* does the same in the other direction.
    func testTheStatusScreenReplacesItselfWithMyVehicles() {
        let navigator = DriverNavigator()

        navigator.open(.vehicleOnboarding)
        navigator.replaceTop(with: .vehicleOnboardingStatus)
        navigator.replaceTop(with: .vehicles)

        XCTAssertEqual(navigator.paths[.menu]?.count, 1)
    }

    /// *"Back exits the wizard"* from Step 1/4 (D2' §SCR-DI-004) — a pop, because the wizard hides
    /// the system's own back button and owns the gesture itself.
    func testExitingTheWizardPopsOneDestination() {
        let navigator = DriverNavigator()

        navigator.open(.vehicles)
        navigator.open(.vehicleOnboarding)
        navigator.pop()

        XCTAssertEqual(navigator.paths[.menu]?.count, 1)
    }

    func testPoppingAnEmptyStackDoesNothing() {
        let navigator = DriverNavigator()
        navigator.tab = .menu

        navigator.pop()

        XCTAssertEqual(navigator.paths[.menu]?.count ?? 0, 0)
    }

    /// SCR-DI-005 is a takeover, not a push: it is presented over whichever screen asked for the
    /// capture, so that screen is still there when the image comes back.
    func testTheScannerIsPresentedOverWhateverAskedForIt() {
        let navigator = DriverNavigator()

        navigator.open(.vehicleOnboarding)
        navigator.open(.documentCapture)

        XCTAssertEqual(navigator.takeover, .documentCapture)
        XCTAssertEqual(navigator.paths[.menu]?.count, 1, "the wizard is still underneath")

        navigator.closeTakeover()
        XCTAssertNil(navigator.takeover)
    }

    /// All four of cluster 2's destinations hang off the Menu tab — SCR-DI-036's rows are what
    /// reaches them, and AL-31 makes Menu the only navigation entry point.
    func testEveryVehicleDestinationBelongsToTheMenuTab() {
        for route in [
            DriverRoute.vehicles,
            DriverRoute.vehicleOnboarding,
            DriverRoute.vehicleOnboardingStatus,
        ] {
            XCTAssertEqual(route.tab, .menu, "\(route.path)")
        }
        XCTAssertTrue(DriverRoute.documentCapture.isFullScreenTakeover)
    }
}

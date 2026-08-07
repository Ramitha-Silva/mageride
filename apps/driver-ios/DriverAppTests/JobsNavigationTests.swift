import MageRideShared
import XCTest

@testable import DriverApp

/// Cluster 3's second half — where the four C090 screens live, and the copy and tokens they draw.
///
/// The navigation half is the kind of thing that is correct on the day it is written and quietly
/// wrong a component later: a screen pushed onto the wrong tab's stack, or a tab root stacked on
/// itself so the back button goes nowhere visible.
@MainActor
final class JobsNavigationTests: XCTestCase {

    /// All four belong to the **Jobs** tab: SCR-DI-017 is its root and the other three hang off it.
    func testTheFourDestinationsBelongToTheJobsTab() {
        XCTAssertEqual(DriverRoute.jobs.tab, .jobs)
        XCTAssertEqual(DriverRoute.scheduledRides.tab, .jobs)
        XCTAssertEqual(DriverRoute.driverLevel.tab, .jobs)
        XCTAssertEqual(DriverRoute.earnings.tab, .jobs)
        XCTAssertEqual(DriverTab.jobs.route, .jobs, "the Job Board is the tab's root")
    }

    /// None of them is a takeover and none of them is pre-session — a driver reaches all four with a
    /// tab bar under them and a way back.
    func testNoneOfThemIsATakeoverOrPreSession() {
        for route in [DriverRoute.jobs, .scheduledRides, .driverLevel, .earnings] {
            XCTAssertFalse(route.isFullScreenTakeover, "\(route.path) is a screen, not an alarm")
            XCTAssertFalse(route.isPreSession, "\(route.path) needs a signed-in driver")
        }
    }

    /// SCR-DI-019 and SCR-DI-020 are opened from the **dashboard** — the `L3` badge and the
    /// *"Today: 4 trips · Rs 3,180"* line, which are the only entry points D2' names. Both therefore
    /// cross tabs, and the navigator switching first is what makes the back button say `‹ Job Board`
    /// rather than dropping the push on Home's stack.
    func testOpeningTheLevelScreenFromHomeSwitchesToTheJobsTab() {
        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.open(.home)

        navigator.open(.driverLevel)

        XCTAssertEqual(navigator.tab, .jobs)
        XCTAssertFalse(navigator.paths[.jobs]?.isEmpty ?? true)
        XCTAssertTrue(navigator.paths[.home]?.isEmpty ?? true, "nothing was pushed onto Home")
    }

    /// The Job Board is the tab's root, so opening it selects the tab rather than stacking a second
    /// copy of it — ``DriverNavigator/open(_:)``'s own rule, asserted here because this is the tab
    /// that has a root screen worth landing on.
    func testOpeningTheJobBoardPopsTheJobsTabToItsRoot() {
        let navigator = DriverNavigator()
        navigator.preSession = nil
        navigator.open(.scheduledRides)
        XCTAssertFalse(navigator.paths[.jobs]?.isEmpty ?? true)

        navigator.open(.jobs)

        XCTAssertEqual(navigator.tab, .jobs)
        XCTAssertTrue(navigator.paths[.jobs]?.isEmpty ?? true)
    }

    // MARK: - Copy

    /// ``LocalizationTests`` proves the three files agree with each other; this proves the *code*
    /// names keys those files actually carry — a key that exists in none of them agrees with itself
    /// perfectly and renders as its own name.
    func testEveryKeyTheseThreeScreensRenderHasAnEntry() {
        let keys = [
            "job_board_title",
            "job_board_radius",
            "job_board_loading",
            "job_board_empty",
            "job_board_level_gate",
            "job_board_post_intent",
            "job_board_intent_posted",
            "job_board_expired",
            "job_board_no_position",
            "job_board_unavailable",
            "schedule_today",
            "schedule_tomorrow",
            "scheduled_title",
            "scheduled_empty",
            "scheduled_accepted",
            "scheduled_in_minutes",
            "scheduled_reminder_fired",
            "scheduled_cancel",
            "level_title",
            "level_loading",
            "level_points_to_next",
            "level_top",
            "level_acceptance",
            "level_no_shows",
            "level_reports_warning",
        ]

        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no entry in Localizable.strings")
        }
    }

    /// The values on these screens that are **data rather than copy**, so a translator never sees
    /// them and `LocalizationTests` never fails on three identical entries.
    func testTheSymbolsOnTheseScreensAreConstantsRatherThanStrings() {
        XCTAssertEqual(LevelLabels.percent(92), "92%")
        XCTAssertEqual(LevelLabels.unknown, MageRideSymbols.unknown, "one em dash in the app, not two")
        XCTAssertEqual(DashboardLabels.level(3), "L3")
        XCTAssertEqual(
            MoneyFormat.radius(metres: Int(JobBoard.companion.CATCHMENT_METRES)),
            "30 km",
            "the catchment the app bar prints is D-06's own constant, and a radius is not a measurement"
        )
        XCTAssertEqual(MoneyFormat.distance(metres: 30_000), "30.0 km", "a measured distance keeps its tenth")
    }

    // MARK: - Tokens

    /// §0.2's rule is that a view reaches for a token and never a raw number; these three are this
    /// component's additions, and they are the wireframe's own measurements.
    func testTheNewControlTokensAreTheWireframesMeasurements() {
        XCTAssertEqual(MageRideControl.levelBadge, 96)
        XCTAssertEqual(MageRideControl.levelProgress, 8)
        XCTAssertEqual(MageRideControl.earningsChart, 88)
    }
}

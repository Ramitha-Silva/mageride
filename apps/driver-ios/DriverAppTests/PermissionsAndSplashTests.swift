import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-007 — the two rows D2' gives this platform, and the Settings deep link.
@MainActor
final class PermissionsModelTests: XCTestCase {

    /// **Δ Section C.** D2' §SCR-DI-007's iOS clause is *"`requestAlwaysAuthorization` +
    /// notification auth"*, and `driver_ios.html` draws exactly two rows. The other three Android
    /// rows are not omissions: foreground and background location are one grant here, and neither
    /// the battery-optimisation exemption nor "display over other apps" exists on iOS at all.
    func testThereAreExactlyTwoRows() {
        XCTAssertEqual(DriverPermission.allCases, [.locationAlways, .notifications])
    }

    func testEveryRowHasItsOwnCopyAndGlyph() {
        XCTAssertEqual(Set(DriverPermission.allCases.map(\.titleKey)).count, DriverPermission.allCases.count)
        XCTAssertEqual(Set(DriverPermission.allCases.map(\.rationaleKey)).count, DriverPermission.allCases.count)
        XCTAssertEqual(Set(DriverPermission.allCases.map(\.symbolName)).count, DriverPermission.allCases.count)
    }

    func testAskingAGrantableRowGrantsIt() async {
        let permissions = FakeDriverPermissions()
        permissions.grantsOnRequest = [.notifications]
        let model = PermissionsModel(permissions: permissions, preferences: FakeOnboardingPreferences())

        await model.refresh()
        XCTAssertFalse(model.state.isGranted(.notifications))

        await model.ask(.notifications)

        XCTAssertEqual(permissions.requested, [.notifications])
        XCTAssertTrue(model.state.isGranted(.notifications))
    }

    /// A permission iOS has already refused shows no sheet at all — the call returns with nothing on
    /// screen and the driver concludes the switch is broken. D2's *"denied → Settings deep-link"* is
    /// what happens instead.
    func testAPermanentlyDeniedRowGoesToSettingsRatherThanAskingAgain() async {
        let permissions = FakeDriverPermissions()
        permissions.permanentlyDenied = [.locationAlways]
        let model = PermissionsModel(permissions: permissions, preferences: FakeOnboardingPreferences())

        await model.ask(.locationAlways)

        XCTAssertTrue(permissions.requested.isEmpty, "the system would have shown nothing")
        XCTAssertEqual(permissions.settingsOpenedFor, 1)
    }

    /// A granted permission cannot be revoked from inside the app, so the switch only ever travels
    /// one way here; the OS's own settings are where it comes back off.
    func testAGrantedRowIsNotAskedAgain() async {
        let permissions = FakeDriverPermissions()
        permissions.granted = [.notifications]
        let model = PermissionsModel(permissions: permissions, preferences: FakeOnboardingPreferences())
        await model.refresh()

        await model.ask(.notifications)

        XCTAssertTrue(permissions.requested.isEmpty)
        XCTAssertEqual(permissions.settingsOpenedFor, 0)
    }

    /// **AL-27.** Continue is never disabled and acknowledging is all it records — the *grants* are
    /// the OS's and are asked for again on the dashboard, so a refusal does not trap a driver in
    /// this screen on every cold start.
    func testContinuingRecordsOnlyThatTheScreenWasShown() {
        let preferences = FakeOnboardingPreferences()
        let model = PermissionsModel(permissions: FakeDriverPermissions(), preferences: preferences)

        model.acknowledge()

        XCTAssertTrue(preferences.permissionsAcknowledged)
    }

    /// A Settings trip reports back no other way — re-reading on return to the foreground is what
    /// makes the toggles true after one.
    func testRefreshingRereadsEveryRow() async {
        let permissions = FakeDriverPermissions()
        let model = PermissionsModel(permissions: permissions, preferences: FakeOnboardingPreferences())

        await model.refresh()
        XCTAssertFalse(model.state.isGranted(.locationAlways))

        permissions.granted = [.locationAlways]
        await model.refresh()

        XCTAssertTrue(model.state.isGranted(.locationAlways))
        XCTAssertEqual(permissions.refreshCount, 2)
    }
}

/// SCR-DI-001 — the boot router, and the two questions it is allowed to skip.
@MainActor
final class SplashModelTests: XCTestCase {

    func testAFreshInstallIsSentToLanguageAndCityWithoutAskingTheServerAnything() async {
        let profiles = FakeDriverProfileRepository()
        let model = SplashModel(
            sessions: FakeDriverSessions(),
            profiles: profiles,
            preferences: FakeOnboardingPreferences()
        )

        await model.route()

        XCTAssertEqual(model.destination, .languageCity)
        XCTAssertEqual(profiles.nameCallCount, 0, "there is nobody to have a profile")
    }

    /// The third question is skipped entirely for a signed-out driver — the splash blocks on the
    /// smallest thing that can answer.
    func testASignedOutDriverNeverCostsAProfileCall() async {
        let profiles = FakeDriverProfileRepository()
        let sessions = FakeDriverSessions()
        let model = SplashModel(sessions: sessions, profiles: profiles, preferences: FakeOnboardingPreferences.firstRunDone)

        await model.route()

        XCTAssertEqual(model.destination, .login)
        XCTAssertEqual(sessions.restoreCount, 1)
        XCTAssertEqual(profiles.nameCallCount, 0)
    }

    /// **A failed profile call answers "has a profile", and that is deliberate.** This path runs on
    /// a session restored from the Keychain, so the driver has signed in before and has therefore
    /// already been through Profile Setup. Answering the other way would put a working driver back
    /// on an onboarding form because of a flat tunnel.
    func testAFailedProfileCallLandsAWorkingDriverOnTheDashboard() async {
        let sessions = FakeDriverSessions()
        sessions.isSignedIn = true
        let profiles = FakeDriverProfileRepository()
        profiles.nameFailure = TestFailure()
        let preferences = FakeOnboardingPreferences.firstRunDone
        preferences.permissionsAcknowledged = true

        let model = SplashModel(sessions: sessions, profiles: profiles, preferences: preferences)
        await model.route()

        XCTAssertEqual(model.destination, .home)
    }

    /// A profile that genuinely has no name is not the same thing as a call that failed, and both
    /// arrive as `nil` through `try?` — which is why the router reads them through `do`/`catch`.
    func testASignedInDriverWithNoNameGoesToProfileSetup() async {
        let sessions = FakeDriverSessions()
        sessions.isSignedIn = true
        let profiles = FakeDriverProfileRepository()
        profiles.name = nil

        let model = SplashModel(sessions: sessions, profiles: profiles, preferences: FakeOnboardingPreferences.firstRunDone)
        await model.route()

        XCTAssertEqual(model.destination, .profileSetup)
    }

    /// SwiftUI may run a `.task` again after a scene change, and routing twice would replace a root
    /// the driver has already moved on from.
    func testRoutingTwiceDecidesOnce() async {
        let sessions = FakeDriverSessions()
        let model = SplashModel(
            sessions: sessions,
            profiles: FakeDriverProfileRepository(),
            preferences: FakeOnboardingPreferences.firstRunDone
        )

        await model.route()
        await model.route()

        XCTAssertEqual(sessions.restoreCount, 1)
    }
}

/// D-26 — a driver never reads a `ProblemDetails` string, and every failure resolves to a key that
/// exists in all three languages.
@MainActor
final class OnboardingErrorsTests: XCTestCase {

    /// **A Kotlin exception does not cross the bridge as itself**: Kotlin/Native wraps it in an
    /// `NSError` under `KotlinException`. Without the unwrap every failure in the app would resolve
    /// to the generic message, which is exactly the bug this asserts against.
    func testAnUnwrappedNsErrorStillFindsItsKotlinCause() {
        // `MageRideError.CircuitOpen` rather than `.Network`, only because it is the one arm whose
        // constructor takes no `KotlinThrowable`. Kotlin's nested types are flattened by the export,
        // which is why the name reads the way it does.
        let cause = MageRideError.CircuitOpen(service: ApiService.content, retryAfterMillis: 1_000)
        let wrapped = NSError(domain: "KotlinException", code: 0, userInfo: ["KotlinException": cause])

        XCTAssertEqual(OnboardingErrors.messageKey(for: wrapped), "error_offline")
    }

    func testAnythingElseIsTheGenericMessage() {
        XCTAssertEqual(OnboardingErrors.messageKey(for: TestFailure()), "error_generic")
    }

    /// Every key this table can produce has to exist in all three files — `LocalizationTests`
    /// checks the files against each other, and this checks the table against the files.
    func testEveryKeyTheTableCanProduceIsLocalised() {
        let keys = [
            "error_generic",
            "error_offline",
            "error_image_too_large",
            "error_otp_invalid",
            "error_otp_expired",
            "error_otp_locked",
            "error_otp_rate_limited",
            "error_device_mismatch",
            "error_user_blocked",
            "error_validation_failed",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no English value")
        }
    }
}

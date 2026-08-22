import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-004 — the first profile.
@MainActor
final class ProfileSetupModelTests: XCTestCase {

    private var profiles: FakePassengerProfileRepository!
    private var preferences: FakeAppPreferences!

    override func setUp() {
        super.setUp()
        profiles = FakePassengerProfileRepository()
        preferences = FakeAppPreferences()
        PassengerLocale.apply(nil)
    }

    override func tearDown() {
        PassengerLocale.apply(nil)
        super.tearDown()
    }

    private func model() -> ProfileSetupModel {
        ProfileSetupModel(profiles: profiles, preferences: preferences)
    }

    func testItPreFillsFromTheProfileTheServerHolds() async {
        profiles.profile = Fixtures.profile(firstName: "Ramith de Silva", language: Language.ta)
        let model = model()

        await model.load()

        XCTAssertEqual(model.state.name, "Ramith de Silva")
        XCTAssertEqual(model.state.language, Language.ta, "the account wins over the local preference")
        XCTAssertTrue(model.state.isLoaded)
    }

    /// **A failure still marks the form loaded**: an empty form a passenger can fill in is better
    /// than a spinner they cannot leave, and the save is a `PUT` that overwrites whatever is there.
    func testAFailedReadStillOpensTheForm() async {
        profiles.profile = nil
        let model = model()

        await model.load()

        XCTAssertTrue(model.state.isLoaded)
        XCTAssertEqual(model.state.name, "")
        XCTAssertFalse(model.state.canSubmit, "there is still no name")
    }

    /// US-10.7 is **opt-out**: an absent key reads as on, so a profile nobody has touched
    /// round-trips to enabled rather than to a mute.
    func testTheNotificationSwitchDefaultsOnWhenTheKeyIsAbsent() async {
        profiles.profile = Fixtures.profile(notifPrefs: nil)
        let model = model()

        await model.load()

        XCTAssertTrue(model.state.notificationsEnabled)
    }

    /// Writing the key explicitly is what makes turning it **off** stick.
    func testAnExplicitFalseIsRead() async {
        profiles.profile = Fixtures.profile(notifPrefs: ["MARKETING": false])
        let model = model()

        await model.load()

        XCTAssertFalse(model.state.notificationsEnabled)
    }

    func testTheNameIsTheOneRequiredField() async {
        // The shared fixture ships a name and this screen is the FIRST profile, where there is not
        // one yet — so the precondition is set rather than assumed.
        profiles.profile = Fixtures.profile(firstName: nil)
        let model = model()
        await model.load()

        XCTAssertFalse(model.state.canSubmit)

        model.onNameChanged("   ")
        XCTAssertFalse(model.state.canSubmit, "spaces alone are not a name")

        model.onNameChanged("Ramith")
        XCTAssertTrue(model.state.canSubmit)
    }

    /// **Everything the screen owns goes in one call.** `UpdateProfileRequest` is all-optional, so a
    /// partial save is expressible and would leave somebody who lost signal halfway with a name and
    /// no language.
    func testSaveSendsEveryFieldInOneCall() async {
        let model = model()
        await model.load()
        model.onNameChanged("Ramith de Silva")
        model.onLanguageChanged(Language.ta)
        model.onNotificationsChanged(false)

        await model.submit()

        XCTAssertEqual(profiles.savedNames, ["Ramith de Silva"])
        XCTAssertEqual(profiles.savedLanguages, [Language.ta])
        XCTAssertEqual(profiles.savedNotifications, [false])
        XCTAssertTrue(model.isSaved)
    }

    /// The chosen language is written to the **device** as well as sent, and nothing is left pending
    /// for the next authenticated pass — it is already on the server.
    func testSaveWritesTheLanguageLocallyAndClearsThePendingFlag() async {
        preferences.languagePendingSync = true
        let model = model()
        await model.load()
        model.onNameChanged("Ramith")
        model.onLanguageChanged(Language.en)

        await model.submit()

        XCTAssertEqual(preferences.language, Language.en)
        XCTAssertFalse(preferences.languagePendingSync)
    }

    /// **The Language row belongs on this screen** and not on Edit Profile: AL-26 removes it from
    /// SCR-PI-027b, and the repository makes that structural — `update` has no language parameter at
    /// all, so no screen reached from Settings can send one however it is written.
    func testTheSettingsWriteCannotCarryALanguage() async {
        // A compile-time fact rather than a runtime one: `update(name:notifPrefs:)` is the whole
        // signature. Asserting it here is what makes the fence visible in the suite as well as in
        // the type.
        _ = try? await profiles.update(name: "Ramith", notifPrefs: nil)
        XCTAssertTrue(profiles.savedLanguages.isEmpty, "the Settings write must not touch a language")
    }

    func testAFailedSaveShowsCopyAndDoesNotNavigate() async {
        profiles.saveFailure = FakeError.unreachable
        let model = model()
        await model.load()
        model.onNameChanged("Ramith")

        await model.submit()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.isSaved)
    }

    func testDismissingTheAlertClearsTheError() async {
        profiles.saveFailure = FakeError.unreachable
        let model = model()
        await model.load()
        model.onNameChanged("Ramith")
        await model.submit()

        model.dismissError()

        XCTAssertNil(model.state.errorKey)
    }

    /// `.task` may run again after a scene change; re-reading would overwrite what has been typed.
    func testLoadingIsIdempotent() async {
        let model = model()
        await model.load()
        model.onNameChanged("Typed by the passenger")

        await model.load()

        XCTAssertEqual(model.state.name, "Typed by the passenger")
        XCTAssertEqual(profiles.meCount, 1)
    }
}

/// SCR-PI-005 — the location rationale.
@MainActor
final class PermissionsModelTests: XCTestCase {

    private var permissions: FakeLocationPermission!
    private var preferences: FakeAppPreferences!

    override func setUp() {
        super.setUp()
        permissions = FakeLocationPermission()
        preferences = FakeAppPreferences()
    }

    private func model() -> PermissionsModel {
        PermissionsModel(permissions: permissions, preferences: preferences)
    }

    /// **The screen gates nothing.** Both controls continue, and what is remembered is that the
    /// rationale was *shown* — never the grant, which belongs to the OS and can be revoked from
    /// Settings at any moment.
    func testBothDoorsRecordThatTheRationaleWasShown() async {
        let allowed = model()
        await allowed.primaryAction()
        XCTAssertTrue(preferences.locationRationaleAcknowledged)

        preferences.locationRationaleAcknowledged = false
        let skipped = model()
        skipped.skip()
        XCTAssertTrue(preferences.locationRationaleAcknowledged)
    }

    /// What is stored is the *rationale*, never the grant: storing the grant would send a passenger
    /// who later revoked it in Settings back through onboarding on the next cold start.
    func testARefusalStillLetsThePassengerThrough() async {
        permissions.grants = .denied
        let model = model()

        await model.primaryAction()

        XCTAssertTrue(preferences.locationRationaleAcknowledged)
        XCTAssertEqual(model.state.authorisation, .denied)
    }

    /// **The CTA changes once asking has stopped working.** After a refusal
    /// `requestWhenInUseAuthorization()` is a no-op for the life of the install, so a button that
    /// still said *"Allow location"* would silently do nothing — which is worse than no button.
    func testARefusedGrantSwapsTheCtaForSettings() async {
        permissions.authorisation = .denied
        let model = model()
        model.refresh()

        XCTAssertTrue(model.state.opensSettings)

        await model.primaryAction()

        XCTAssertEqual(permissions.settingsCount, 1)
        XCTAssertEqual(permissions.requestCount, 0, "asking again is a no-op; do not pretend otherwise")
    }

    func testAnUndeterminedGrantAsksTheSystem() async {
        permissions.authorisation = .notDetermined
        permissions.grants = .granted
        let model = model()
        model.refresh()

        XCTAssertFalse(model.state.opensSettings)

        await model.primaryAction()

        XCTAssertEqual(permissions.requestCount, 1)
        XCTAssertEqual(permissions.settingsCount, 0)
        XCTAssertTrue(model.state.isGranted)
    }

    /// Settings is where a denial is undone and the app is not running while it happens, so the
    /// screen re-reads on every return to the foreground.
    func testRefreshReReadsTheGrant() {
        let model = model()
        model.refresh()
        XCTAssertEqual(model.state.authorisation, .notDetermined)

        permissions.authorisation = .granted
        model.refresh()

        XCTAssertEqual(model.state.authorisation, .granted)
        XCTAssertFalse(model.state.opensSettings)
    }
}

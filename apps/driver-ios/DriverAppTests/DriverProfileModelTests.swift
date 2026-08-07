import MageRideShared
import XCTest

@testable import DriverApp

/// **SCR-DI-029 · driver profile** — the emergency contact, the grouped switches, the language and
/// the way out.
@MainActor
final class DriverProfileModelTests: XCTestCase {

    private var identity = FakeDriverIdentity()
    private var profiles = FakeProfileRepository()
    private var appliedLanguages: [Language] = []

    override func setUp() {
        super.setUp()
        identity = FakeDriverIdentity()
        profiles = FakeProfileRepository()
        appliedLanguages = []
    }

    /// ``DriverLocale/apply(_:)`` swaps the app bundle's class and writes `AppleLanguages` — process-wide
    /// effects a test must not leave behind, which is why the model takes it as a parameter.
    private func makeModel() -> DriverProfileModel {
        DriverProfileModel(
            identity: identity,
            profiles: profiles,
            applyLanguage: { [weak self] language in self?.appliedLanguages.append(language) }
        )
    }

    // MARK: - The identity card

    /// The star average has no read on the app-facing surface at all, so the card prints an em dash
    /// where the number would be — and the level, which is real, comes from C090's one repository.
    func testTheLevelIsReadAndTheStarAverageIsNot() async {
        profiles.jobStanding = jobStanding(level: 3)
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.levelText, "profile_level_value".localisedFormat(3))
        XCTAssertEqual(model.state.profile?.userId, testDriverId, "the platform id, printed verbatim")
    }

    /// **`nil` is *"reputation did not answer"***, and it must render as the em dash rather than as
    /// Level 1 — the same three-valued rule C090's job-board gate turns on.
    func testALevelThatCouldNotBeReadPrintsNothingRatherThanLevelOne() async {
        profiles.jobStanding = JobStanding()
        let model = makeModel()

        await model.refresh()

        XCTAssertNil(model.state.levelText)
    }

    // MARK: - AL-13 · the emergency contact

    /// `isPrimary` is the one denormalised onto `iam.users` for D-33's SOS fast path, so it is the one
    /// the screen is about — whatever order the server listed them in.
    func testThePrimaryContactIsTheOneShown() async {
        profiles.contacts = [
            emergencyContact(contactId: "01JCONTACT00000000000009", isPrimary: false, name: "Second"),
            emergencyContact(name: "Amma"),
        ]
        let model = makeModel()

        await model.refresh()

        XCTAssertEqual(model.state.contact?.name, "Amma")
        XCTAssertEqual(model.state.emergencyText, "Amma" + MageRideSymbols.separator + "+94770001111")
    }

    /// **Replaced, never accumulated.** A second contact would leave the SOS fast path pointing at
    /// whichever the server had already denormalised.
    func testSavingOverAnExistingContactUpdatesItInPlace() async {
        profiles.contacts = [emergencyContact()]
        let model = makeModel()
        await model.refresh()
        model.open(.emergency)
        model.onContactNameChange("Thaththa")
        model.onContactPhoneChange("0771234567")

        await model.saveEmergencyContact()

        XCTAssertEqual(profiles.savedContacts.count, 1)
        XCTAssertNotNil(profiles.savedContacts.first?.existing, "an update, not a second contact")
        XCTAssertEqual(profiles.savedContacts.first?.phone, "+94771234567", "E.164 is what safety-svc dials")
        XCTAssertNil(model.state.sheet)
    }

    func testADriverWithNoContactCreatesOne() async {
        profiles.contacts = []
        let model = makeModel()
        await model.refresh()
        model.open(.emergency)
        model.onContactNameChange("Amma")
        model.onContactPhoneChange("771234567")

        await model.saveEmergencyContact()

        XCTAssertNil(profiles.savedContacts.first?.existing)
    }

    /// The stored number is E.164 and the field takes the national digits; both spellings a driver may
    /// type reach the same nine.
    func testTheEditorSeedsTheNationalDigitsAndRefusesAnIncompleteNumber() async {
        profiles.contacts = [emergencyContact()]
        let model = makeModel()
        await model.refresh()

        model.open(.emergency)
        XCTAssertEqual(model.state.contactPhoneDraft, "770001111")

        model.onContactPhoneChange("+94 77 123 45")
        XCTAssertTrue(model.state.isContactPhoneRejected)
        XCTAssertFalse(model.state.canSaveContact)

        await model.saveEmergencyContact()
        XCTAssertTrue(profiles.savedContacts.isEmpty)
    }

    /// A contact chosen from the address book fills both fields in one tap.
    func testAPickedContactFillsBothFields() async {
        let model = makeModel()
        await model.refresh()
        model.open(.emergency)

        model.onContactPicked(name: "Amma", phone: "077-123 4567")

        XCTAssertEqual(model.state.contactNameDraft, "Amma")
        XCTAssertEqual(model.state.contactPhoneDraft, "771234567")
        XCTAssertTrue(model.state.canSaveContact)
    }

    // MARK: - US-10.7 · the switches

    /// **An absent key is on.** `iam.users.notif_prefs` starts empty and every type is enabled by
    /// default, so a fresh account must not open onto a screen claiming everything is muted.
    func testAnAbsentPreferenceReadsAsOn() async {
        profiles.storedProfile = userProfile(notifPrefs: nil)
        let model = makeModel()

        await model.refresh()

        for group in DriverNotificationGroup.allCases {
            XCTAssertTrue(group.isEnabled(in: model.state.notificationPreferences), "\(group) opened off")
        }
    }

    /// Toggling a group writes `false` for **every** key in it, which is what *"turn off wallet
    /// alerts"* means — and the whole map goes back, so a key this build has never heard of survives.
    func testTurningAGroupOffWritesEveryKeyInItAndKeepsTheUnknownOnes() async {
        profiles.storedProfile = userProfile(notifPrefs: ["SOME_FUTURE_TYPE": false])
        let model = makeModel()
        await model.refresh()

        await model.setNotificationGroup(.money, isEnabled: false)

        XCTAssertEqual(profiles.savedPreferences.count, 1)
        let sent = profiles.savedPreferences.first ?? [:]
        XCTAssertEqual(sent["SOME_FUTURE_TYPE"], false, "a key this build does not know survives a save")
        for type in DriverNotificationGroup.money.types {
            XCTAssertEqual(sent[type], false, "\(type) was not muted")
        }
        XCTAssertNil(sent["RIDE_OFFER"], "another group is not touched")
    }

    /// Nothing safety-critical is offered: iam-svc drops a mute for one on the way in and
    /// notification-svc ignores it on the way out, so a switch would silence nothing.
    func testNoGroupCanMuteASafetyCriticalTypeOrTheScheduleAlarm() {
        let offered = Set(DriverNotificationGroup.allCases.flatMap(\.types))
        let forbidden = ["SOS_TRIGGERED", "SOS_RESOLVED", "RIDE_CANCELLED", "SCHEDULE_NOT_STARTED"]

        for type in forbidden {
            XCTAssertFalse(offered.contains(type), "\(type) must not have a switch")
        }
    }

    /// There is no **sharing** group, because `NotificationCatalogue` declares no type for a Mode B
    /// access request — a switch that silenced nothing would be worse than its absence (C074 gap 6).
    func testThereIsNoSharingGroupBecauseNothingRaisesThatNotification() {
        let offered = Set(DriverNotificationGroup.allCases.flatMap(\.types))

        XCTAssertFalse(offered.contains { $0.contains("SHARE") || $0.contains("ACCESS_REQUEST") })
    }

    // MARK: - AL-26 · the language

    /// Both halves: the server's copy is what every rendered template and SMS is written in, and the
    /// device's is what redirects the app's own strings.
    func testChoosingALanguageSavesItAndAppliesItToTheAppsOwnStrings() async {
        let model = makeModel()
        await model.refresh()
        model.open(.language)

        await model.choose(language: Language.ta)

        XCTAssertEqual(profiles.savedLanguages, [Language.ta])
        XCTAssertEqual(appliedLanguages, [Language.ta])
        XCTAssertNil(model.state.sheet)
    }

    /// A language change that failed at the gateway must not silently redirect the app: the two halves
    /// answer different questions and the local one is second for a reason.
    func testALanguageSaveThatFailedDoesNotRedirectTheApp() async {
        profiles.nextFailure = apiFailure(code: "validation-failed")
        let model = makeModel()
        await model.refresh()

        await model.choose(language: Language.ta)

        XCTAssertTrue(appliedLanguages.isEmpty)
        XCTAssertEqual(model.state.errorKey, "error_validation_failed")
    }

    // MARK: - US-1.7 · the way out

    /// The screen does not navigate: `AuthSessionManager` raises `RouteToLogin` and ``DriverShellModel``
    /// is the single subscriber. A second handler would reset the stacks twice.
    func testLoggingOutEndsTheSessionAndLeavesTheNavigationToTheShell() async {
        let model = makeModel()
        await model.refresh()

        await model.logOut()

        XCTAssertEqual(profiles.logOutCount, 1)
        XCTAssertFalse(model.state.isSaving)
    }

    // MARK: - The name

    func testSavingTheNameSendsTheTrimmedValueAndClosesTheEditor() async {
        let model = makeModel()
        await model.refresh()
        model.open(.name)
        model.onNameChange("  K. Fernando  ")

        await model.saveName()

        XCTAssertEqual(profiles.savedNames, ["  K. Fernando  "], "trimming is the repository's, once")
        XCTAssertNil(model.state.sheet)
    }

    func testABlankNameCannotBeSaved() async {
        let model = makeModel()
        await model.refresh()
        model.open(.name)
        model.onNameChange("   ")

        XCTAssertFalse(model.state.canSaveName)
        await model.saveName()
        XCTAssertTrue(profiles.savedNames.isEmpty)
    }
}

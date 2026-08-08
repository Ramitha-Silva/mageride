import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-026 and SCR-PI-026a — the address book.
///
/// Every rule here is one the wireframe, AL-14 or AL-26 fixes, and each is asserted on **what the
/// seam was handed** rather than on what the screen drew: the `PUT` that has to carry the flags, the
/// geocode that must not overwrite a typed line, the shortcut that has to move off the row that held
/// it.
@MainActor
final class SavedAddressesModelTests: XCTestCase {

    private var addresses: FakeAddressBook!
    private var lastFix: LastKnownFix!
    private var keys: FakeIdempotencyKeys!

    override func setUp() {
        super.setUp()
        addresses = FakeAddressBook()
        lastFix = LastKnownFix()
        keys = FakeIdempotencyKeys()
        PassengerLocale.apply(nil)
    }

    override func tearDown() {
        PassengerLocale.apply(nil)
        super.tearDown()
    }

    private func model() -> SavedAddressesModel {
        SavedAddressesModel(addresses: addresses, lastFix: lastFix, keys: keys)
    }

    /// The pin is the last known fix, taken once — **not** a subscription. See the model's note and
    /// ``LastKnownFix``.
    func testItOpensOnTheLastKnownFixWithoutSubscribingForOne() async {
        lastFix.record(PassengerFix(lat: AddressFixtures.fix.lat, lng: AddressFixtures.fix.lng))

        let model = model()

        XCTAssertEqual(model.state.pin?.lat, AddressFixtures.fix.lat)
        XCTAssertTrue(model.state.canAdd)
    }

    /// A cold start has no fix at all, and the `＋` waits for one camera idle.
    func testTheAddControlWaitsForSomewhereToPutTheAddress() async {
        let model = model()
        XCTAssertNil(model.state.pin)
        XCTAssertFalse(model.state.canAdd)

        model.onPinMoved(AddressFixtures.fix)

        XCTAssertTrue(model.state.canAdd)
    }

    /// US-22.1: the two shortcuts are the flags, never a label convention — a Sinhala *"නිවස"* is a
    /// Home and an address a passenger labelled `"Home"` is not.
    func testHomeAndWorkAreTheFlagsAndEverythingElseIsLabelled() async {
        addresses.stored = [AddressFixtures.home(), AddressFixtures.work(), AddressFixtures.gym()]
        let model = model()

        await model.refresh()

        XCTAssertEqual(model.state.home?.addressId, AddressFixtures.homeId)
        XCTAssertEqual(model.state.work?.addressId, AddressFixtures.workId)
        XCTAssertEqual(model.state.labelled.map(\.addressId), [AddressFixtures.gymId])
    }

    /// The `＋` opens the sheet at the pin, with no shortcut and nothing in it.
    func testAddOpensAnEmptySheetAtThePin() async {
        addresses.reverse = nil
        let model = model()
        model.onPinMoved(AddressFixtures.fix)

        await model.addAddress()

        XCTAssertEqual(model.state.sheet?.lat, AddressFixtures.fix.lat)
        XCTAssertNil(model.state.sheet?.addressId)
        // Spelled out: `.none` against an `AddressShortcut?` would resolve to `Optional.none`, which
        // is a different assertion and a passing one for the wrong reason.
        XCTAssertEqual(model.state.sheet?.shortcut, AddressShortcut.none)
        XCTAssertFalse(model.state.sheet?.canSave ?? true, "an empty sheet cannot be saved")
    }

    /// **An existing shortcut is edited where it is** — its own coordinates, not wherever the map
    /// happens to be centred. Tapping ✎ on a saved Home is a request to correct it.
    func testEditingASetShortcutUsesItsOwnCoordinatesRatherThanThePin() async {
        addresses.stored = [AddressFixtures.home()]
        let model = model()
        model.onPinMoved(AddressFixtures.fix)
        await model.refresh()

        await model.editShortcut(.home)

        XCTAssertEqual(model.state.sheet?.lat, AddressFixtures.homeLat)
        XCTAssertEqual(model.state.sheet?.addressId, AddressFixtures.homeId)
        XCTAssertTrue(addresses.described.isEmpty, "an edit must not overwrite the passenger's wording")
    }

    /// An unset shortcut takes the pin — *"Home & Work via OSM pin"* — and its label is pre-filled,
    /// which is why SCR-PI-026a never has to ask *"is this your Home?"*.
    func testAnUnsetShortcutTakesThePinAndArrivesPreLabelled() async {
        let model = model()
        model.onPinMoved(AddressFixtures.fix)

        await model.editShortcut(.work)

        XCTAssertEqual(model.state.sheet?.lat, AddressFixtures.fix.lat)
        XCTAssertEqual(model.state.sheet?.shortcut, .work)
        XCTAssertEqual(model.state.sheet?.label, "addresses_work".localised)
    }

    /// AL-14: the reverse geocode fills line 1 and line **3**, because `GeocodedPlace` has a street
    /// and a city and nothing that is an area/suburb.
    func testTheReverseGeocodeFillsTheStreetAndTheCityAndNotTheSuburb() async {
        addresses.reverse = AddressFixtures.reverse()
        let model = model()
        model.onPinMoved(AddressFixtures.fix)

        await model.addAddress()

        XCTAssertEqual(model.state.sheet?.line1, "No. 42, Galle Road")
        XCTAssertEqual(model.state.sheet?.line2, "", "nothing on the wire is an area/suburb")
        XCTAssertEqual(model.state.sheet?.line3, "Colombo 03")
        XCTAssertFalse(model.state.sheet?.isLocating ?? true)
    }

    /// **A geocoder that cannot name the coordinate has not stopped anybody saving it.** `404` in
    /// the sea, `503` when Nominatim is down: the sheet opens with empty lines and no error at all.
    func testAFailedGeocodeCostsAPreFillAndNothingElse() async {
        addresses.reverse = nil
        let model = model()
        model.onPinMoved(AddressFixtures.fix)

        await model.addAddress()

        XCTAssertNotNil(model.state.sheet)
        XCTAssertEqual(model.state.sheet?.line1, "")
        XCTAssertNil(model.state.errorKey, "the lookup is a pre-fill, never a gate")
    }

    /// The two required fields are the contract's, and a row of spaces is not one of them.
    func testSaveNeedsALabelAndAFirstLineThatAreNotWhitespace() async {
        let model = model()
        model.onPinMoved(AddressFixtures.fix)
        await model.addAddress()

        model.onLine1Changed("   ")
        model.onLabelChanged("Gym")
        XCTAssertFalse(model.state.sheet?.canSave ?? true)

        model.onLine1Changed("No. 42, Galle Road")
        XCTAssertTrue(model.state.sheet?.canSave ?? false)
    }

    /// A new address is a `POST` and it carries an idempotency key (R-14/R-18); the empty lines are
    /// dropped rather than stored as blanks.
    func testANewAddressIsPostedWithAKeyAndWithoutItsEmptyLines() async {
        let model = model()
        model.onPinMoved(AddressFixtures.fix)
        await model.addAddress()
        model.onLine1Changed("No. 42, Galle Road")
        model.onLine3Changed("Colombo 03")
        model.onLabelChanged("  Gym  ")

        await model.save()

        XCTAssertEqual(addresses.added.count, 1)
        XCTAssertEqual(addresses.added.first?.label, "Gym", "the label is trimmed")
        XCTAssertNil(addresses.added.first?.line2 ?? nil, "an empty line is absent, never a stored blank")
        XCTAssertEqual(addresses.added.first?.line3 ?? nil, "Colombo 03")
        XCTAssertEqual(addresses.idempotencyKeys.compactMap { $0 }, keys.issued)
        XCTAssertNil(model.state.sheet, "the sheet closes on a successful save")
        XCTAssertEqual(model.state.addresses.map(\.addressId), [AddressFixtures.newAddressId])
    }

    /// An edit is a `PUT` on the row it opened, and **the flags travel on every save** — the
    /// contract replaces the whole row, so an omitted `isHome` would clear a Home the passenger
    /// still has.
    func testAnEditIsAFullReplacementThatCarriesTheShortcutFlags() async {
        addresses.stored = [AddressFixtures.home()]
        let model = model()
        await model.refresh()
        await model.editShortcut(.home)
        model.onLine1Changed("223 Galle Rd")

        await model.save()

        XCTAssertEqual(addresses.replaced.first?.addressId, AddressFixtures.homeId)
        XCTAssertTrue(addresses.added.isEmpty, "an edit is never a second row")
        let input = addresses.replaced.first?.input
        XCTAssertNotNil(input)
        XCTAssertEqual(input.map { IosSavedAddressKt.savedAddressInputIsHome(input: $0) }, true)
        XCTAssertEqual(input.map { IosSavedAddressKt.savedAddressInputIsWork(input: $0) }, false)
    }

    /// **A saved Home clears the flag off whichever row held it**, locally, because that is what the
    /// server just did — and two Home rows on screen until a refetch lands is worse than the round
    /// trip it saves.
    ///
    /// Asserted on ``SavedAddressesModel/merge(_:into:)`` directly rather than through the screen:
    /// with the shortcut rows as the only door, a *promotion* needs a list that is already stale
    /// (the flag moved on a second handset), which is exactly the case this rule is defensive
    /// against and exactly the one a screen-level test cannot stage honestly.
    func testMergeClearsTheShortcutOffTheRowThatHeldIt() {
        let existing = [AddressFixtures.home(), AddressFixtures.gym()]
        let promoted = AddressFixtures.address(addressId: AddressFixtures.gymId, label: "Home", isHome: true)

        let merged = SavedAddressesModel.merge(promoted, into: existing)

        XCTAssertEqual(
            merged.filter { AddressShortcut.of($0) == .home }.map(\.addressId),
            [AddressFixtures.gymId],
            "exactly one Home survives"
        )
        let old = merged.first { $0.addressId == AddressFixtures.homeId }
        XCTAssertNotNil(old)
        XCTAssertEqual(old.map { IosSavedAddressKt.savedAddressIsHome(address: $0) }, false)
        XCTAssertEqual(old?.line1, "221 Galle Rd", "the row keeps its own address; only the flag moved")
    }

    /// An edit keeps its place in a list the server ordered; a new address joins the end.
    func testMergeKeepsAnEditInPlaceAndPutsANewRowLast() {
        let existing = [AddressFixtures.home(), AddressFixtures.work(), AddressFixtures.gym()]

        let edited = AddressFixtures.address(
            addressId: AddressFixtures.workId,
            label: "Work",
            line1: "WTC East",
            isWork: true
        )
        XCTAssertEqual(
            SavedAddressesModel.merge(edited, into: existing).map(\.addressId),
            [AddressFixtures.homeId, AddressFixtures.workId, AddressFixtures.gymId]
        )

        let fresh = AddressFixtures.address(addressId: AddressFixtures.newAddressId)
        XCTAssertEqual(
            SavedAddressesModel.merge(fresh, into: existing).map(\.addressId),
            [AddressFixtures.homeId, AddressFixtures.workId, AddressFixtures.gymId, AddressFixtures.newAddressId]
        )
    }

    /// US-22.3's delete is reached through the sheet, because SCR-PI-026 draws no ✕.
    func testDeleteRemovesTheRowTheSheetWasEditing() async {
        addresses.stored = [AddressFixtures.home(), AddressFixtures.gym()]
        let model = model()
        await model.refresh()
        await model.edit(AddressFixtures.gym())

        await model.delete()

        XCTAssertEqual(addresses.removed, [AddressFixtures.gymId])
        XCTAssertEqual(model.state.addresses.map(\.addressId), [AddressFixtures.homeId])
        XCTAssertNil(model.state.busyWith)
        XCTAssertNil(model.state.sheet)
    }

    /// A new address has nothing to delete, and the sheet must not offer it.
    func testANewSheetOffersNoDelete() async {
        let model = model()
        model.onPinMoved(AddressFixtures.fix)
        await model.addAddress()

        XCTAssertFalse(model.state.sheet?.isEditing ?? true)
    }

    /// **A failed save keeps the form the passenger filled in**, and takes the spinner off the CTA
    /// so they can try again.
    ///
    /// The failure is a bare Swift error rather than a `409`: a `MageRideError` cannot be
    /// constructed from Swift without the Kotlin initialiser (the C095 finding), so the *wiring* is
    /// asserted here and the *table* in ``SettingsErrorsTests``.
    func testAFailedSaveKeepsTheSheetAndClearsTheSpinner() async {
        let model = model()
        model.onPinMoved(AddressFixtures.fix)
        await model.addAddress()
        model.onLine1Changed("No. 42, Galle Road")
        model.onLabelChanged("Gym")
        addresses.failWith = FakeError.unreachable

        await model.save()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertNotNil(model.state.sheet, "a failed save keeps the form the passenger filled in")
        XCTAssertFalse(model.state.sheet?.isSaving ?? true)
        XCTAssertTrue(model.state.addresses.isEmpty, "nothing was stored")
    }

    /// A failed list leaves the screen usable rather than spinning for ever.
    func testAFailedListStopsLoadingAndSaysSo() async {
        addresses.failWith = FakeError.unreachable
        let model = model()

        await model.refresh()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isLoading)
    }

    /// A failed delete puts the row back — a passenger whose delete did not land still has the
    /// address, and hiding it would mean they could not try again.
    func testAFailedDeleteLeavesTheRowWhereItWas() async {
        addresses.stored = [AddressFixtures.home(), AddressFixtures.gym()]
        let model = model()
        await model.refresh()
        await model.edit(AddressFixtures.gym())
        addresses.failWith = FakeError.unreachable

        await model.delete()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertEqual(model.state.addresses.count, 2)
        XCTAssertNil(model.state.busyWith)
    }
}

/// SCR-PI-027 — profile, language, the default rail, notifications and the two PDPA-shaped actions.
@MainActor
final class SettingsModelTests: XCTestCase {

    private var profiles: FakePassengerProfileRepository!
    private var preferences: FakeAppPreferences!
    private var sessions: FakePassengerSessions!
    private var identity: PassengerIdentity!

    override func setUp() {
        super.setUp()
        profiles = FakePassengerProfileRepository()
        preferences = FakeAppPreferences()
        sessions = FakePassengerSessions()
        identity = PassengerIdentity(profiles: profiles)
        PassengerLocale.apply(nil)
    }

    /// ``PassengerLocale`` is process-wide, and this suite changes it. Resetting is not optional.
    override func tearDown() {
        PassengerLocale.apply(nil)
        super.tearDown()
    }

    private func model() -> SettingsModel {
        SettingsModel(profiles: profiles, identity: identity, preferences: preferences, sessions: sessions)
    }

    /// The profile read fills the card **and** hands the result to the identity holder, which is
    /// what stops SCR-PI-033 making a second `GET /v1/users/me`.
    func testLoadingTheProfileAlsoFillsTheMenuCard() async {
        let model = model()

        await model.refresh()

        XCTAssertEqual(model.state.profile?.userId, Fixtures.passengerId)
        XCTAssertEqual(identity.profile?.userId, Fixtures.passengerId)
        XCTAssertEqual(profiles.meCount, 1)
    }

    /// **A failed read still renders the rows the device can answer.** The language it is drawing in
    /// and the rail it will book with are both local facts.
    func testAFailedReadStillDrawsTheLocalRows() async {
        profiles.profile = nil
        preferences.language = Language.ta
        preferences.rememberRail(PaymentMethod.wallet)
        let model = model()

        await model.refresh()

        XCTAssertEqual(model.state.language, Language.ta)
        XCTAssertEqual(model.state.defaultPayment, PaymentMethod.wallet)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }

    /// AL-26: **the device is written first and the app changes language whether or not the server
    /// hears about it.** `languagePendingSync` is what carries the unsent half.
    func testALanguageChangeWritesTheDeviceBeforeTheServer() async {
        profiles.writeFailure = FakeError.unreachable
        let model = model()

        await model.chooseLanguage(Language.ta)

        XCTAssertEqual(preferences.language, Language.ta)
        XCTAssertTrue(preferences.languagePendingSync, "left set for the next authenticated pass")
        XCTAssertEqual(PassengerLocale.current, Language.ta, "the bundle is re-pointed regardless")
        XCTAssertEqual(model.state.language, Language.ta)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }

    /// A successful write clears the pending flag, and **nothing is re-created** — the Δ from
    /// Android, where the same change calls `Activity.recreate()`.
    func testASuccessfulLanguageWriteClearsThePendingFlag() async {
        let model = model()

        await model.chooseLanguage(Language.en)

        XCTAssertEqual(profiles.pushedLanguages, [Language.en])
        XCTAssertFalse(preferences.languagePendingSync)
        XCTAssertNil(model.state.errorKey)
    }

    /// Choosing the language already in force is free — the picker closes and nothing is written.
    func testChoosingTheSameLanguageWritesNothing() async {
        preferences.language = Language.si
        let model = model()
        model.openPicker(.language)

        await model.chooseLanguage(Language.si)

        XCTAssertTrue(profiles.pushedLanguages.isEmpty)
        XCTAssertNil(model.state.picker)
    }

    /// US-22.4 / AL-57: **the device always remembers, and the account is told only when the
    /// contract can express the choice.** `wallet` has no value in `DefaultPaymentMethod`.
    func testAWalletDefaultIsHonouredLocallyAndNotSent() async {
        let model = model()

        await model.chooseDefaultPayment(PaymentMethod.wallet)

        XCTAssertEqual(preferences.preferredRail, PaymentMethod.wallet)
        XCTAssertEqual(model.state.defaultPayment, PaymentMethod.wallet)
        XCTAssertTrue(profiles.savedDefaultPayments.isEmpty, "the enum predates AL-57")
    }

    func testACashDefaultIsWrittenToTheAccountAsWell() async {
        let model = model()

        await model.chooseDefaultPayment(PaymentMethod.cash)

        XCTAssertEqual(profiles.savedDefaultPayments, [DefaultPaymentMethod.cash])
    }

    /// US-22.6: the account's stored rail seeds a handset that has none, and **loses to one that
    /// has** — a passenger who changed it here last week must not have it reverted by a read.
    func testTheStoredRailSeedsAFreshHandsetAndLosesToALocalChoice() async {
        profiles.profile = Fixtures.profile()
        preferences.rememberRail(PaymentMethod.wallet)
        let model = model()

        await model.refresh()

        XCTAssertEqual(model.state.defaultPayment, PaymentMethod.wallet)
    }

    /// US-10.7: the switch flips first and is **put back** if the call fails.
    func testAFailedNotificationToggleIsPutBack() async {
        let model = model()
        await model.refresh()
        profiles.writeFailure = FakeError.unreachable

        await model.setNotifications(false)

        XCTAssertTrue(model.state.notificationsEnabled, "the account did not change, so nor does the row")
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }

    /// The **whole** `notif_prefs` map goes back, keys this build has never heard of included — a
    /// caller that sent only what it knows would re-enable a type a newer build had muted.
    func testTheNotificationWriteCarriesKeysThisBuildDoesNotKnow() async {
        profiles.profile = Fixtures.profile(notifPrefs: ["MARKETING": true, "SOMETHING_NEWER": false])
        let model = model()
        await model.refresh()

        await model.setNotifications(false)

        let sent = profiles.updatedNotifPrefs.last ?? nil
        XCTAssertEqual(sent?["MARKETING"], false)
        XCTAssertEqual(sent?["SOMETHING_NEWER"], false, "an unknown key survives the round trip")
    }

    /// *Log out* clears the card and ends the session; **it does not navigate** — C014's
    /// `RouteToLogin` has one subscriber and it is the shell.
    func testLogOutClearsTheCardAndEndsTheSessionWithoutNavigating() async {
        let model = model()
        await model.refresh()
        XCTAssertNotNil(identity.profile)

        await model.logOut()

        XCTAssertNil(identity.profile)
        XCTAssertEqual(sessions.logOutCount, 1)
    }

    /// PDPA / E-06: the `202` is **accepted, not done**, and the session is deliberately left alone
    /// — signing the passenger out would claim an erasure that has not happened.
    func testDeletingTheAccountReportsARequestAndKeepsTheSession() async {
        let model = model()
        model.confirmDelete()
        XCTAssertTrue(model.state.isConfirmingDelete)

        await model.deleteAccount()

        XCTAssertEqual(profiles.deletionCount, 1)
        XCTAssertEqual(model.state.deletionRequestId, Fixtures.deletionRequestId)
        XCTAssertEqual(sessions.logOutCount, 0, "an erasure request is not a logout")
        XCTAssertFalse(model.state.isConfirmingDelete)
    }

    /// A refused erasure reports nothing as requested — the acknowledgement row must never appear
    /// for a call that failed.
    ///
    /// The `409`'s own sentence is ``SettingsErrors/deletionMessageKey(for:)``'s and is asserted in
    /// ``SettingsErrorsTests``: a `MageRideError` cannot be constructed from Swift (the C095
    /// finding), so the two halves are checked separately.
    func testARefusedErasureIsNotReportedAsRequested() async {
        profiles.writeFailure = FakeError.unreachable
        let model = model()

        await model.deleteAccount()

        XCTAssertNil(model.state.deletionRequestId)
        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertFalse(model.state.isBusy)
    }
}

/// SCR-PI-027b — the name, the switch and the SOS list.
@MainActor
final class EditProfileModelTests: XCTestCase {

    private var profiles: FakePassengerProfileRepository!
    private var contacts: FakeSosContacts!
    private var identity: PassengerIdentity!
    private var keys: FakeIdempotencyKeys!

    override func setUp() {
        super.setUp()
        profiles = FakePassengerProfileRepository()
        contacts = FakeSosContacts()
        identity = PassengerIdentity(profiles: profiles)
        keys = FakeIdempotencyKeys()
        PassengerLocale.apply(nil)
    }

    override func tearDown() {
        PassengerLocale.apply(nil)
        super.tearDown()
    }

    private func model() -> EditProfileModel {
        EditProfileModel(profiles: profiles, contacts: contacts, identity: identity, keys: keys)
    }

    func testItLoadsTheProfileAndTheContactList() async {
        contacts.stored = [AddressFixtures.contact(contactId: AddressFixtures.ammaId)]
        let model = model()

        await model.load()

        XCTAssertEqual(model.state.name, "Ramith de Silva")
        XCTAssertEqual(model.state.contacts.map(\.contactId), [AddressFixtures.ammaId])
        XCTAssertTrue(model.state.isLoaded)
    }

    /// **The contact list is allowed to fail on its own.** An iam-svc hiccup there should cost the
    /// passenger the SOS section, not the ability to fix their name.
    func testAFailedContactReadStillLeavesAUsableForm() async {
        contacts.listFailure = FakeError.unreachable
        let model = model()

        await model.load()

        XCTAssertTrue(model.state.isLoaded)
        XCTAssertEqual(model.state.name, "Ramith de Silva")
        XCTAssertTrue(model.state.contacts.isEmpty)
        XCTAssertNil(model.state.errorKey, "the profile read succeeded")
    }

    /// **Two reads, not `GET /v1/me/bootstrap`** (AL-14) — the eager-fetch payload carries the whole
    /// RBAC matrix and any trip in flight, which is not what editing a name should cost.
    func testItDoesNotReachForTheBootstrapPayload() async {
        let model = model()

        await model.load()

        XCTAssertEqual(profiles.meCount, 1)
        XCTAssertEqual(contacts.listCount, 1)
    }

    /// AL-26 made structural: the save has no language parameter, so this screen sends the name and
    /// the switch and nothing else.
    func testSaveSendsTheNameAndTheSwitchAndNoLanguage() async {
        let model = model()
        await model.load()
        model.onNameChanged("Ramith P. de Silva")
        model.onNotificationsChanged(false)

        await model.save()

        XCTAssertEqual(profiles.updatedNames.last ?? nil, "Ramith P. de Silva")
        XCTAssertEqual((profiles.updatedNotifPrefs.last ?? nil)?["MARKETING"], false)
        XCTAssertTrue(profiles.pushedLanguages.isEmpty, "there is no language on this screen")
        XCTAssertTrue(model.state.isSaved)
    }

    /// A saved name reaches SCR-PI-033's card without a second read.
    func testASavedNameReachesTheMenuCard() async {
        let model = model()
        await model.load()
        model.onNameChanged("Nimal")

        await model.save()

        XCTAssertEqual(identity.profile?.firstName, "Nimal")
    }

    /// A blank name is not a name; the bar's *Save* stays dead.
    func testSaveNeedsANameThatIsNotWhitespace() async {
        let model = model()
        await model.load()

        model.onNameChanged("   ")
        XCTAssertFalse(model.state.canSave)

        model.onNameChanged("Nimal")
        XCTAssertTrue(model.state.canSave)
    }

    /// A failed save does not report success — the screen must not pop on a `PUT` that never landed.
    func testAFailedSaveDoesNotPopTheScreen() async {
        let model = model()
        await model.load()
        profiles.writeFailure = FakeError.unreachable

        await model.save()

        XCTAssertFalse(model.state.isSaved)
        XCTAssertEqual(model.state.errorKey, "error_generic")
    }

    /// The number is typed nationally and sent as E.164 — the same normalisation SCR-PI-003 makes,
    /// so `0771234567` and `771234567` are the same contact.
    func testAContactIsSentAsE164WhateverWayItWasTyped() async {
        let model = model()
        await model.load()
        model.addContact()
        model.onContactNameChanged("Amma")
        model.onContactPhoneChanged("0770001111")

        await model.saveContact()

        XCTAssertEqual(contacts.added.first?.phone, "+94770001111")
        XCTAssertEqual(contacts.idempotencyKeys.compactMap { $0 }, keys.issued)
        XCTAssertNil(model.state.contactDraft, "the sheet closes on a successful save")
    }

    /// An incomplete number cannot be saved — nine digits starting with a 7 (D5' §14.1).
    func testAnIncompleteNumberCannotBeSaved() async {
        let model = model()
        await model.load()
        model.addContact()
        model.onContactNameChanged("Amma")

        model.onContactPhoneChanged("77000")
        XCTAssertFalse(model.state.contactDraft?.canSave ?? true)

        model.onContactPhoneChanged("770001111")
        XCTAssertTrue(model.state.contactDraft?.canSave ?? false)
    }

    /// An existing contact opens on the **national** form, because that is what the field holds.
    func testEditingAContactStripsTheCountryCodeBackOff() async {
        contacts.stored = [AddressFixtures.contact(contactId: AddressFixtures.ammaId)]
        let model = model()
        await model.load()

        model.editContact(model.state.contacts[0])

        XCTAssertEqual(model.state.contactDraft?.phone, "770001111")
        XCTAssertTrue(model.state.contactDraft?.isEditing ?? false)
    }

    /// **A delete re-reads**, because iam-svc promotes the next contact onto
    /// `iam.users.emergency_contact_name/phone` and which one that is is the server's answer.
    func testDeletingAContactReReadsTheListSoIsPrimaryIsNotALie() async {
        contacts.stored = [
            AddressFixtures.contact(contactId: AddressFixtures.ammaId, name: "Amma", isPrimary: true),
            AddressFixtures.contact(
                contactId: AddressFixtures.thathaId,
                name: "Thatha",
                phone: "+94770002222",
                isPrimary: false
            ),
        ]
        let model = model()
        await model.load()
        model.editContact(model.state.contacts[0])

        await model.removeContact()

        XCTAssertEqual(contacts.removed, [AddressFixtures.ammaId])
        XCTAssertEqual(contacts.listCount, 2, "the list is re-read rather than edited in place")
        XCTAssertEqual(model.state.contacts.map(\.contactId), [AddressFixtures.thathaId])
        XCTAssertTrue(model.state.contacts[0].isPrimary, "the server promoted the survivor")
    }

    /// Nothing in this app sets `isPrimary` — `EmergencyContactInput` has no field for it, and a
    /// client that chose would be choosing something the server owns.
    func testNothingHereChoosesThePrimaryContact() async {
        let model = model()
        await model.load()
        model.addContact()
        model.onContactNameChanged("Amma")
        model.onContactPhoneChanged("770001111")

        await model.saveContact()

        // The seam takes a name and a number and there is no third parameter to pass — asserted on
        // the recorded call, which is the only shape the protocol admits.
        XCTAssertEqual(contacts.added.map(\.name), ["Amma"])
    }

    /// A refused contact save keeps what was typed and takes the spinner off.
    ///
    /// A bare Swift error rather than iam-svc's `422 invalid-phone`, for the reason the address
    /// suite gives: a `MageRideError` cannot be constructed from Swift (the C095 finding), so the
    /// code table is asserted in ``SettingsErrorsTests``.
    func testARefusedContactSaveKeepsWhatWasTyped() async {
        let model = model()
        await model.load()
        model.addContact()
        model.onContactNameChanged("Amma")
        model.onContactPhoneChanged("770001111")
        contacts.failWith = FakeError.unreachable

        await model.saveContact()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertNotNil(model.state.contactDraft, "a failed save keeps what was typed")
        XCTAssertFalse(model.state.contactDraft?.isSaving ?? true)
    }

    /// An edit keeps its place in the list; a new contact joins the end.
    func testMergeKeepsAnEditInPlaceAndPutsANewContactLast() {
        let existing = [
            AddressFixtures.contact(contactId: AddressFixtures.ammaId),
            AddressFixtures.contact(contactId: AddressFixtures.thathaId, name: "Thatha", isPrimary: false),
        ]

        let renamed = AddressFixtures.contact(contactId: AddressFixtures.ammaId, name: "Ammi")
        XCTAssertEqual(
            EditProfileModel.merge(renamed, into: existing).map(\.name),
            ["Ammi", "Thatha"]
        )

        let fresh = AddressFixtures.contact(contactId: AddressFixtures.newContactId, name: "Akka", isPrimary: false)
        XCTAssertEqual(
            EditProfileModel.merge(fresh, into: existing).map(\.contactId),
            [AddressFixtures.ammaId, AddressFixtures.thathaId, AddressFixtures.newContactId]
        )
    }
}

/// D-26 — a failure is copy this app resolved from a kebab code, never a `ProblemDetails` string.
final class SettingsErrorsTests: XCTestCase {

    /// **Every code this cluster's eleven operations declare has copy**, and every one of them is a
    /// key the three `.strings` files carry — `LocalizationTests` is what checks the second half.
    ///
    /// A `MageRideError` cannot be constructed from Swift without the Kotlin initialiser (the C095
    /// finding), so what is asserted here is the *table*: the keys it can produce, against the ones
    /// declared. The wiring from a thrown error to it is covered by the model suites above.
    func testTheErrorTableCoversTheCodesThisClusterCanSee() {
        let keys = [
            "error_address_shortcut_taken", "error_address_not_found", "error_phone_invalid",
            "error_validation_failed", "error_dependency_unavailable", "error_offline",
            "error_generic", "settings_delete_already_requested",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no copy in the bundle")
        }
    }

    /// **One code, two meanings, and they are not interchangeable.** A `409` on a saved address is
    /// *"you already have a Home"*; a `409` on the erasure request is *"one is already open"*
    /// (E-06). The delete path therefore has its own function rather than the table guessing.
    func testTheDeletionPathHasItsOwnSentenceForAConflict() {
        XCTAssertNotEqual(
            "error_address_shortcut_taken".localised,
            "settings_delete_already_requested".localised
        )
        // Anything with no `MageRideError` behind it falls through to the shared table, which is
        // what makes the two functions agree on everything except that one code.
        XCTAssertEqual(
            SettingsErrors.deletionMessageKey(for: FakeError.unreachable),
            SettingsErrors.messageKey(for: FakeError.unreachable)
        )
    }
}

/// The cluster's two device-local rules and the identity holder — the parts no screen owns.
@MainActor
final class SettingsPreferenceTests: XCTestCase {

    private var preferences: FakeAppPreferences!

    override func setUp() {
        super.setUp()
        preferences = FakeAppPreferences()
    }

    /// Cash until the passenger says otherwise — US-22.4's own default.
    func testTheDefaultRailIsCash() {
        XCTAssertEqual(preferences.preferredRail, PaymentMethod.cash)
    }

    /// **A rail this build cannot offer reads as Cash**, not as nothing: a booking pre-selected on
    /// a retired rail would be a method SCR-PI-016 does not draw.
    func testARetiredRailReadsAsCash() {
        preferences.defaultPaymentMethod = PaymentMethod.lankaqr.wire
        XCTAssertEqual(preferences.preferredRail, PaymentMethod.cash)

        preferences.defaultPaymentMethod = "something-a-later-build-wrote"
        XCTAssertEqual(preferences.preferredRail, PaymentMethod.cash)
    }

    /// The driver QR is a **settlement** choice the contract excludes from a stored preference, so
    /// it can never be what a booking opens on however it got into the key.
    func testTheDriverQrIsNeverAStoredPreference() {
        preferences.defaultPaymentMethod = PaymentMethod.scanDriverQr.wire
        XCTAssertEqual(preferences.preferredRail, PaymentMethod.cash)
    }

    /// US-22.6: the profile seeds a handset with no answer of its own, and loses to one with.
    func testAdoptSeedsAFreshHandsetOnly() {
        preferences.adoptRail(DefaultPaymentMethod.cash)
        XCTAssertEqual(preferences.defaultPaymentMethod, PaymentMethod.cash.wire)

        preferences.rememberRail(PaymentMethod.wallet)
        preferences.adoptRail(DefaultPaymentMethod.cash)
        XCTAssertEqual(preferences.preferredRail, PaymentMethod.wallet, "the device wins after the first time")
    }

    /// The card reads once and is refreshed by whoever changed it — a second `.task` costs nothing.
    func testTheIdentityCardReadsOnceUnlessForced() async {
        let profiles = FakePassengerProfileRepository()
        let identity = PassengerIdentity(profiles: profiles)

        await identity.refresh()
        await identity.refresh()

        XCTAssertEqual(profiles.meCount, 1)
        XCTAssertEqual(identity.profile?.userId, Fixtures.passengerId)

        await identity.refresh(force: true)
        XCTAssertEqual(profiles.meCount, 2)
    }

    /// A failed read leaves the card brand-only rather than putting an error where a name goes.
    func testAFailedIdentityReadIsSwallowed() async {
        let profiles = FakePassengerProfileRepository()
        profiles.profile = nil
        let identity = PassengerIdentity(profiles: profiles)

        await identity.refresh()

        XCTAssertNil(identity.profile)
    }
}

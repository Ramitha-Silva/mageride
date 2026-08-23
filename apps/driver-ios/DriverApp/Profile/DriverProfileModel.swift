import Foundation
import MageRideShared

/// Which of SCR-DI-029's three editors is open.
enum ProfileSheet: String, Identifiable {

    /// The header's ✎ — the display name.
    case name

    /// The 🆘 row's ✎ — AL-13's contact and its number.
    case emergency

    /// The language row — AL-26's three boxes, again.
    case language

    var id: String { rawValue }
}

/// SCR-DI-029's state.
///
/// - Parameters:
///   - profile: The signed-in driver, or `nil` while the read is in flight.
///   - contact: The primary emergency contact (AL-13), or `nil` when none is stored.
///   - standing: Level and points, for the `L3 ›` row.
///   - sheet: Which editor is open.
///   - nameDraft: What the name editor holds.
///   - contactNameDraft: What the emergency-contact editor holds for the name.
///   - contactPhoneDraft: The national digits — `+94` is the field's prefix, never typed.
///   - isLoading: The reads are in flight.
///   - isSaving: A write is in flight.
///   - errorKey: Resolved copy for the last failure.
struct DriverProfileState {

    var profile: UserProfile?

    /// The plate of the vehicle this handset is live for (Δ MCS-24), or `nil` when nothing is
    /// eligible — a driver who has not onboarded one, or whose only vehicle is suspended.
    var registration: String?

    /// The driver's own photograph, absolute and ready to load (Δ MCS-25).
    ///
    /// Not ``profile``'s `photoUrl`: that is `iam.users.photo_url`, which Profile Setup never
    /// writes. Bytes rather than a URL so the avatar paints off disk on the frame this screen
    /// opens (Δ MCS-27) — see ``ProfileRepository/driverPhoto(driverId:)``.
    var photo: Data?
    var contact: EmergencyContact?
    var standing = JobStanding()
    var sheet: ProfileSheet?
    var nameDraft = ""
    var contactNameDraft = ""
    var contactPhoneDraft = ""
    var isLoading = true
    var isSaving = false
    var errorKey: String?

    /// The switch map as the server holds it; empty means every type is on (US-10.7 is opt-out).
    var notificationPreferences: [String: Bool] {
        guard let stored = profile?.notifPrefs else { return [:] }
        return stored.mapValues { $0.boolValue }
    }

    /// Whether the name editor may save.
    var canSaveName: Bool {
        !isSaving && !nameDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// Whether the emergency-contact editor may save — a name and a complete Sri Lankan mobile.
    var canSaveContact: Bool {
        !isSaving
            && !contactNameDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && PhoneNumber.isValid(contactPhoneDraft)
    }

    /// Whether the phone field should be drawn in `error`.
    var isContactPhoneRejected: Bool {
        !contactPhoneDraft.isEmpty && !PhoneNumber.isValid(contactPhoneDraft)
    }

    /// The `L3` the Driver Level row prints, or `nil` while reputation has not answered.
    var levelText: String? {
        standing.standing.map { "profile_level_value".localisedFormat(Int($0.level)) }
    }

    /// *"Amma · +94 77 000 1111"*, or `nil` when no contact is stored.
    var emergencyText: String? {
        contact.map { $0.name + MageRideSymbols.separator + $0.phone }
    }
}

/// **SCR-DI-029 · driver profile** (US-18.3, AL-13, AL-26, US-10.7, US-1.5).
///
/// The wireframe's identity card and its four rows, plus the three things the C092 deliverable adds
/// that the sketch has no room for: the **language**, the **notification switches** and **log out**.
///
/// **The overall star rating is not drawn, because nothing serves one.** The wireframe prints
/// *"DRV-22011 · ★4.8 overall"* and US-18.3 asks for it. `GET /v1/drivers/{id}/level` answers a level
/// and its points; `GET /v1/drivers/{id}/stats` answers acceptance rate, no-shows and points; neither
/// carries an average, `trips.ratings` has no aggregate read, and the only place a driver's `rating`
/// appears on the whole app-facing surface is `RideDetail.driver.rating` — the number a *passenger* is
/// shown about the driver of *their* ride. C088's menu header reached the same conclusion and drew
/// nothing. This screen prints the level, which is real, and an em dash where the average would be.
/// Carried forward as a C074 spec gap.
///
/// **The id under the name is the platform id.** There is no `DRV-22011` (see ``PlatformId``), and this
/// is the screen C091's handoff named as the one a driver reads their own id off before another driver
/// types it into SCR-DI-023 — *"if that screen does not print it verbatim and copyably, credit transfer
/// has no way to be used"*, which is why the row carries a copy action.
@MainActor
final class DriverProfileModel: ObservableObject {

    @Published private(set) var state = DriverProfileState()

    private let identity: DriverIdentity
    private let profiles: ProfileRepository
    private let applyLanguage: (Language) -> Void

    /// - Parameter applyLanguage: What ``choose(language:)`` does with the answer. Defaulted to
    ///   ``DriverLocale/apply(_:)`` and injectable for the reason ``LanguageCityModel``'s is: that call
    ///   swaps the app bundle's class and writes `AppleLanguages`, process-wide effects a test must not
    ///   leave behind.
    init(
        identity: DriverIdentity,
        profiles: ProfileRepository,
        applyLanguage: @escaping (Language) -> Void = { DriverLocale.apply($0) }
    ) {
        self.identity = identity
        self.profiles = profiles
        self.applyLanguage = applyLanguage
    }

    /// Re-reads the profile, the emergency contact and the level.
    /// The §3.16 cache, drawn first (Δ MCS-27).
    ///
    /// SCR-DI-029 opened on an empty header and filled in after three reads. This is the frame in
    /// between, and on a bad connection it is most of the time the driver spends looking at it.
    func paintFromCache() async {
        guard let driverId = identity.driverId,
              let cached = await profiles.cachedProfile(driverId: driverId),
              !cached.isEmpty
        else { return }

        // A read that has already answered outranks the cache.
        state.registration = state.registration ?? cached.registration
        state.photo = state.photo ?? cached.photoBytes.map { IosBytesKt.nsDataOf(bytes: $0) as Data }
    }

    func refresh() async {
        state.isLoading = true
        state.errorKey = nil
        do {
            let profile = try await profiles.profile()
            let contacts = try await profiles.emergencyContacts()

            state.profile = profile
            // `isPrimary` is the one denormalised onto `iam.users` for D-33's SOS fast path, so it is
            // the one this screen is about. The plain first behind it covers an account that has
            // contacts but no primary flag set.
            state.contact = contacts.first(where: \.isPrimary) ?? contacts.first
            if let driverId = identity.driverId {
                state.standing = await profiles.standing(driverId: driverId)

                // Δ MCS-24 — SCR-DI-029's header names the live vehicle. Read here rather than in
                // the view because `liveVehicle()` also WRITES the choice back (D-03: it settles
                // which of several eligible vehicles is the publisher), and a call driven by a
                // redraw would run again on every frame this screen draws.
                state.registration = (try? await identity.liveVehicle().live)?.registrationNumber

                // Δ MCS-25/27. Non-throwing on its own, like the read behind it: a gateway that
                // answered the profile but not this one should cost the avatar, not the screen.
                if let driverId = identity.driverId {
                    state.photo = await profiles.driverPhoto(driverId: driverId) ?? state.photo

                    await profiles.cacheIdentity(
                        driverId: driverId,
                        name: state.profile?.firstName,
                        level: state.standing.standing?.level?.int32Value,
                        registration: state.registration)
                }
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// Opens one of the three editors, seeded from what is currently stored.
    func open(_ sheet: ProfileSheet) {
        state.nameDraft = state.profile?.firstName ?? ""
        state.contactNameDraft = state.contact?.name ?? ""
        // The stored number is E.164 and the field takes the national digits; `normalise` is what turns
        // one into the other, and it is the same function the login screen uses on every keystroke.
        state.contactPhoneDraft = PhoneNumber.normalise(state.contact?.phone ?? "")
        state.errorKey = nil
        state.sheet = sheet
    }

    /// Closes whichever editor is open, discarding the draft.
    func dismissSheet() {
        state.sheet = nil
        state.errorKey = nil
    }

    /// The name editor's field.
    func onNameChange(_ raw: String) {
        state.nameDraft = raw
        state.errorKey = nil
    }

    /// The emergency-contact editor's name field.
    func onContactNameChange(_ raw: String) {
        state.contactNameDraft = raw
        state.errorKey = nil
    }

    /// The emergency-contact editor's number field. Normalised on every keystroke.
    func onContactPhoneChange(_ raw: String) {
        state.contactPhoneDraft = PhoneNumber.normalise(raw)
        state.errorKey = nil
    }

    /// A contact chosen from the handset's address book — name and number in one go.
    func onContactPicked(name: String, phone: String) {
        state.contactNameDraft = name
        state.contactPhoneDraft = PhoneNumber.normalise(phone)
        state.errorKey = nil
    }

    /// Saves the display name (`PUT /v1/users/me`).
    func saveName() async {
        guard state.canSaveName else { return }
        let name = state.nameDraft

        await write {
            self.state.profile = try await self.profiles.saveName(name)
            self.state.sheet = nil
        }
    }

    /// Saves the emergency contact — the one SCR-DI-032's SOS sends GPS and the active trip to.
    ///
    /// A driver typing `0771234567` and one typing `771234567` both reach the same `+94771234567`
    /// through ``PhoneNumber``, which is what safety-svc hands the SMS gateway.
    func saveEmergencyContact() async {
        guard state.canSaveContact else { return }
        let existing = state.contact
        let name = state.contactNameDraft
        let phone = PhoneNumber.toE164(state.contactPhoneDraft)

        await write {
            self.state.contact = try await self.profiles.saveEmergencyContact(
                existing: existing,
                name: name,
                phone: phone
            )
            self.state.sheet = nil
        }
    }

    /// Chooses the UI language (D-26, AL-26).
    ///
    /// **Δ Section C.** Android raises a flag so the screen can `recreate()` the Activity; iOS has no
    /// `recreate()` and needs none, because ``DriverLocale`` redirects the bundle every subsequent
    /// lookup goes through — the profile rebuilds on the state change and every other screen resolves
    /// its strings in the new language the next time it is built. See that file for the half that only
    /// lands on the next launch.
    func choose(language: Language) async {
        guard !state.isSaving else { return }

        await write {
            try await self.profiles.saveLanguage(language)
            self.applyLanguage(language)
            self.state.sheet = nil
        }
    }

    /// One notification group switched on or off (US-10.7).
    func setNotificationGroup(_ group: DriverNotificationGroup, isEnabled: Bool) async {
        guard !state.isSaving else { return }
        let updated = group.applied(to: state.notificationPreferences, isEnabled: isEnabled)

        await write {
            self.state.profile = try await self.profiles.saveNotificationPreferences(updated)
        }
    }

    /// **Log out** — `POST /v1/auth/logout`.
    ///
    /// The call is best-effort inside `AuthSessionManager`, which clears the local session and raises
    /// `RouteToLogin` **whatever the gateway did**: a driver who has asked to be signed out on a handset
    /// with no signal must still end up signed out on *this device*. Nothing here navigates —
    /// ``DriverShellModel`` is the single subscriber to that event, and a screen that also reset the
    /// stacks would race it.
    func logOut() async {
        guard !state.isSaving else { return }
        state.isSaving = true
        state.errorKey = nil
        await profiles.logOut()
        state.isSaving = false
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    /// A write, with the spinner and the failure handling every one of them shares.
    private func write(_ block: () async throws -> Void) async {
        state.isSaving = true
        state.errorKey = nil
        do {
            try await block()
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isSaving = false
    }
}

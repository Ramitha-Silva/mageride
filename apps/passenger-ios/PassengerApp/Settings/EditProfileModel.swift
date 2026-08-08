import Foundation
import MageRideShared

/// The `＋ Add SOS contact` row being filled in, or an existing one being corrected.
struct SosContactDraft: Equatable {

    /// The contact being replaced, or `nil` for a new one.
    var contactId: String?

    var name = ""

    /// The **national** number — nine digits, no `+94`. ``PhoneNumber/toE164(_:)`` is applied on the
    /// way out, exactly as SCR-PI-003 does it, so a passenger can type their mother's number the way
    /// it is written on the back of her phone.
    var phone = ""

    var isSaving = false

    var canSave: Bool { !isSaving && !name.trimmed.isEmpty && PhoneNumber.isValid(phone) }

    var isEditing: Bool { contactId != nil }
}

/// SCR-PI-027b's state.
struct EditProfileState {

    var name = ""
    var notificationsEnabled = true
    var contacts: [EmergencyContact] = []
    var profile: UserProfile?

    var isLoaded = false
    var isSaving = false

    /// Flips once the profile save lands; the screen pops on it.
    var isSaved = false

    /// The add/edit sheet, or `nil` when it is closed.
    var contactDraft: SosContactDraft?

    /// The contact whose delete is in flight.
    var removing: String?

    var errorKey: String?

    /// *Save* is live once the form has loaded and the one required field has something in it.
    var canSave: Bool { isLoaded && !isSaving && !name.trimmed.isEmpty }
}

/// SCR-PI-027b — *"Edit profile"*.
///
/// The wireframe's four things: the avatar with its 📷 badge, **Full name**, the **Notifications &
/// offers** switch, and the SOS contact list over `＋ Add SOS contact`.
///
/// **There is no language control here, and there is nowhere to put one** (AL-26). The cell says
/// *"Language selection removed from this screen"* in as many words, D2' §SCR-PI-027b's older table
/// still lists it, and the change set is what settles it — so the save goes through
/// ``PassengerProfileRepository/update(name:notifPrefs:)``, which **has no language parameter**. The
/// fence is structural rather than a rule this screen has to remember.
///
/// **The contacts are saved as they are edited; the name and the switch are saved by *Save*.** The
/// two are different shapes on the wire — a contact is its own row with its own route, and the name
/// and the switch are one `PUT /v1/users/me` — and pretending otherwise would mean either a
/// `＋ Add SOS contact` that does nothing until the passenger finds Save, or four calls behind one
/// tap where three of them could not fail.
///
/// **Nothing here sets `isPrimary`.** iam-svc promotes the first contact into
/// `iam.users.emergency_contact_name/phone` for D-33's five-second SOS budget and re-promotes on a
/// delete; `EmergencyContactInput` has no field for it, and a client that tried to choose would be
/// choosing something the server owns.
@MainActor
final class EditProfileModel: ObservableObject {

    @Published private(set) var state = EditProfileState()

    private let profiles: PassengerProfileRepository
    private let contacts: SosContacts
    private let identity: PassengerIdentity
    private let keys: IdempotencyKeys

    init(
        profiles: PassengerProfileRepository,
        contacts: SosContacts,
        identity: PassengerIdentity,
        keys: IdempotencyKeys
    ) {
        self.profiles = profiles
        self.contacts = contacts
        self.identity = identity
        self.keys = keys
    }

    /// Reads the profile and the contact list.
    ///
    /// **Two calls, not `GET /v1/me/bootstrap`.** The bootstrap would answer both in one round trip
    /// (AL-14) and also carries the addresses, the payment methods, any trip in flight and the whole
    /// RBAC matrix — paying for the eager-fetch payload every time somebody edits their name is the
    /// opposite of what it is for. The driver's SCR-DI-029 made the same call.
    ///
    /// The list is allowed to fail on its own: an iam-svc hiccup on the contacts should cost the
    /// passenger the SOS section, not the ability to fix their name.
    func load() async {
        do {
            let profile = try await profiles.me()
            identity.adopt(profile)
            state.profile = profile
            state.name = profile.firstName ?? ""
            state.notificationsEnabled = PassengerNotificationPreferences.marketingEnabled(profile.notifPrefs)
            state.isLoaded = true
        } catch is CancellationError {
            return
        } catch {
            // An empty form a passenger can fill in beats a spinner they cannot leave — the same
            // call SCR-PI-004 makes, and the save is a `PUT` that overwrites whatever is there.
            state.isLoaded = true
            state.errorKey = SettingsErrors.messageKey(for: error)
        }

        await loadContacts()
    }

    func onNameChanged(_ value: String) {
        state.name = value
        state.errorKey = nil
    }

    func onNotificationsChanged(_ enabled: Bool) {
        state.notificationsEnabled = enabled
    }

    func clearError() {
        state.errorKey = nil
    }

    /// The navigation bar's *Save* — one `PUT /v1/users/me` with the two fields this screen owns.
    func save() async {
        guard state.canSave else { return }

        state.isSaving = true
        state.errorKey = nil

        do {
            let saved = try await profiles.update(
                name: state.name,
                notifPrefs: PassengerNotificationPreferences.withMarketing(
                    state.profile?.notifPrefs,
                    enabled: state.notificationsEnabled
                )
            )
            // SCR-PI-033's card and SCR-PI-027's are drawing this name; handing the result over is
            // what changes them without a second read.
            identity.adopt(saved)
            state.profile = saved
            state.isSaving = false
            state.isSaved = true
        } catch is CancellationError {
            state.isSaving = false
        } catch {
            state.isSaving = false
            state.errorKey = SettingsErrors.messageKey(for: error)
        }
    }

    /// `＋ Add SOS contact`.
    func addContact() {
        state.contactDraft = SosContactDraft()
        state.errorKey = nil
    }

    /// A contact row.
    func editContact(_ contact: EmergencyContact) {
        state.errorKey = nil
        state.contactDraft = SosContactDraft(
            contactId: contact.contactId,
            // Back to the national form the field holds. A stored contact is E.164, and `normalise`
            // drops the `+94` the same way it drops a typed one.
            name: contact.name,
            phone: PhoneNumber.normalise(contact.phone)
        )
    }

    func onContactNameChanged(_ value: String) { state.contactDraft?.name = value }

    func onContactPhoneChanged(_ value: String) { state.contactDraft?.phone = PhoneNumber.normalise(value) }

    func dismissContact() {
        state.contactDraft = nil
    }

    /// Saves the contact in the sheet — `POST` for a new one, `PUT` for one being corrected.
    func saveContact() async {
        guard let draft = state.contactDraft, draft.canSave else { return }

        state.contactDraft?.isSaving = true
        state.errorKey = nil

        let phone = PhoneNumber.toE164(draft.phone)
        let saved: EmergencyContact
        do {
            if let contactId = draft.contactId {
                saved = try await contacts.replace(contactId: contactId, name: draft.name, phone: phone)
            } else {
                saved = try await contacts.add(name: draft.name, phone: phone, idempotencyKey: keys.next())
            }
        } catch is CancellationError {
            state.contactDraft?.isSaving = false
            return
        } catch {
            state.contactDraft?.isSaving = false
            state.errorKey = SettingsErrors.messageKey(for: error)
            return
        }

        state.contactDraft = nil
        state.contacts = EditProfileModel.merge(saved, into: state.contacts)
    }

    /// Removes the contact being edited (US-12.1).
    ///
    /// **From inside the sheet, and that is the Δ from Android.** `passenger_android.html` draws a
    /// ✎ *and* a 🗑 on every contact row; `passenger_ios.html` draws the `✎` alone, so delete lives
    /// where SCR-PI-026a already puts it in this same cluster — behind the edit, where the passenger
    /// has just been shown which contact it is.
    ///
    /// Deleting the last one puts `POST /v1/sos` back to `400 no-emergency-contact`, which is why
    /// the screen says so where the list is empty rather than leaving C102's SCR-PI-029 to explain
    /// it at the moment somebody is pressing an alarm.
    func removeContact() async {
        guard let contactId = state.contactDraft?.contactId else { return }

        state.contactDraft = nil
        state.removing = contactId
        state.errorKey = nil

        do {
            try await contacts.remove(contactId: contactId)
        } catch is CancellationError {
            state.removing = nil
            return
        } catch {
            state.removing = nil
            state.errorKey = SettingsErrors.messageKey(for: error)
            return
        }

        state.contacts.removeAll { $0.contactId == contactId }
        state.removing = nil
        // **Then re-read** — the one place in this cluster that does. Deleting the primary
        // *promotes the next contact*, and which one that is is the server's answer; a client that
        // just dropped the row would keep showing a list whose `isPrimary` was a lie about where the
        // SOS SMS is going.
        await loadContacts()
    }

    // MARK: -

    private func loadContacts() async {
        guard let items = try? await contacts.list() else { return }
        state.contacts = items
    }

    /// Puts `saved` in place of the row it replaced, or on the end when it is new.
    ///
    /// `static` so a test can put the rule under a microscope without a model.
    static func merge(_ saved: EmergencyContact, into current: [EmergencyContact]) -> [EmergencyContact] {
        guard let position = current.firstIndex(where: { $0.contactId == saved.contactId }) else {
            return current + [saved]
        }
        var next = current
        next[position] = saved
        return next
    }
}

package lk.mageride.passenger.settings

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.onboarding.PassengerProfileRepository
import lk.mageride.passenger.onboarding.PhoneNumber
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.UserProfile

/**
 * The `＋ Add SOS contact` row being filled in, or an existing one being corrected.
 *
 * @property contactId The contact being replaced, or `null` for a new one.
 * @property phone The **national** number — nine digits, no `+94`. `PhoneNumber.toE164` is applied
 *   on the way out, exactly as SCR-PA-003 does it, so a passenger can type their mother's number
 *   the way it is written on the back of her phone.
 */
internal data class SosContactDraft(
    val contactId: Ulid? = null,
    val name: String = "",
    val phone: String = "",
    val saving: Boolean = false,
) {
    val canSave: Boolean get() = !saving && name.isNotBlank() && PhoneNumber.isValid(phone)
}

/**
 * SCR-PA-027b's state.
 *
 * @property saved Flips once the profile save lands; the screen navigates back on it.
 * @property contactDraft The add/edit dialog, or `null` when it is closed.
 */
internal data class EditProfileState(
    val name: String = "",
    val notificationsEnabled: Boolean = true,
    val contacts: List<EmergencyContact> = emptyList(),
    val profile: UserProfile? = null,
    val loaded: Boolean = false,
    val saving: Boolean = false,
    val saved: Boolean = false,
    val contactDraft: SosContactDraft? = null,
    val removing: Ulid? = null,
    @param:StringRes val error: Int? = null,
) {
    /** *Save* is live once the form has loaded and the one required field has something in it. */
    val canSave: Boolean get() = loaded && !saving && name.isNotBlank()
}

/**
 * SCR-PA-027b — "Edit profile".
 *
 * The wireframe's four things: the avatar with its 📷 badge, **Full name**, the **Notifications &
 * offers** switch, and the **SOS / emergency contacts** list under a `＋ Add SOS contact` button.
 *
 * **There is no language control here, and there is nowhere to put one** (AL-26). The wireframe
 * says *"Language selection removed from this screen"* in as many words, D2' §SCR-PA-027b's older
 * table still lists it, and the change set is what settles it — so the save goes through
 * [PassengerProfileRepository.update], which **has no language parameter**. The fence is structural
 * rather than a rule this screen has to remember.
 *
 * **The contacts are saved as they are edited; the name and the switch are saved by `Save`.** The
 * two are different shapes on the wire — a contact is its own row with its own route, and the name
 * and the switch are one `PUT /v1/users/me` — and pretending otherwise would mean either a
 * `＋ Add SOS contact` that does nothing until the passenger finds the Save link, or four calls
 * behind one tap where three of them could not fail.
 *
 * **Nothing here sets `isPrimary`.** iam-svc promotes the first contact into
 * `iam.users.emergency_contact_name/phone` for D-33's five-second SOS budget and re-promotes on a
 * delete; `EmergencyContactInput` has no field for it, and a client that tried to choose would be
 * choosing something the server owns.
 */
@Suppress("TooManyFunctions") // A profile form plus the SOS list's own add / edit / remove.
internal class EditProfileViewModel(
    private val profiles: PassengerProfileRepository,
    private val contacts: SosContacts,
    private val identity: PassengerIdentity,
    private val keys: IdempotencyKeyGenerator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(EditProfileState())

    val state: StateFlow<EditProfileState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch { load() }
    }

    fun onNameChanged(value: String) {
        mutableState.update { it.copy(name = value, error = null) }
    }

    fun onNotificationsChanged(enabled: Boolean) {
        mutableState.update { it.copy(notificationsEnabled = enabled) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /**
     * The app bar's `Save` — one `PUT /v1/users/me` with the two fields this screen owns.
     *
     * The whole `notif_prefs` map goes back, keys this build has never heard of included: the set
     * of notification types grows without a contract change, and sending only the key this screen
     * draws would silently re-enable a type a newer build had muted.
     */
    fun save() {
        val current = mutableState.value
        if (!current.canSave) return

        mutableState.update { it.copy(saving = true, error = null) }
        viewModelScope.launch { commit(current) }
    }

    /** `＋ Add SOS contact`. */
    fun addContact() {
        mutableState.update { it.copy(contactDraft = SosContactDraft(), error = null) }
    }

    /** The ✎ on a contact row. */
    fun editContact(contact: EmergencyContact) {
        mutableState.update {
            it.copy(
                error = null,
                contactDraft = SosContactDraft(
                    contactId = contact.contactId,
                    name = contact.name,
                    // Back to the national form the field holds. A stored contact is E.164, and
                    // `normalise` drops the `+94` the same way it drops a typed one.
                    phone = PhoneNumber.normalise(contact.phone),
                ),
            )
        }
    }

    fun onContactNameChanged(value: String) = editDraft { it.copy(name = value) }

    fun onContactPhoneChanged(value: String) = editDraft { it.copy(phone = PhoneNumber.normalise(value)) }

    fun dismissContact() {
        mutableState.update { it.copy(contactDraft = null) }
    }

    /** Saves the contact in the dialog — `POST` for a new one, `PUT` for one being corrected. */
    fun saveContact() {
        val draft = mutableState.value.contactDraft ?: return
        if (!draft.canSave) return

        editDraft { it.copy(saving = true) }
        mutableState.update { it.copy(error = null) }
        viewModelScope.launch { commitContact(draft) }
    }

    /**
     * Removes a contact (US-12.1).
     *
     * Deleting the last one puts `POST /v1/sos` back to `400 no-emergency-contact` — which is why
     * the screen says so where the list is empty, rather than leaving C084's SCR-PA-029 to explain
     * it at the moment somebody is pressing an alarm.
     */
    fun removeContact(contact: EmergencyContact) {
        mutableState.update { it.copy(removing = contact.contactId, error = null) }
        viewModelScope.launch { deleteContact(contact.contactId) }
    }

    // ------------------------------------------------------------------------------------------

    private fun editDraft(change: (SosContactDraft) -> SosContactDraft) {
        mutableState.update { current -> current.copy(contactDraft = current.contactDraft?.let(change)) }
    }

    /**
     * Reads the profile and the contact list.
     *
     * **Two calls, not `GET /v1/me/bootstrap`.** The bootstrap would answer both in one round trip
     * (AL-14) and also carries the addresses, the payment methods, any trip in flight and the whole
     * RBAC matrix — paying for the eager-fetch payload every time somebody edits their name is the
     * opposite of what it is for. The driver's SCR-DA-029 made the same call.
     *
     * The list is allowed to fail on its own: an iam-svc hiccup on the contacts should cost the
     * passenger the SOS section, not the ability to fix their name.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val profile = profiles.me()
            identity.adopt(profile)
            mutableState.update {
                it.copy(
                    profile = profile,
                    name = profile.firstName.orEmpty(),
                    notificationsEnabled = PassengerProfileRepository.marketingEnabled(profile.notifPrefs),
                    loaded = true,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            // An empty form a passenger can fill in beats a spinner they cannot leave — the same
            // call SCR-PA-004 makes, and the save is a `PUT` that overwrites whatever is there.
            mutableState.update { it.copy(loaded = true, error = SettingsErrors.messageFor(cause)) }
        }

        loadContacts()
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadContacts() {
        try {
            val items = contacts.list()
            mutableState.update { it.copy(contacts = items) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // The section renders empty. See [load].
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun commit(current: EditProfileState) {
        try {
            val saved = profiles.update(
                firstName = current.name,
                notifPrefs = PassengerProfileRepository.withMarketing(
                    existing = current.profile?.notifPrefs,
                    enabled = current.notificationsEnabled,
                ),
            )
            // SCR-PA-033's header is drawing this name; handing the result over is what changes it
            // without a second read.
            identity.adopt(saved)
            mutableState.update { it.copy(profile = saved, saving = false, saved = true) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(saving = false, error = SettingsErrors.messageFor(cause)) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun commitContact(draft: SosContactDraft) {
        val phone = PhoneNumber.toE164(draft.phone)
        try {
            val saved = if (draft.contactId == null) {
                contacts.add(name = draft.name, phone = phone, idempotencyKey = keys.next())
            } else {
                contacts.replace(contactId = draft.contactId, name = draft.name, phone = phone)
            }
            mutableState.update { current ->
                current.copy(contactDraft = null, contacts = current.contacts.merge(saved))
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            editDraft { it.copy(saving = false) }
            mutableState.update { it.copy(error = SettingsErrors.messageFor(cause)) }
        }
    }

    /**
     * Deletes, then re-reads.
     *
     * The one place in this cluster that re-reads rather than editing the list in place: deleting
     * the primary **promotes the next contact**, and which one that is is the server's answer. A
     * client that just dropped the row would keep showing a list whose `isPrimary` was a lie about
     * where the SOS SMS is going.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun deleteContact(contactId: Ulid) {
        try {
            contacts.remove(contactId)
            mutableState.update { current ->
                current.copy(removing = null, contacts = current.contacts.filterNot { it.contactId == contactId })
            }
            loadContacts()
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(removing = null, error = SettingsErrors.messageFor(cause)) }
        }
    }

    /** Puts [saved] in place of the row it replaced, or on the end when it is new. */
    private fun List<EmergencyContact>.merge(saved: EmergencyContact): List<EmergencyContact> {
        val position = indexOfFirst { it.contactId == saved.contactId }
        return if (position < 0) this + saved else take(position) + saved + drop(position + 1)
    }
}

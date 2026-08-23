package lk.mageride.driver.profile

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.jobs.JobStanding
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.driver.onboarding.PhoneNumber
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.iam.EmergencyContact
import lk.mageride.shared.data.models.iam.UserProfile

/** Which of SCR-DA-029's three editors is open. */
internal enum class ProfileSheet {

    /** The header's ✎ — the display name. */
    Name,

    /** The 🆘 row's ✎ — AL-13's contact and its number. */
    Emergency,

    /** The language row — AL-26's three boxes, again. */
    Language,
}

/**
 * SCR-DA-029's state.
 *
 * @property profile The signed-in driver, or `null` while the read is in flight.
 * @property contact The primary emergency contact (AL-13), or `null` when none is stored.
 * @property standing Level and points, for the `L3 ›` row.
 * @property sheet Which editor is open.
 * @property nameDraft What the name editor holds.
 * @property contactNameDraft What the emergency-contact editor holds for the name.
 * @property contactPhoneDraft The national digits — `+94` is the field's prefix, never typed.
 * @property loading The reads are in flight.
 * @property saving A write is in flight.
 * @property languageChanged A language save landed; the Activity has to be rebuilt to show it.
 * @property signedOut The session has ended and the shell should route to Login.
 * @property error Resolved copy for the last failure.
 */
internal data class DriverProfileState(
    val profile: UserProfile? = null,
    /**
     * The plate of the vehicle this handset is live for (Δ MCS-24), or `null` when nothing is
     * eligible — a driver who has not onboarded one, or whose only vehicle is suspended.
     */
    val registration: String? = null,
    /**
     * The driver's own photograph, absolute and ready to load (Δ MCS-25).
     *
     * Not [profile]'s `photoUrl`: that is `iam.users.photo_url`, which Profile Setup never writes.
     * See `ProfileRepository.driverPhotoUrl`.
     */
    val photoUrl: String? = null,
    val contact: EmergencyContact? = null,
    val standing: JobStanding = JobStanding(),
    val sheet: ProfileSheet? = null,
    val nameDraft: String = "",
    val contactNameDraft: String = "",
    val contactPhoneDraft: String = "",
    val loading: Boolean = true,
    val saving: Boolean = false,
    val languageChanged: Boolean = false,
    val signedOut: Boolean = false,
    @param:StringRes val error: Int? = null,
) {

    /** The switch map as the server holds it; empty means every type is on (US-10.7 is opt-out). */
    val notificationPreferences: Map<String, Boolean> get() = profile?.notifPrefs.orEmpty()

    /** Whether the name editor may save. */
    val canSaveName: Boolean get() = !saving && nameDraft.isNotBlank()

    /** Whether the emergency-contact editor may save — a name and a complete Sri Lankan mobile. */
    val canSaveContact: Boolean
        get() = !saving && contactNameDraft.isNotBlank() && PhoneNumber.isValid(contactPhoneDraft)

    /** Whether the phone field should be drawn in `error`. */
    val contactPhoneRejected: Boolean
        get() = contactPhoneDraft.isNotEmpty() && !PhoneNumber.isValid(contactPhoneDraft)
}

/**
 * **SCR-DA-029 · driver profile** (US-18.3, AL-13, AL-26, US-10.7, US-1.5).
 *
 * The wireframe's identity card and its four rows, plus the three things the C074 deliverable adds
 * that the sketch has no room for: the **language**, the **notification switches** and **log out**.
 *
 * **The overall star rating is not drawn, because nothing serves one.** The wireframe prints
 * *"DRV-22011 · ★4.8 overall"* and US-18.3 asks for it. `GET /v1/drivers/{id}/level` answers a level
 * and its points; `GET /v1/drivers/{id}/stats` answers acceptance rate, no-shows and points;
 * neither carries an average, `trips.ratings` has no aggregate read, and the only place a driver's
 * `rating` appears on the whole app-facing surface is `RideDetail.driver.rating` — the number a
 * *passenger* is shown about the driver of *their* ride. C070's menu header reached the same
 * conclusion and drew nothing. This screen prints the level, which is real, and an em dash where
 * the average would be. Recorded as a C074 spec gap.
 *
 * **The id under the name is the platform id.** There is no `DRV-22011`
 * (see [lk.mageride.driver.ui.PlatformId]), and this is the screen C073's handoff named as the one
 * a driver reads their own id off before another driver types it into SCR-DA-023.
 */
// Fifteen members, and the ceiling is eleven. This is one screen with three editors and five
// preference rows, and every way of getting under the threshold makes it worse: merging the two
// saves into one `save()` puts a `when (sheet)` between a CTA and its request, and splitting the
// editors into a second view model would put one driver's profile in two states that have to be
// kept in step. The class is long because the screen is, not because anything is tangled.
@Suppress("TooManyFunctions")
internal class DriverProfileViewModel(private val identity: DriverIdentity, private val profiles: ProfileRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(DriverProfileState())

    val state: StateFlow<DriverProfileState> = mutableState.asStateFlow()

    init {
        refresh()
        refreshPhoto()
    }

    /**
     * The avatar, on its own coroutine (Δ MCS-25).
     *
     * **Deliberately not part of [refresh].** It was, and that made a fourth network read a
     * precondition for `loading` clearing — so a gateway that answered the profile but was slow on
     * this one held the whole screen on its spinner, and every field the driver came to read waited
     * on a picture. A failure here costs the avatar and nothing else; the glyph is already the
     * right thing to draw.
     */
    private fun refreshPhoto() {
        launchGuarded(onFailure = { }) {
            val photoUrl = profiles.driverPhotoUrl()

            mutableState.update { it.copy(photoUrl = photoUrl ?: it.photoUrl) }
        }
    }

    /** Re-reads the profile, the emergency contact and the level. */
    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val profile = profiles.profile()
            val contacts = profiles.emergencyContacts()
            val driverId = identity.driverId
            // Read before the state is touched: `update` is a compare-and-set loop and re-runs its
            // lambda when it loses, which would re-fire this read.
            val standing = driverId?.let { id -> profiles.standing(id) }

            // Δ MCS-24 — SCR-DA-029's header now names the live vehicle. Read here rather than in
            // the composable because `liveVehicle()` also WRITES the choice back (D-03: it settles
            // which of several eligible vehicles is the publisher), and a suspending write driven
            // by recomposition would run again on every frame this screen redraws.
            val live = runCatching { identity.liveVehicle().live }.getOrNull()

            mutableState.update {
                it.copy(
                    loading = false,
                    profile = profile,
                    // `isPrimary` is the one denormalised onto `iam.users` for D-33's SOS fast
                    // path, so it is the one this screen is about. `firstOrNull` behind it covers
                    // an account that has contacts but no primary flag set.
                    contact = contacts.firstOrNull(EmergencyContact::isPrimary) ?: contacts.firstOrNull(),
                    standing = standing ?: it.standing,
                    registration = live?.registrationNumber ?: it.registration,
                )
            }
        }
    }

    /** Opens one of the three editors, seeded from what is currently stored. */
    fun openSheet(sheet: ProfileSheet) {
        val current = mutableState.value
        mutableState.update {
            it.copy(
                sheet = sheet,
                nameDraft = current.profile?.firstName.orEmpty(),
                contactNameDraft = current.contact?.name.orEmpty(),
                // The stored number is E.164 and the field takes the national digits; `normalise`
                // is what turns one into the other, and it is the same function the login screen
                // uses on every keystroke.
                contactPhoneDraft = PhoneNumber.normalise(current.contact?.phone.orEmpty()),
                error = null,
            )
        }
    }

    /** Closes whichever editor is open, discarding the draft. */
    fun dismissSheet() {
        mutableState.update { it.copy(sheet = null, error = null) }
    }

    /** The name editor's field. */
    fun onNameChange(raw: String) {
        mutableState.update { it.copy(nameDraft = raw, error = null) }
    }

    /** The emergency-contact editor's name field. */
    fun onContactNameChange(raw: String) {
        mutableState.update { it.copy(contactNameDraft = raw, error = null) }
    }

    /** The emergency-contact editor's number field. Normalised on every keystroke. */
    fun onContactPhoneChange(raw: String) {
        mutableState.update { it.copy(contactPhoneDraft = PhoneNumber.normalise(raw), error = null) }
    }

    /** A contact chosen from the handset's address book — name and number in one go. */
    fun onContactPicked(name: String, phone: String) {
        mutableState.update {
            it.copy(contactNameDraft = name, contactPhoneDraft = PhoneNumber.normalise(phone), error = null)
        }
    }

    /** Saves the display name (`PUT /v1/users/me`). */
    fun saveName() {
        val current = mutableState.value
        if (!current.canSaveName) return

        // The `PUT` is made before `update`, never inside it: a driver still typing in the field
        // moves the state under the compare-and-set, and a mutation in that lambda is sent twice.
        write {
            val profile = profiles.saveName(current.nameDraft)
            mutableState.update { it.copy(profile = profile, sheet = null) }
        }
    }

    /**
     * Saves the emergency contact — the one SCR-DA-032's SOS sends GPS and the active trip to.
     *
     * The number goes up in E.164 because that is what `EmergencyContactInput.phone` is and what
     * safety-svc hands the SMS gateway; a driver typing `0771234567` and one typing `771234567`
     * both reach the same `+94771234567` through `PhoneNumber`.
     */
    fun saveEmergencyContact() {
        val current = mutableState.value
        if (!current.canSaveContact) return

        write {
            val saved = profiles.saveEmergencyContact(
                existing = current.contact,
                name = current.contactNameDraft,
                phone = PhoneNumber.toE164(current.contactPhoneDraft),
            )
            mutableState.update { it.copy(contact = saved, sheet = null) }
        }
    }

    /**
     * Chooses the UI language (D-26, AL-26).
     *
     * [DriverProfileState.languageChanged] is raised only when the choice actually differs from
     * what the app is rendering in: `Activity.recreate()` on a no-op change is a screen that blinks
     * for nothing, and it would throw away an open editor.
     */
    fun chooseLanguage(language: Language) {
        if (mutableState.value.saving) return
        val changed = profiles.storedLanguage() != language

        write {
            profiles.saveLanguage(language)
            mutableState.update { it.copy(sheet = null, languageChanged = changed) }
        }
    }

    /** One notification group switched on or off (US-10.7). */
    fun setNotificationGroup(group: DriverNotificationGroup, enabled: Boolean) {
        val current = mutableState.value
        if (current.saving) return
        val updated = group.applied(current.notificationPreferences, enabled)

        write {
            val profile = profiles.saveNotificationPreferences(updated)
            mutableState.update { it.copy(profile = profile) }
        }
    }

    /**
     * **Log out** — `POST /v1/auth/logout`.
     *
     * [DriverProfileState.signedOut] is raised whatever the call did. A driver who has asked to be
     * signed out on a handset with no signal must still end up signed out on *this device*, and
     * `AuthSessionManager.logout()` clears the local session either way; leaving them on a profile
     * screen because the gateway was unreachable would be the worst possible reading of the word.
     */
    fun logOut() {
        if (mutableState.value.saving) return

        mutableState.update { it.copy(saving = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(saving = false, signedOut = true) } }) {
            profiles.logOut()
            mutableState.update { it.copy(saving = false, signedOut = true) }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /** A write, with the spinner and the failure handling every one of them shares. */
    private fun write(block: suspend () -> Unit) {
        mutableState.update { it.copy(saving = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(saving = false) } }) {
            block()
            mutableState.update { it.copy(saving = false) }
        }
    }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }
}

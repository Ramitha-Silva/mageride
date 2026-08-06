package lk.mageride.passenger.onboarding

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.Language

/**
 * SCR-PA-004's state.
 *
 * @property name The wireframe's *"Full name"*. The one required field.
 * @property language The wireframe's own Language row — see the class KDoc on why it is here.
 * @property notificationsEnabled *"Notifications & offers"*. On by default (US-10.7 is opt-out).
 * @property loaded Whether the existing profile has been read; the form is disabled until it has.
 * @property busy A save is in flight.
 * @property error Resolved copy for the last failure, or `null`.
 */
internal data class ProfileSetupState(
    val name: String = "",
    val language: Language = Language.SI,
    val notificationsEnabled: Boolean = true,
    val loaded: Boolean = false,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
) {
    /** A name with something in it. Trimmed, so spaces alone do not pass. */
    val nameValid: Boolean get() = name.trim().isNotEmpty()

    /** *"Save & continue"* is live when the one required field has a value. */
    val canSubmit: Boolean get() = !busy && loaded && nameValid
}

/**
 * SCR-PA-004 — the first profile.
 *
 * **The Language row belongs on THIS screen and not on Edit Profile.** AL-26 removes the language
 * selector from SCR-PA-027b, and the wireframe for that screen says so in as many words; what it
 * leaves is onboarding (SCR-PA-002) and Profile & settings (SCR-PA-027). SCR-PA-004 is neither —
 * it is the *first* profile, drawn once — and its own wireframe draws a Language field. Keeping it
 * is what lets a passenger who tapped the wrong box two screens ago fix it without hunting through
 * settings; the value is pre-filled from what SCR-PA-002 already stored, so leaving it alone is
 * the common path.
 *
 * **No referral and no Aadhaar** — D2' §SCR-PA-004 drops both, and there is nowhere on
 * `UpdateProfileRequest` to put them.
 */
internal class ProfileSetupViewModel(
    private val profiles: PassengerProfileRepository,
    private val preferences: AppPreferences,
) : ViewModel() {

    private val mutableState = MutableStateFlow(
        ProfileSetupState(language = preferences.language ?: Language.SI),
    )
    private val mutableSaved = MutableStateFlow(false)

    val state: StateFlow<ProfileSetupState> = mutableState.asStateFlow()

    /** Flips once the save lands; the screen navigates on it. */
    val saved: StateFlow<Boolean> = mutableSaved.asStateFlow()

    init {
        viewModelScope.launch { load() }
    }

    fun onNameChanged(input: String) {
        mutableState.update { it.copy(name = input, error = null) }
    }

    fun onLanguageChanged(language: Language) {
        mutableState.update { it.copy(language = language, error = null) }
    }

    fun onNotificationsChanged(enabled: Boolean) {
        mutableState.update { it.copy(notificationsEnabled = enabled) }
    }

    /**
     * *"Save & continue"* — one `PUT /v1/users/me` with everything the screen owns.
     *
     * The chosen language is written to the local preference **as well** as sent: `attachBaseContext`
     * reads the preference, so a passenger who changed it here would otherwise keep the old locale
     * until something else happened to write one.
     */
    fun submit() {
        val current = mutableState.value
        if (!current.canSubmit) return

        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            @Suppress("TooGenericExceptionCaught")
            try {
                profiles.save(
                    firstName = current.name,
                    language = current.language,
                    notificationsEnabled = current.notificationsEnabled,
                )
                preferences.language = current.language
                // Already on the server, so nothing is pending for the next authenticated pass.
                preferences.languagePendingSync = false
                mutableSaved.value = true
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            } finally {
                mutableState.update { it.copy(busy = false) }
            }
        }
    }

    /**
     * Pre-fills from `GET /v1/users/me`.
     *
     * A failure still marks the form [ProfileSetupState.loaded] — an empty form a passenger can
     * fill in is better than a spinner they cannot leave, and the save is a `PUT` that will
     * overwrite whatever is there regardless. The server's language wins over the local preference
     * when it has one, because the account is the source of truth for a passenger signing in on a
     * second handset.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val profile = profiles.me()
            mutableState.update {
                it.copy(
                    name = profile.firstName.orEmpty(),
                    language = profile.language ?: it.language,
                    notificationsEnabled = profile.notifPrefs
                        ?.get(PassengerProfileRepository.NOTIFICATIONS_AND_OFFERS)
                        // US-10.7 is opt-out: an absent key reads as ON.
                        ?: true,
                    loaded = true,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            mutableState.update { it.copy(loaded = true) }
        }
    }
}

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
import lk.mageride.passenger.ride.PaymentRails
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.iam.DefaultPaymentMethod
import lk.mageride.shared.data.models.iam.UserProfile
import lk.mageride.shared.domain.auth.AuthSessionManager

/** Which of SCR-PA-027's two value rows is open as a chooser. */
internal enum class SettingsPicker { LANGUAGE, PAYMENT }

/**
 * SCR-PA-027's state.
 *
 * @property profile The card at the top — name, id and phone. `null` until the read lands.
 * @property language The 🌐 row's value, and what the app is drawing in.
 * @property defaultPayment The 💳 row's value — the rail the **next** booking starts with.
 * @property relaunch Set when a language change needs the Activity re-created. See
 *   [SettingsViewModel.chooseLanguage].
 * @property deletionRequestId The PDPA erasure request `DELETE /v1/users/me` accepted, which is
 *   what the screen shows instead of claiming the account is gone (E-06).
 */
internal data class SettingsState(
    val profile: UserProfile? = null,
    val language: Language = Language.SI,
    val defaultPayment: PaymentMethod = PaymentMethod.CASH,
    val notificationsEnabled: Boolean = true,
    val loading: Boolean = true,
    val busy: Boolean = false,
    val picker: SettingsPicker? = null,
    val confirmingDelete: Boolean = false,
    val relaunch: Boolean = false,
    val deletionRequestId: String? = null,
    @param:StringRes val error: Int? = null,
)

/**
 * SCR-PA-027 — "Profile & settings".
 *
 * The wireframe's rows in order: the profile card (→ SCR-PA-027b), 🌐 **Language**, ★ **Save Home &
 * Work**, 📍 **Saved addresses**, 💳 **Default payment**, 🔔 **Notifications**, 💬 **Help &
 * support**, and the two textlinks — **Log out** and **Delete account**.
 *
 * **Language is here and on SCR-PA-002, and nowhere else** (AL-26). This is one of the two screens
 * the change set left it on; [PassengerProfileRepository.update] — what Edit Profile saves through
 * — has no language parameter at all, so the fence is structural rather than remembered.
 *
 * **A language change re-creates the Activity, and it has to.** `PassengerLocale.wrap` is applied in
 * `MainActivity.attachBaseContext`, which has already run by the time any composable exists; the
 * per-app locale API that would avoid this (`LocaleManager`) is API 33+ and the URD floor is 26. So
 * the preference is written, the server is told, and [SettingsState.relaunch] asks the screen for a
 * `recreate()`.
 *
 * **Default payment is two writes, and one of them may not be possible.** The device always
 * remembers ([PaymentPreference]); `iam.users.default_payment_method` is written only when the
 * chosen rail has a value in an enum that predates AL-57 — see `PaymentRails.storedValueOf`. A
 * wallet default is honoured here and does not follow the passenger to a second handset, which is a
 * contract gap and not a design.
 */
@Suppress("TooManyFunctions") // One method per row on the wireframe's longest settings list.
internal class SettingsViewModel(
    private val profiles: PassengerProfileRepository,
    private val identity: PassengerIdentity,
    private val payments: PaymentPreference,
    private val preferences: AppPreferences,
    private val sessions: AuthSessionManager,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SettingsState(language = preferences.language ?: Language.SI))

    val state: StateFlow<SettingsState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    fun refresh() {
        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch { load() }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    fun openPicker(picker: SettingsPicker) {
        mutableState.update { it.copy(picker = picker) }
    }

    fun dismissPicker() {
        mutableState.update { it.copy(picker = null) }
    }

    /**
     * The 🌐 row (D-26, AL-26).
     *
     * **The local write comes first and is not conditional on the call.** A passenger who chose
     * Tamil on a train with no signal asked for a Tamil app, and `attachBaseContext` is what gives
     * them one; the server copy is what every SMS and server-rendered string is written in, and it
     * can be corrected on the next visit. The two answer different questions, so a failure of one
     * is not a reason to abandon the other.
     */
    fun chooseLanguage(language: Language) {
        if (language == mutableState.value.language) {
            dismissPicker()
            return
        }

        preferences.language = language
        preferences.languagePendingSync = true
        mutableState.update { it.copy(language = language, picker = null, busy = true, error = null) }

        viewModelScope.launch { pushLanguage(language) }
    }

    /**
     * The 💳 row (US-22.4, AL-14).
     *
     * The device is told unconditionally, because that is what the next booking reads; the account
     * is told when the contract can express the choice. See the class KDoc.
     */
    fun chooseDefaultPayment(method: PaymentMethod) {
        payments.remember(method)
        mutableState.update { it.copy(defaultPayment = method, picker = null, error = null) }

        val stored = PaymentRails.storedValueOf(method) ?: return
        viewModelScope.launch { pushDefaultPayment(stored) }
    }

    /**
     * The 🔔 row (US-10.7).
     *
     * The switch flips first and is put back if the call fails: a toggle that waited for a round
     * trip would feel broken on a Sri Lankan mobile network, and one that stayed flipped after a
     * failure would be lying about what the account holds.
     */
    fun setNotifications(enabled: Boolean) {
        mutableState.update { it.copy(notificationsEnabled = enabled, busy = true, error = null) }
        viewModelScope.launch { pushNotifications(enabled) }
    }

    /** *Log out* — the same one action the drawer's row takes. */
    fun logOut() {
        identity.clear()
        // The shell's `RouteToLogin` collector is what clears the back stack — one subscriber for
        // every way a session can end, so a logout here and a revocation take the same path out.
        viewModelScope.launch { runCatching { sessions.logout() } }
    }

    /** *Delete account* — the tap that opens the dialog. Nothing has happened yet. */
    fun confirmDelete() {
        mutableState.update { it.copy(confirmingDelete = true) }
    }

    fun dismissDelete() {
        mutableState.update { it.copy(confirmingDelete = false) }
    }

    /**
     * `DELETE /v1/users/me` (US-1.8, PDPA).
     *
     * **Accepted, not done.** The `202` hands the request to pdpa-svc, where a statutory hold can
     * delay it (E-06), so the screen reports a *request* and the session is deliberately left
     * alone: signing the passenger out here would claim an erasure that has not happened and would
     * take away the only surface that can tell them when it has.
     */
    fun deleteAccount() {
        mutableState.update { it.copy(confirmingDelete = false, busy = true, error = null) }
        viewModelScope.launch { requestErasure() }
    }

    /** The screen has re-created itself for the new language. */
    fun onRelaunchConsumed() {
        mutableState.update { it.copy(relaunch = false) }
    }

    // ------------------------------------------------------------------------------------------

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        try {
            val profile = profiles.me()
            // The drawer header reads the same profile — see `PassengerIdentity`. Handing it over
            // is what stops SCR-PA-033 making a second `GET /v1/users/me` of its own.
            identity.adopt(profile)
            // US-22.6: the account's stored rail seeds a handset that has none of its own, and
            // loses to one that has.
            payments.adopt(profile.defaultPaymentMethod)

            mutableState.update {
                it.copy(
                    profile = profile,
                    language = profile.language ?: it.language,
                    defaultPayment = payments.current,
                    notificationsEnabled = PassengerProfileRepository.marketingEnabled(profile.notifPrefs),
                    loading = false,
                )
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            // The rows still render from what the device knows — the language it is drawing in and
            // the payment rail it will book with are both local facts.
            mutableState.update {
                it.copy(loading = false, defaultPayment = payments.current, error = SettingsErrors.messageFor(cause))
            }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun pushLanguage(language: Language) {
        try {
            profiles.saveLanguage(language)
            preferences.languagePendingSync = false
            mutableState.update { it.copy(busy = false, relaunch = true) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            // The app still changes language — see the KDoc. `languagePendingSync` is left set, so
            // C077's `OnboardingRepository` pushes it on the next authenticated pass.
            mutableState.update { it.copy(busy = false, relaunch = true, error = SettingsErrors.messageFor(cause)) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun pushDefaultPayment(stored: DefaultPaymentMethod) {
        try {
            profiles.saveDefaultPaymentMethod(stored)
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(error = SettingsErrors.messageFor(cause)) }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun pushNotifications(enabled: Boolean) {
        try {
            val saved = profiles.update(
                notifPrefs = PassengerProfileRepository.withMarketing(
                    existing = mutableState.value.profile?.notifPrefs,
                    enabled = enabled,
                ),
            )
            identity.adopt(saved)
            mutableState.update { it.copy(profile = saved, busy = false) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update {
                it.copy(
                    busy = false,
                    notificationsEnabled = !enabled,
                    error = SettingsErrors.messageFor(cause),
                )
            }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun requestErasure() {
        try {
            val requestId = profiles.deleteAccount()
            mutableState.update { it.copy(busy = false, deletionRequestId = requestId) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(busy = false, error = SettingsErrors.deletionMessageFor(cause)) }
        }
    }
}

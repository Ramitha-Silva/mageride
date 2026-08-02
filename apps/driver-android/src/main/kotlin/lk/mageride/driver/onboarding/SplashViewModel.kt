package lk.mageride.driver.onboarding

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/**
 * SCR-DA-001 — the boot router.
 *
 * Three questions, in order, and only as many as the answer needs:
 *
 * 1. **Has this install chosen a language and a city?** Local, instant (`SharedPreferences`).
 * 2. **Is there a session?** `AuthSessionManager.restore()` — a secure-store read, no network.
 * 3. **Does this driver have a profile?** `GET /v1/users/me`, and *only* when signed in.
 *
 * The splash is the one screen allowed to block on that, and it blocks on the smallest thing that
 * can answer the question — which is why the third is skipped entirely for a signed-out driver.
 */
internal class SplashViewModel(
    private val sessions: AuthSessionManager,
    private val profiles: DriverProfileRepository,
    private val preferences: OnboardingPreferences,
) : ViewModel() {

    private val mutableDestination = MutableStateFlow<OnboardingDestination?>(null)

    /** Where to go. `null` while the splash is still deciding — the wireframe's spinner. */
    val destination: StateFlow<OnboardingDestination?> = mutableDestination.asStateFlow()

    init {
        viewModelScope.launch { route() }
    }

    private suspend fun route() {
        sessions.restore()
        val signedIn = sessions.state.value is SessionState.SignedIn

        mutableDestination.value = OnboardingRouter.next(
            signedIn = signedIn,
            firstRunComplete = preferences.firstRunComplete,
            profileComplete = signedIn && hasProfile(),
            permissionsAcknowledged = preferences.permissionsAcknowledged,
        )
    }

    /**
     * Whether `registry.driver_profiles` already has a name for this driver (US-2.21).
     *
     * `firstName` is `iam.users`' copy of the Profile Setup name, and D1' A.1 makes "no name" the
     * entry condition for the profile screen on both apps.
     *
     * **A failed call answers `true`, and that is deliberate.** This runs on a session that was
     * restored from the secure store, so the driver has signed in before and has therefore already
     * been through Profile Setup — the screen cannot be reached without it. Answering `false` on a
     * flat tunnel would put a working driver back on an onboarding form; answering `true` lands
     * them on the dashboard, which has the offline banner and can retry everything. A genuinely
     * new account never takes this path: the login screen computes its own destination from the
     * verify it just made.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun hasProfile(): Boolean = try {
        !profiles.me().firstName.isNullOrBlank()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        true
    }
}

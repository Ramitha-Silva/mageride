package lk.mageride.passenger.onboarding

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import lk.mageride.passenger.nav.PassengerRoute
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/**
 * SCR-PA-001 — the boot router.
 *
 * Three questions, in order, and only as many as the answer needs:
 *
 * 1. **Has this install chosen a language?** Local, instant (`SharedPreferences`).
 * 2. **Is there a session?** `AuthSessionManager.restore()` — a secure-store read, no network.
 * 3. **Does this passenger have a profile, and a ride in flight?** `GET /v1/users/me` and
 *    `GET /v1/rides/passenger/{id}/active`, and *only* when signed in.
 *
 * The splash is the one screen allowed to block on that, and it blocks on the smallest thing that
 * can answer the question — which is why steps 2 and 3 are skipped entirely for a first run.
 */
internal class SplashViewModel(
    private val sessions: AuthSessionManager,
    private val profiles: PassengerProfileRepository,
    private val rides: RideApi,
    private val preferences: AppPreferences,
) : ViewModel() {

    private val mutableRoute = MutableStateFlow<PassengerRoute?>(null)

    /** Where to go. `null` while the splash is still deciding — the wireframe's spinner. */
    val route: StateFlow<PassengerRoute?> = mutableRoute.asStateFlow()

    init {
        viewModelScope.launch { decide() }
    }

    private suspend fun decide() {
        sessions.restore()
        val session = sessions.state.value as? SessionState.SignedIn

        val destination = OnboardingRouter.next(
            signedIn = session != null,
            firstRunComplete = preferences.firstRunComplete,
            profileComplete = session != null && hasProfile(),
            locationAcknowledged = preferences.locationRationaleAcknowledged,
        )

        // US-1.14's active-trip continuity, and the wireframe's own state line: "resumes active
        // ride via GET /v1/rides/passenger/{id}/active". Only from the map — a passenger who still
        // owes a name or has never seen the location rationale is finishing those first, and the
        // ride is not going anywhere.
        mutableRoute.value = if (destination == PassengerDestination.LIVE_MAP && session != null) {
            activeRide(session.userId)?.let(PassengerRoute::ActiveRide) ?: destination.route
        } else {
            destination.route
        }
    }

    /**
     * Whether `iam.users` already has a name for this passenger (US-1.5).
     *
     * **A failed call answers `true`, and that is deliberate.** This runs on a session restored
     * from the secure store, so the passenger has signed in before and has therefore already been
     * through Profile Setup — the screen cannot be reached without it. Answering `false` on a flat
     * tunnel would put a working passenger back on an onboarding form; answering `true` lands them
     * on the map, which has the offline banner and can retry everything. A genuinely new account
     * never takes this path: the login screen computes its own destination from the verify it just
     * made.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun hasProfile(): Boolean = try {
        !profiles.me().firstName.isNullOrBlank()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        true
    }

    /**
     * The ride this passenger is on, or `null`.
     *
     * A failure answers `null` — the opposite default to [hasProfile], and for the opposite
     * reason: the cost of guessing wrong here is landing on the map instead of on the ride, which
     * SCR-PA-010 recovers from the moment the socket connects. Guessing the other way would put a
     * passenger with no ride on a ride screen with nothing to show.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun activeRide(passengerId: Ulid): Ulid? = try {
        rides.getActivePassengerRide(passengerId)?.rideId
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }
}

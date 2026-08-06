package lk.mageride.passenger.settings

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import lk.mageride.passenger.onboarding.PassengerProfileRepository
import lk.mageride.shared.data.models.iam.UserProfile

/**
 * Who is signed in, for the chrome that is not a screen.
 *
 * SCR-PA-033's drawer header draws a name, an id and a phone number, and the drawer belongs to the
 * shell while `GET /v1/users/me` belongs to a screen's data layer. This is the seam between them: a
 * `single` holding the last profile read, so the header renders from state rather than owning a
 * fetch of its own.
 *
 * **One read, shared, and refreshed by whoever changed it.** SCR-PA-027 loads the profile anyway
 * and hands the result here ([adopt]); SCR-PA-027b does the same after a save, which is what makes
 * a renamed passenger's drawer header change without a second round trip. The drawer itself asks
 * for a [refresh] only when it has nothing at all — a header that re-fetched on every open would
 * cost a request each time a passenger looked for the menu.
 */
internal class PassengerIdentity(private val profiles: PassengerProfileRepository) {

    private val mutableProfile = MutableStateFlow<UserProfile?>(null)

    /** The signed-in passenger, or `null` before the first successful read. */
    val profile: StateFlow<UserProfile?> = mutableProfile.asStateFlow()

    /** Takes a profile a screen has already read. */
    fun adopt(profile: UserProfile) {
        mutableProfile.value = profile
    }

    /**
     * Reads `GET /v1/users/me` if nothing has been read yet.
     *
     * **A failure is swallowed and leaves the header brand-only**, which is what it already draws
     * before the first read — a drawer that showed an error where a name goes would put a failed
     * request in front of navigation the passenger opened the drawer to use.
     */
    @Suppress("TooGenericExceptionCaught")
    suspend fun refresh(force: Boolean = false) {
        if (!force && mutableProfile.value != null) return
        try {
            mutableProfile.value = profiles.me()
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // See the KDoc.
        }
    }

    /** Drops the profile — the session it belonged to has ended. */
    fun clear() {
        mutableProfile.value = null
    }
}

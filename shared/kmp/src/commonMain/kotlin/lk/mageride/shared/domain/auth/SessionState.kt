package lk.mageride.shared.domain.auth

import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

/**
 * Why there is no session.
 *
 * The distinction matters to the login screen: a user who was signed out by another device
 * (US-1.12) needs to be told, and one who tapped Log out does not.
 */
public enum class SignedOutReason {

    /** This install has never held a session, or the store was wiped. */
    NEVER_SIGNED_IN,

    /** The user tapped Log out (US-1.7). */
    SIGNED_OUT,

    /**
     * The server refused the credential and the refresh could not recover it (D-29).
     *
     * Reached from a `401` that survives one refresh and one replay, or from a refresh that is
     * itself rejected. **Not** reached from an offline or 5xx failure — see
     * [AuthSessionManager.onAuthenticationLost].
     */
    SESSION_REVOKED,

    /**
     * Someone signed in to **this app** on another device, so this session was revoked (AL-08).
     *
     * Single active device is per app, not per account: a driver and a passenger session on one
     * handset coexist, and only the same surface displaces this one.
     */
    SIGNED_IN_ELSEWHERE,

    /** The account is being erased (`DELETE /v1/users/me`, US-1.8, E-06). */
    ACCOUNT_DELETED,
}

/**
 * An OTP attempt in flight (D5' §14.1: six digits, 60-second resend, five per hour).
 *
 * @property authId Handle from `POST /v1/auth/otp/request`, echoed on verify and resend.
 * @property phone The number the code went to, so the screen can show it.
 * @property deviceId The id bound to this attempt; a verify with a different one is
 *   `409 device-mismatch`.
 * @property attemptsRemaining Entries left before `423 otp-locked`.
 * @property resendAllowedAt When a resend stops being refused locally (D-32).
 * @property isBlocked The number is blocked outright (`user-blocked`). The server still answers
 *   `200` so the screen cannot enumerate blocked numbers by timing; no code will arrive.
 */
public data class OtpChallenge(
    val authId: Ulid,
    val phone: PhoneE164,
    val deviceId: String,
    val attemptsRemaining: Int,
    val resendAllowedAt: Timestamp,
    val isBlocked: Boolean = false,
)

/**
 * Where the session is, as one value the whole app can key off.
 *
 * ```
 *                       restore()
 *   Loading ──────────────────────────────► SignedOut │ SignedIn
 *
 *   SignedOut ──requestOtp()──► AwaitingOtp ──verifyOtp()──► SignedIn
 *        ▲                           │                          │
 *        │                           └──cancelOtp() / locked ────┤
 *        └───────── logout() / revoked / signed in elsewhere ─────┘
 * ```
 *
 * There is no `Refreshing` state: a token rotation is invisible to the UI by design (E-02 exists
 * precisely so a mid-ride refresh does not interrupt anything), and a state the screens must
 * ignore is a state that eventually gets rendered by accident.
 */
public sealed interface SessionState {

    /** Before [AuthSessionManager.restore] has read the secure store. The app shows its splash. */
    public data object Loading : SessionState

    /**
     * No session. The app shows the login screen.
     *
     * @property reason Why, so the screen can explain it.
     */
    public data class SignedOut(val reason: SignedOutReason) : SessionState

    /**
     * An OTP is out; the app shows the code entry screen.
     *
     * @property challenge The attempt in flight.
     */
    public data class AwaitingOtp(val challenge: OtpChallenge) : SessionState

    /**
     * Signed in.
     *
     * Deliberately carries **no tokens**: a screen never needs one, and a state object that holds
     * the refresh token ends up in a log line or a crash report. The tokens live in
     * [lk.mageride.shared.platform.SecureStore] and are reachable only through
     * [lk.mageride.shared.data.api.TokenProvider].
     *
     * @property userId The signed-in user.
     * @property app Which surface this session belongs to (AL-08).
     * @property deviceId This install's stable device id.
     * @property isNewUser `true` when the verify that produced this session created the account,
     *   which is what routes to first-run onboarding. `false` for a restored session.
     */
    public data class SignedIn(val userId: Ulid, val app: AppSurface, val deviceId: String, val isNewUser: Boolean) :
        SessionState
}

/**
 * One-shot things that happen to a session, for the app shell rather than a screen.
 *
 * Same reasoning as [lk.mageride.shared.data.api.MageRideApiSignals]: a revocation can surface on
 * any of the 176 operations, and handling it per call site is 176 chances to forget.
 */
public sealed interface SessionEvent {

    /**
     * The session ended; navigate to login and drop whatever is on the back stack.
     *
     * Emitted for every exit — user logout, revocation, another device — so a single subscriber
     * covers all of them. [reason] is what the login screen shows.
     *
     * @property reason Why the session ended.
     */
    public data class RouteToLogin(val reason: SignedOutReason) : SessionEvent
}

package lk.mageride.shared.domain.auth

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.iam.RefreshSessionRequest
import lk.mageride.shared.data.models.iam.RequestOtpRequest
import lk.mageride.shared.data.models.iam.ResendOtpRequest
import lk.mageride.shared.data.models.iam.VerifyOtpRequest
import kotlin.concurrent.Volatile
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/** What the last token rotation did, which is what decides whether a `401` ends the session. */
private enum class RefreshOutcome {
    /** No rotation has been attempted on this session. */
    NONE,

    /** Rotated; the client now holds a fresh access token. */
    SUCCEEDED,

    /** The server refused the refresh token: it is spent, revoked or unknown (D-29). */
    REVOKED,

    /** Another device signed in to this app and displaced this session (AL-08). */
    DISPLACED,

    /** Offline, 5xx, timeout, breaker open. The credential is probably fine; the network is not. */
    TRANSIENT,
}

/**
 * The session state machine: OTP sign-in, token lifecycle, logout and revocation.
 *
 * This is the whole of D5' §14.2 on the client side — phone OTP only (AL-07: there is no Google,
 * Apple or password path in a mobile app, and the ones `IamApi` exposes are the portals'), a
 * 30-minute RS256 access token beside an opaque single-use rotating refresh token (D-29), and one
 * active session per `(user, app)` (AL-08).
 *
 * **Three rules are load-bearing and easy to break:**
 *
 * 1. *One refresh at a time.* The refresh token is single-use and racing it revokes the whole
 *    session family. Five concurrent `401`s must produce **one** call to `/v1/auth/refresh`, not
 *    five — see [refresh].
 * 2. *Offline is not revoked.* [onAuthenticationLost] ends the session for a refused credential
 *    and does nothing for a network failure. The opposite would sign a driver out of an active
 *    ride every time they drove through a tunnel.
 * 3. *No token leaves this class.* [state] carries a user id, never a token; the only way out is
 *    [SessionTokenProvider], which the HTTP pipeline holds.
 *
 * @param api The iam-svc client, resolved lazily. It has to be: the client is built on an
 *   `HttpClient` that is built on the [lk.mageride.shared.data.api.TokenProvider] that is built on
 *   *this*, so eager injection is a Koin cycle. The lambda is called at the moment of use, by
 *   which time the graph is complete.
 * @param store Where the tokens and the device id live.
 * @param config Which surface this app is, and the timing knobs.
 * @param clock Wall clock; injectable so a test can drive expiry on virtual time.
 */
@OptIn(ExperimentalTime::class)
@Suppress("TooManyFunctions")
public class AuthSessionManager(
    private val api: () -> IamApi,
    private val store: AuthSessionStore,
    private val config: AuthConfig,
    private val clock: () -> Timestamp = { Clock.System.now() },
) {
    private val mutableState = MutableStateFlow<SessionState>(SessionState.Loading)
    private val mutableEvents = MutableSharedFlow<SessionEvent>(
        replay = 1,
        extraBufferCapacity = 4,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    private val refreshMutex = Mutex()

    @Volatile
    private var session: AuthSession? = null

    @Volatile
    private var lastOutcome: RefreshOutcome = RefreshOutcome.NONE

    @Volatile
    private var proactiveRetryAfter: Timestamp? = null

    /** Where the session is. Starts at [SessionState.Loading] until [restore] has run. */
    public val state: StateFlow<SessionState> = mutableState.asStateFlow()

    /**
     * Session-scoped events for the app shell.
     *
     * Replays the last one, so a shell that subscribes after a revocation still routes to login
     * rather than sitting on a screen whose every call now fails.
     */
    public val events: SharedFlow<SessionEvent> = mutableEvents.asSharedFlow()

    /** This install's stable device id (AL-08), minted on first use. */
    public suspend fun deviceId(): String = store.deviceId()

    /**
     * Reads the stored session at cold start. Call once, before the first screen.
     *
     * A restored session is [SessionState.SignedIn] with `isNewUser = false` — the flag exists to
     * route a *just-created* account into onboarding, and a relaunch is not that.
     */
    public suspend fun restore() {
        if (mutableState.value != SessionState.Loading) return
        val stored = store.loadSession()
        if (stored == null || stored.app != config.app) {
            if (stored != null) store.wipeSession()
            mutableState.value = SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN)
            return
        }
        session = stored
        mutableState.value = stored.toSignedIn(isNewUser = false)
    }

    // ------------------------------------------------------------------------------------------
    // Phone OTP — the only sign-in an app has (AL-07)
    // ------------------------------------------------------------------------------------------

    /**
     * `POST /v1/auth/otp/request` — sends a six-digit code and moves to
     * [SessionState.AwaitingOtp].
     *
     * @param phone E.164, `+947XXXXXXXX` (D5' §14.1).
     * @param fcmToken This install's push token, so the first notification can be delivered.
     * @return The challenge, also published on [state].
     */
    public suspend fun requestOtp(phone: PhoneE164, fcmToken: String? = null): OtpChallenge {
        val device = store.deviceId()
        val response = api().requestOtp(
            RequestOtpRequest(phone = phone, deviceId = device, fcmToken = fcmToken, role = config.app),
        )
        val challenge = OtpChallenge(
            authId = response.authId,
            phone = phone,
            deviceId = device,
            attemptsRemaining = response.attemptsRemaining,
            resendAllowedAt = clock() + cooldownOf(response.cooldownSeconds),
            isBlocked = response.isBlocked,
        )
        mutableState.value = SessionState.AwaitingOtp(challenge)
        return challenge
    }

    /**
     * `POST /v1/auth/otp/resend` — re-sends the code for the in-flight attempt.
     *
     * Refuses locally before [OtpChallenge.resendAllowedAt]: the screen owns the countdown, so a
     * call inside the window is a bug rather than a user action, and D-32's server-side bucket
     * would spend one of the five hourly attempts on it.
     */
    public suspend fun resendOtp(): OtpChallenge {
        val challenge = awaitingChallenge()
        val now = clock()
        check(now >= challenge.resendAllowedAt) { "resend is on cooldown until ${challenge.resendAllowedAt}" }

        val response = api().resendOtp(ResendOtpRequest(authId = challenge.authId))
        val updated = challenge.copy(
            attemptsRemaining = response.attemptsRemaining,
            resendAllowedAt = now + cooldownOf(response.cooldownSeconds),
        )
        mutableState.value = SessionState.AwaitingOtp(updated)
        return updated
    }

    /**
     * `POST /v1/auth/otp/verify` — exchanges the code for a session.
     *
     * On success the token pair is written to the secure store **before** the state moves, so a
     * process death between the two leaves a usable session rather than a signed-in screen with
     * no credential.
     *
     * A wrong code (`401 invalid-otp`) keeps the challenge and decrements the local attempt
     * counter; a dead attempt (`423 otp-locked`, `400 otp-expired`, `404 auth-not-found`) returns
     * to [SessionState.SignedOut] because that `authId` can never succeed again. Both rethrow —
     * the screen still has to say what happened.
     */
    public suspend fun verifyOtp(otp: String): SessionState.SignedIn {
        val challenge = awaitingChallenge()
        val response = try {
            api().verifyOtp(VerifyOtpRequest(authId = challenge.authId, otp = otp, deviceId = challenge.deviceId))
        } catch (cause: MageRideError) {
            onVerifyFailed(challenge, cause)
            throw cause
        }

        // A different user on the same handset is a different session, and the previous one's
        // MQTT token is bound to a vehicle they may no longer drive (E-02).
        val previous = store.loadSession()
        if (previous != null && previous.userId != response.user.userId) store.wipeSession()
        store.clearMqttToken()

        val issued = AuthSession(
            userId = response.user.userId,
            app = config.app,
            deviceId = challenge.deviceId,
            accessToken = response.accessToken,
            accessTokenExpiresAt = clock() + response.expiresIn.seconds,
            refreshToken = response.refreshToken,
        )
        store.saveSession(issued)
        session = issued
        lastOutcome = RefreshOutcome.NONE
        proactiveRetryAfter = null

        val signedIn = issued.toSignedIn(isNewUser = response.isNewUser)
        mutableState.value = signedIn
        return signedIn
    }

    /** Abandons the in-flight OTP attempt and goes back to the login screen. */
    public suspend fun cancelOtp() {
        if (mutableState.value is SessionState.AwaitingOtp) {
            mutableState.value = SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN)
        }
    }

    // ------------------------------------------------------------------------------------------
    // Ending a session
    // ------------------------------------------------------------------------------------------

    /**
     * `POST /v1/auth/logout` — revokes this device's refresh token, then clears local state
     * (US-1.7).
     *
     * The local half happens whether or not the call succeeds. A logout that failed because the
     * handset is offline must still sign the user out of *this* device; the server-side token is
     * bounded by its own TTL and by the next `new-device` login.
     */
    @Suppress("TooGenericExceptionCaught")
    public suspend fun logout() {
        try {
            api().logout()
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // Best effort by design — see the KDoc.
        }
        endSession(SignedOutReason.SIGNED_OUT)
    }

    /**
     * `DELETE /v1/users/me` — requests erasure, then wipes the install (US-1.8, E-06).
     *
     * `202`, not `200`: the account is not gone yet, and a statutory hold can keep it. What *is*
     * immediate is that this device stops holding credentials for it.
     *
     * @return The PDPA request id to poll.
     */
    public suspend fun deleteAccount(): Ulid {
        val accepted = api().deleteMyAccount()
        store.erase()
        session = null
        lastOutcome = RefreshOutcome.NONE
        publishSignedOut(SignedOutReason.ACCOUNT_DELETED)
        return accepted.requestId
    }

    // ------------------------------------------------------------------------------------------
    // The TokenProvider side — called by the HTTP pipeline, never by a screen
    // ------------------------------------------------------------------------------------------

    /**
     * The access token to put in `Authorization`, refreshed ahead of expiry when it is close
     * enough (ADD §12.1 "proactive refresh").
     *
     * Returns whatever the session holds if that refresh fails: a token that is about to expire
     * is still worth sending, and the `401` path is the backstop.
     */
    internal suspend fun accessToken(): String? {
        val current = session ?: return null
        if (shouldRefreshEarly(current)) refresh(current.accessToken)
        return session?.accessToken
    }

    /**
     * Rotates the token pair, at most once no matter how many callers ask at once.
     *
     * The collapsing is keyed on the token that failed, not on the lock alone. Five requests that
     * went out together produce five `401`s; the first to take the lock rotates, and the other
     * four find that the session no longer holds the token *they* sent, so they answer "replay"
     * without touching the refresh token. A lock on its own would not do it — a caller that
     * acquired it after the rotation finished would rotate the value that rotation had just
     * produced, which is precisely the race D-29 punishes with a revoked session family.
     *
     * @param staleAccessToken The access token the failing request carried.
     * @return `true` when a usable access token is available and the request should be replayed.
     */
    internal suspend fun refresh(staleAccessToken: String?): Boolean = refreshMutex.withLock {
        val current = session
        when {
            current == null -> {
                lastOutcome = RefreshOutcome.REVOKED
                false
            }

            staleAccessToken != null && current.accessToken != staleAccessToken -> true

            else -> rotate(current)
        }
    }

    /**
     * The pipeline gave up: the refresh failed, or the replay after a successful one was still
     * `401`.
     *
     * **Only a refused credential ends the session.** A transient failure leaves the session
     * exactly where it was so the next call — or the next tunnel exit — can succeed; the caller
     * still sees its own `401`, which is the honest answer to "did that request work?".
     */
    internal suspend fun onAuthenticationLost() {
        if (session == null) return
        when (lastOutcome) {
            RefreshOutcome.TRANSIENT -> return
            RefreshOutcome.DISPLACED -> endSession(SignedOutReason.SIGNED_IN_ELSEWHERE)
            else -> endSession(SignedOutReason.SESSION_REVOKED)
        }
    }

    // ------------------------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------------------------

    private suspend fun rotate(current: AuthSession): Boolean = try {
        val rotated = api().refreshSession(RefreshSessionRequest(refreshToken = current.refreshToken))
        val updated = current.copy(
            accessToken = rotated.accessToken,
            refreshToken = rotated.refreshToken,
            accessTokenExpiresAt = clock() + rotated.expiresIn.seconds,
        )
        // Persist before the in-memory copy moves: the old refresh token is spent the moment
        // the server answered, so losing the new one to a crash here is a forced sign-out.
        store.saveSession(updated)
        session = updated
        lastOutcome = RefreshOutcome.SUCCEEDED
        proactiveRetryAfter = null
        true
    } catch (cause: MageRideError) {
        lastOutcome = cause.toOutcome()
        if (lastOutcome == RefreshOutcome.TRANSIENT) {
            proactiveRetryAfter = clock() + config.refreshRetryCooldown
        }
        false
    }

    private fun shouldRefreshEarly(current: AuthSession): Boolean {
        val now = clock()
        val retryAfter = proactiveRetryAfter
        if (retryAfter != null && now < retryAfter) return false
        return now >= current.accessTokenExpiresAt - config.accessTokenRefreshSkew
    }

    private suspend fun endSession(reason: SignedOutReason) {
        refreshMutex.withLock {
            session = null
            lastOutcome = RefreshOutcome.NONE
            proactiveRetryAfter = null
            store.wipeSession()
        }
        publishSignedOut(reason)
    }

    private fun publishSignedOut(reason: SignedOutReason) {
        mutableState.value = SessionState.SignedOut(reason)
        mutableEvents.tryEmit(SessionEvent.RouteToLogin(reason))
    }

    private fun onVerifyFailed(challenge: OtpChallenge, cause: MageRideError) {
        if (cause.endsTheAttempt()) {
            mutableState.value = SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN)
            return
        }
        if (cause.code == ErrorCode.INVALID_OTP) {
            val remaining = (challenge.attemptsRemaining - 1).coerceAtLeast(0)
            mutableState.value = SessionState.AwaitingOtp(challenge.copy(attemptsRemaining = remaining))
        }
    }

    private fun awaitingChallenge(): OtpChallenge {
        val current = mutableState.value
        check(current is SessionState.AwaitingOtp) { "no OTP attempt is in flight (state = $current)" }
        return current.challenge
    }

    private fun cooldownOf(seconds: Int) = if (seconds > 0) seconds.seconds else config.otpResendCooldown

    private fun AuthSession.toSignedIn(isNewUser: Boolean) = SessionState.SignedIn(
        userId = userId,
        app = app,
        deviceId = deviceId,
        isNewUser = isNewUser,
    )
}

/**
 * Whether this failure means the `authId` is finished, as opposed to one wrong digit.
 *
 * `423 otp-locked` spent the attempt budget, `400 otp-expired` timed the code out and
 * `404 auth-not-found` never had one — none of the three can succeed on a retry, so the screen
 * has to start a new attempt.
 */
private fun MageRideError.endsTheAttempt(): Boolean = when (this) {
    is MageRideError.Locked, is MageRideError.NotFound -> true
    is MageRideError.BadRequest -> code == ErrorCode.OTP_EXPIRED
    else -> false
}

/**
 * How a failed refresh should be read.
 *
 * `403 device-revoked` is the AL-08 displacement code named in `mobile_db_schema.md` §0.4; it is
 * matched on the wire spelling because `_shared.yaml#/components/schemas/ErrorCode` does not
 * carry it — see this component's handoff. Anything the server did not *decide* is transient.
 */
private fun MageRideError.toOutcome(): RefreshOutcome = when (this) {
    is MageRideError.Unauthorized -> RefreshOutcome.REVOKED

    is MageRideError.Forbidden ->
        if (wireCode == DEVICE_REVOKED_CODE) RefreshOutcome.DISPLACED else RefreshOutcome.REVOKED

    is MageRideError.BadRequest, is MageRideError.NotFound -> RefreshOutcome.REVOKED

    else -> RefreshOutcome.TRANSIENT
}

private const val DEVICE_REVOKED_CODE = "device-revoked"

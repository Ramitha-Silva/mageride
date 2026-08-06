package lk.mageride.passenger.onboarding

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.push.PushTokenProvider
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.OtpChallenge
import lk.mageride.shared.domain.auth.SessionState
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

/** Which half of SCR-PA-003 is live. The wireframe draws both on one screen. */
internal enum class LoginPhase {

    /** Only the `+94` field is enabled; Continue requests the code. */
    PHONE,

    /** A code is out. The OTP cells are enabled and the resend counts down. */
    OTP,
}

/**
 * SCR-PA-003's state.
 *
 * @property phone The national number, always normalised — see [PhoneNumber].
 * @property otp What has been typed into the six cells.
 * @property busy A request is in flight; the CTA shows its inline loader.
 * @property error The resolved copy for the last failure, or `null`.
 * @property resendInSeconds Seconds until Resend is offered again (US-1.10's 60-second cooldown).
 * @property attemptsRemaining Verifies left before `423 otp-locked`, once the server has said.
 */
internal data class LoginState(
    val phase: LoginPhase = LoginPhase.PHONE,
    val phone: String = "",
    val otp: String = "",
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
    val resendInSeconds: Int = 0,
    val attemptsRemaining: Int? = null,
) {
    /** Whether the number is a complete `+947XXXXXXXX`. */
    val phoneValid: Boolean get() = PhoneNumber.isValid(phone)

    /** Whether the six digits are all there. */
    val otpComplete: Boolean get() = otp.length == OTP_LENGTH

    /** The CTA is live when the step it belongs to can be submitted. */
    val canSubmit: Boolean
        get() = !busy && if (phase == LoginPhase.PHONE) phoneValid else otpComplete

    /**
     * Resend is refused **locally** inside the cooldown.
     *
     * US-1.10 is a 60-second wait, and D-32 caps requests at five an hour — so a tap inside the
     * cooldown does not merely fail, it spends one of the five. Gating the button is what keeps a
     * passenger who taps four times in frustration from locking themselves out for an hour.
     */
    val canResend: Boolean get() = phase == LoginPhase.OTP && !busy && resendInSeconds <= 0

    internal companion object {
        /** D5' §14.1 — six digits. */
        const val OTP_LENGTH = 6
    }
}

/**
 * SCR-PA-003 — `+94` phone, then the SMS OTP.
 *
 * **Phone-OTP only** (AL-07). The URD's own cost decision is the reason: Firebase Phone Auth is
 * ~Rs 90 an SMS in Sri Lanka against Rs 0.50–1.50 through a local gateway. `IamApi` carries
 * Google, Apple and password sign-in for the Fleet and Admin portals and **none of them may be
 * reached from here**; `AuthSessionManager` is the only door this screen has, and it has no other.
 *
 * Everything about tokens, the device binding and the single-active-device rule is C014's — this
 * view model owns the two things a screen owns: what is on the field, and what to say when the
 * server says no.
 */
internal class LoginViewModel(
    private val sessions: AuthSessionManager,
    private val onboarding: OnboardingRepository,
    private val profiles: PassengerProfileRepository,
    private val preferences: AppPreferences,
    private val pushTokens: PushTokenProvider,
) : ViewModel() {

    private val mutableState = MutableStateFlow(LoginState())
    private val mutableDestination = MutableStateFlow<PassengerDestination?>(null)

    private var countdown: Job? = null

    val state: StateFlow<LoginState> = mutableState.asStateFlow()

    /** Set once the passenger is through; the screen navigates and this screen is popped. */
    val destination: StateFlow<PassengerDestination?> = mutableDestination.asStateFlow()

    init {
        // A session restored from the secure store means the passenger never needed this screen —
        // the shell can land on it from a `RouteToLogin`, and from a deep link on a cold start.
        if (sessions.state.value is SessionState.SignedIn) {
            viewModelScope.launch { finish() }
        }
    }

    fun onPhoneChanged(input: String) {
        mutableState.update { it.copy(phone = PhoneNumber.normalise(input), error = null) }
    }

    fun onOtpChanged(input: String) {
        mutableState.update { it.copy(otp = input, error = null) }
    }

    /** Back from the OTP half to the number, without abandoning the attempt server-side. */
    fun editPhoneNumber() {
        countdown?.cancel()
        viewModelScope.launch { sessions.cancelOtp() }
        mutableState.update {
            it.copy(phase = LoginPhase.PHONE, otp = "", error = null, resendInSeconds = 0, attemptsRemaining = null)
        }
    }

    /** The CTA: requests the code on the phone half, verifies it on the OTP half. */
    fun submit() {
        val current = mutableState.value
        if (!current.canSubmit) return

        when (current.phase) {
            LoginPhase.PHONE -> execute {
                // The push token rides along on the OTP request so the very first notification a
                // passenger can receive — a driver assigned, a package on its way — has somewhere
                // to land before they have opened the app a second time.
                val challenge = sessions.requestOtp(
                    PhoneNumber.toE164(current.phone),
                    pushTokens.current(),
                )
                mutableState.update { it.copy(phase = LoginPhase.OTP, otp = "") }
                applyChallenge(challenge)
            }

            LoginPhase.OTP -> execute(onFailure = { onVerifyFailed() }) {
                sessions.verifyOtp(current.otp)
                finish()
            }
        }
    }

    /** `POST /v1/auth/otp/resend`. Refused locally while [LoginState.resendInSeconds] is positive. */
    fun resend() {
        if (!mutableState.value.canResend) return
        execute(onFailure = { mutableState.update { it.copy(otp = "") } }) {
            applyChallenge(sessions.resendOtp())
        }
    }

    /**
     * A verify that did not take.
     *
     * A wrong digit keeps the attempt alive with one fewer try; a **dead** attempt
     * (`423 otp-locked`, `400 otp-expired`, `404 auth-not-found`) takes C014 back to `SignedOut`,
     * because that `authId` can never succeed again — so the screen goes back to the number rather
     * than offering a seventh box.
     */
    private fun onVerifyFailed() {
        val awaiting = sessions.state.value as? SessionState.AwaitingOtp
        mutableState.update { current ->
            current.copy(
                otp = "",
                phase = if (awaiting == null) LoginPhase.PHONE else current.phase,
                attemptsRemaining = awaiting?.challenge?.attemptsRemaining ?: current.attemptsRemaining,
            )
        }
    }

    /**
     * What happens the moment there is a bearer token.
     *
     * The language chosen on SCR-PA-002 is pushed to `iam.users` here because this is the first
     * point at which it can be (that screen runs signed out), and the destination is computed from
     * the profile the server actually holds rather than from `isNewUser` — a passenger who
     * installed, signed in and killed the app before Profile Setup is not a new user and still has
     * no profile.
     */
    private suspend fun finish() {
        onboarding.syncPreferences()
        mutableDestination.value = OnboardingRouter.next(
            signedIn = true,
            firstRunComplete = preferences.firstRunComplete,
            profileComplete = hasProfile(),
            locationAcknowledged = preferences.locationRationaleAcknowledged,
        )
    }

    /**
     * Whether this passenger already has a name on `iam.users` (US-1.5).
     *
     * A failure answers `false` — the opposite of the splash's choice, and for the opposite reason.
     * Here the passenger has just signed in, so the network was working a moment ago; the safe
     * outcome is to show Profile Setup, which is idempotent (`PUT /v1/users/me`) and where a
     * brand-new passenger belongs anyway.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun hasProfile(): Boolean = try {
        !profiles.me().firstName.isNullOrBlank()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        false
    }

    /**
     * Puts a fresh challenge on screen: the attempt counter, and the countdown that gates Resend.
     *
     * The countdown reads [OtpChallenge.resendAllowedAt] on every tick rather than counting down
     * from a number — a screen backgrounded for thirty seconds comes back with thirty seconds
     * gone, not with the timer where it was left.
     */
    private fun applyChallenge(challenge: OtpChallenge) {
        mutableState.update { it.copy(attemptsRemaining = challenge.attemptsRemaining) }
        countdown?.cancel()
        countdown = viewModelScope.launch {
            while (true) {
                val remaining = (challenge.resendAllowedAt - Clock.System.now()).inWholeSeconds
                mutableState.update { it.copy(resendInSeconds = remaining.coerceAtLeast(0).toInt()) }
                if (remaining <= 0) return@launch
                delay(1.seconds)
            }
        }
    }

    /**
     * The one shape every call on this screen has: busy on, error cleared, error resolved on the
     * way out.
     *
     * `CancellationException` is rethrown rather than shown — a cancelled coroutine is the screen
     * going away, not a failure the passenger caused.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun execute(onFailure: (Throwable) -> Unit = {}, block: suspend () -> Unit) {
        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure(cause)
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            } finally {
                mutableState.update { it.copy(busy = false) }
            }
        }
    }
}

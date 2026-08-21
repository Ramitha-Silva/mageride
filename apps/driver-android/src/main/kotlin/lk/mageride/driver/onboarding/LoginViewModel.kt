package lk.mageride.driver.onboarding

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
import lk.mageride.driver.push.PushTokenProvider
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.OtpChallenge
import lk.mageride.shared.domain.auth.SessionState
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/** Which half of SCR-DA-003 is live. The wireframe draws both on one screen. */
internal enum class LoginPhase {

    /** Only the `+94` field is enabled; Continue requests the code. */
    PHONE,

    /** A code is out. The OTP cells are enabled and the resend counts down. */
    OTP,
}

/**
 * SCR-DA-003's state.
 *
 * @property phone The national number, always normalised — see [PhoneNumber].
 * @property otp What has been typed into the six cells.
 * @property busy A request is in flight; the CTA shows its inline loader.
 * @property error The resolved copy for the last failure, or `null`.
 * @property resendInSeconds Seconds until Resend is offered again (D-32's 60-second bucket).
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

    /** Resend is refused locally inside the cooldown — a call inside it spends a D-32 attempt. */
    val canResend: Boolean get() = phase == LoginPhase.OTP && !busy && resendInSeconds <= 0

    internal companion object {
        /** D5' §14.1 — six digits. */
        const val OTP_LENGTH = 6
    }
}

/**
 * SCR-DA-003 — `+94` phone, then the SMS OTP.
 *
 * **Phone-OTP only** (AL-07 / US-11.5). `IamApi` carries Google, Apple and password sign-in for
 * the Fleet and Admin portals and none of them may be reached from here; `AuthSessionManager` is
 * the only door this screen has, and it has no other.
 *
 * Everything about tokens, the device binding and the single-active-device rule is C014's —
 * this view model owns the two things a screen owns: what is on the field, and what to say when
 * the server says no.
 */
@OptIn(ExperimentalTime::class)
internal class LoginViewModel(
    private val sessions: AuthSessionManager,
    private val onboarding: OnboardingRepository,
    private val profiles: DriverProfileRepository,
    private val preferences: OnboardingPreferences,
    private val pushTokens: PushTokenProvider,
) : ViewModel() {

    private val mutableState = MutableStateFlow(LoginState())
    private val mutableDestination = MutableStateFlow<OnboardingDestination?>(null)

    private var countdown: Job? = null

    val state: StateFlow<LoginState> = mutableState.asStateFlow()

    /** Set once the driver is through; the screen navigates and this screen is popped. */
    val destination: StateFlow<OnboardingDestination?> = mutableDestination.asStateFlow()

    init {
        // A session restored from the secure store means the driver never needed this screen —
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
                // The push token rides along on the OTP request so the very first notification —
                // the approval push a new driver is waiting for — has somewhere to go.
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
     * The two first-run preferences are pushed to `iam.users` here because this is the first point
     * at which they can be (SCR-DA-002 runs signed out), and the destination is computed from the
     * profile the server actually holds rather than from `isNewUser` — a driver who installed,
     * signed in and killed the app before Profile Setup is not a new user and still has no
     * profile.
     */
    private suspend fun finish() {
        onboarding.syncPreferences()
        mutableDestination.value = OnboardingRouter.next(
            signedIn = true,
            firstRunComplete = preferences.firstRunComplete,
            profileComplete = hasProfile(),
            permissionsAcknowledged = preferences.permissionsAcknowledged,
        )
    }

    /**
     * Whether this driver already has a `registry.driver_profiles` row (US-2.21).
     *
     * **Δ MCS-05 — `GET /v1/drivers/profile`, not `iam.users.first_name`.** The old read was the
     * passenger app's, and on this surface it answered a different question: somebody who signs
     * into the Driver App with the number they already use for the Passenger App has a name, and
     * would have gone straight past the screen that collects their driving licence.
     *
     * A failure answers `false` — the opposite of the splash's choice, and for the opposite
     * reason. Here the driver has just signed in, so the network was working a moment ago; the
     * safe outcome is to show Profile Setup, which is idempotent (`PUT /v1/drivers/profile`) and
     * where a brand-new driver belongs anyway.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun hasProfile(): Boolean = try {
        profiles.hasDriverProfile()
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
     * going away, not a failure the driver caused.
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

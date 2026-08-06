package lk.mageride.passenger.onboarding

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Turns a failure into copy a passenger can read, in their own language.
 *
 * **D-26: an app never renders `title`, `detail` or `message` from a `ProblemDetails`.** Those are
 * English strings written for an operator, and putting one on a Sinhala screen is how a trilingual
 * app becomes an English one at exactly the moment it matters. The kebab `code` is the key; the
 * copy is `strings.xml`'s, in all three languages.
 *
 * **This is the first-run set only.** Every code below is one `iam.yaml` declares on the three
 * operations SCR-PA-003 and SCR-PA-004 can reach — the OTP request, the resend, the verify and the
 * profile save. C078–C084 add their own tables for the codes their contracts declare; one `when`
 * over the whole platform would be a function nobody can check against a contract, which is the
 * only way a table like this stays true.
 */
internal object OnboardingErrors {

    /** The string resource for [cause], falling back to the shell's generic message. */
    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        // D-32's bucket seen at the transport layer — the gateway refused before iam-svc was
        // reached, so there is no kebab code to read.
        is MageRideError.RateLimited -> R.string.error_otp_rate_limited

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    @StringRes
    @Suppress("ReturnCount") // One arm per code the three operations declare; a chain reads worse.
    private fun forCode(code: ErrorCode?): Int = when (code) {
        // The six digits were wrong. The attempt survives with one fewer try — the screen says how
        // many are left, which is what stops a passenger burning the last one guessing.
        ErrorCode.INVALID_OTP -> R.string.error_otp_invalid

        // The five-minute window closed. A new code is the only way forward, so the copy says so.
        ErrorCode.OTP_EXPIRED -> R.string.error_otp_expired

        // Out of attempts. `AuthSessionManager` has already dropped back to `SignedOut`, so the
        // screen returns to the number — there is no seventh box to offer.
        ErrorCode.OTP_LOCKED -> R.string.error_otp_locked

        // D-32: 5 requests an hour. Not the same as OTP_LOCKED — the code was never wrong.
        ErrorCode.OTP_RATE_LIMITED -> R.string.error_otp_rate_limited

        // The number itself is not a `+947XXXXXXXX`. Reachable in practice only from a paste that
        // survived `PhoneNumber.normalise`, which is why the copy names the format.
        ErrorCode.INVALID_PHONE -> R.string.error_phone_invalid

        // AL-08's single-active-device rule seen from the login side: the OTP was requested from
        // one install and verified from another, which is a different sign-in attempt entirely.
        ErrorCode.DEVICE_MISMATCH -> R.string.error_device_mismatch

        // The `authId` is gone — a verify against an attempt the server has already retired.
        ErrorCode.AUTH_NOT_FOUND -> R.string.error_otp_expired

        ErrorCode.USER_BLOCKED -> R.string.error_user_blocked

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        else -> R.string.error_generic
    }
}

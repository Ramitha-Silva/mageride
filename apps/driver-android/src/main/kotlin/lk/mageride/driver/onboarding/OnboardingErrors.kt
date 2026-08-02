package lk.mageride.driver.onboarding

import androidx.annotation.StringRes
import lk.mageride.driver.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Turns a failure into copy a driver can read, in their own language.
 *
 * **D-26: an app never renders `title`, `detail` or `message` from a `ProblemDetails`.** Those are
 * English strings written for an operator, and putting one on a Sinhala screen is how a
 * trilingual app becomes an English one at exactly the moment it matters. The kebab `code` is the
 * key; the copy is `strings.xml`'s, in all three languages.
 *
 * Everything cluster 1 can fail with is here, in one place, because the same six OTP codes appear
 * on the login screen, the resend and the verify.
 */
internal object OnboardingErrors {

    /** The string resource for [cause], falling back to the shell's generic message. */
    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is DocumentUploadUnavailableException -> R.string.error_upload_unavailable

        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        is MageRideError.RateLimited -> R.string.error_otp_rate_limited

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    @StringRes
    private fun forCode(code: ErrorCode?): Int = when (code) {
        ErrorCode.INVALID_OTP -> R.string.error_otp_invalid

        ErrorCode.OTP_EXPIRED -> R.string.error_otp_expired

        ErrorCode.OTP_LOCKED -> R.string.error_otp_locked

        ErrorCode.OTP_RATE_LIMITED -> R.string.error_otp_rate_limited

        // AL-08's single-active-device rule seen from the login side: the OTP was requested from
        // one install and verified from another, which is a different sign-in attempt entirely.
        ErrorCode.DEVICE_MISMATCH -> R.string.error_device_mismatch

        ErrorCode.USER_BLOCKED -> R.string.error_user_blocked

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        else -> R.string.error_generic
    }
}

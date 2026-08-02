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
 * Everything cluster 1 and the Mode-C wizard can fail with is here, in one place, because the same
 * six OTP codes appear on the login screen, the resend and the verify, and the same four vehicle
 * codes appear on all four wizard steps (C069).
 */
internal object OnboardingErrors {

    /** The string resource for [cause], falling back to the shell's generic message. */
    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        // A document too large for the gateway. Its own message, because "try again" is wrong
        // advice for a photograph that will be the same size next time (Δ MCS-01).
        is MageRideError.PayloadTooLarge -> R.string.error_image_too_large

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

        // ---- C069 · the Mode-C wizard (SCR-DA-004…004c) ------------------------------------
        // D-37's active-set uniqueness. The wizard also renders this one *inline on the plate
        // field* rather than as a screen error, because that one field is what has to change.
        ErrorCode.REGISTRATION_EXISTS -> R.string.error_registration_exists

        // AL-27's fence, seen from the client side: the Driver App onboards Mode C only, and a
        // Mode A/B vehicle or a route permit belongs to the Fleet Portal.
        ErrorCode.MODE_NOT_ALLOWED, ErrorCode.INVALID_VEHICLE_TYPE -> R.string.error_mode_not_allowed

        ErrorCode.NOT_OWNER -> R.string.error_not_owner

        ErrorCode.VEHICLE_NOT_FOUND -> R.string.error_vehicle_not_found

        else -> R.string.error_generic
    }
}

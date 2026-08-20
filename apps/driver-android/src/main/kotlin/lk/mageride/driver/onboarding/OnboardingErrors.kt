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

        is MageRideError -> forCode(cause.code) ?: byType(cause)

        else -> R.string.error_generic
    }

    /**
     * The code table, one half per screen group.
     *
     * Split by *screen group* rather than by kind: cluster 1 and the Mode-C wizard are C068/C069's,
     * the dashboard and the ride lifecycle are C070's, and each half is the set of codes its own
     * contracts declare. One `when` over all of them would be a function nobody can read against a
     * contract, which is the only way this table stays true.
     *
     * `null` means *no table claims this code*, which is the caller's cue to fall back to
     * [byType] rather than to the generic message — see [messageFor].
     */
    @StringRes
    private fun forCode(code: ErrorCode?): Int? = onboardingCode(code)
        ?: dashboardCode(code)
        ?: walletCode(code)
        ?: safetyCode(code)
        ?: platformCode(code)

    /**
     * The kernel's cross-cutting codes (C002) — the half of the registry no screen group owns.
     *
     * These are the codes **every** call can answer, which is exactly why their absence showed:
     * with no row here a `403 forbidden`, a `503 service-unavailable` and a `426 upgrade-required`
     * all reached the driver as *"Something went wrong"*, which says nothing about whether to
     * wait, to sign in again, or to call somebody.
     */
    @StringRes
    private fun platformCode(code: ErrorCode?): Int? = when (code) {
        // The session is gone and D-29's single refresh has already been spent. The shell routes
        // to login off `SessionEvent`; this is the copy that explains why the screen went.
        ErrorCode.UNAUTHORIZED -> R.string.session_expired

        // AL-06 deny-by-default. On this app it is nearly always the `driver` role missing from
        // an account that was created on the passenger side: every `/v1/vehicles` and
        // `/v1/drivers` route demands that role, and nothing a driver can reach grants it.
        ErrorCode.FORBIDDEN -> R.string.error_forbidden

        ErrorCode.BAD_REQUEST -> R.string.error_validation_failed

        // Ours, not the driver's — and unlike a 4xx, waiting is genuinely the right advice.
        ErrorCode.INTERNAL_ERROR,
        ErrorCode.SERVICE_UNAVAILABLE,
        ErrorCode.DEPENDENCY_UNAVAILABLE,
        ErrorCode.UPSTREAM_TIMEOUT,
        -> R.string.error_service_down

        // D-31's version gate. `MageRideApiSignals.upgradeRequired` is what puts the wall up;
        // this is what the screen underneath says while it does.
        ErrorCode.UPGRADE_REQUIRED -> R.string.error_upgrade_required

        // D-30. Play Integrity was rejected at the edge, and no amount of retrying fixes a build
        // that cannot attest — the copy has to say where a working copy comes from instead.
        ErrorCode.ATTESTATION_FAILED -> R.string.error_attestation_failed

        // The 429 arm of [messageFor] catches this by status; the code is here for a bucket that
        // answered one without it.
        ErrorCode.RATE_LIMITED -> R.string.error_otp_rate_limited

        else -> null
    }

    /**
     * The coarse fallback, on the error's own type.
     *
     * `MageRideError`'s KDoc asks for exactly this split — *"branching is meant to happen on the
     * type for the coarse decision and on `code` for the fine one"*. It is what answers the two
     * cases [forCode] cannot: a problem body whose `code` this build predates, which
     * `ErrorCode.fromWire` resolves to `null` by design, and a 5xx from something between the app
     * and the gateway, which carries no MageRide code at all and would otherwise be
     * indistinguishable from a bug in this app.
     */
    @StringRes
    private fun byType(cause: MageRideError): Int = when (cause) {
        is MageRideError.AttestationFailed -> R.string.error_attestation_failed
        is MageRideError.UpgradeRequired -> R.string.error_upgrade_required
        is MageRideError.Unauthorized -> R.string.session_expired
        is MageRideError.Forbidden -> R.string.error_forbidden
        is MageRideError.BadRequest -> R.string.error_validation_failed
        is MageRideError.Server -> R.string.error_service_down
        else -> R.string.error_generic
    }

    @StringRes
    @Suppress("ReturnCount")
    private fun onboardingCode(code: ErrorCode?): Int? = when (code) {
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

        else -> null
    }

    /** C070 · standby, Directional Travel and the ride lifecycle. */
    @StringRes
    private fun dashboardCode(code: ErrorCode?): Int? = when (code) {
        // D5' §14.1a's gate seen from the dashboard: the toggle moved, and the vehicle behind it
        // has not cleared onboarding.
        ErrorCode.VEHICLE_NOT_APPROVED -> R.string.error_vehicle_not_approved

        // D-03's active-session mutex. A driver holds one live session or one ride, never both,
        // so this is nearly always a session left open on another handset.
        ErrorCode.DRIVER_ALREADY_LIVE -> R.string.error_driver_already_live

        // DT-01's `403` — a Directional filter is a standby filter, and there is no standby to
        // filter while the driver is offline.
        ErrorCode.NOT_ONLINE -> R.string.error_not_online

        // DT-03's daily budget. Turning a filter off still spends its use (US-6A.19), which is
        // why a driver can reach this having "only used one".
        ErrorCode.DIRECTIONAL_LIMIT_REACHED -> R.string.error_directional_limit

        // R-14. Somebody else moved the ride — the passenger cancelled, or a timer fired. The
        // answer is to look again, never to resend with a bumped version.
        ErrorCode.VERSION_CONFLICT -> R.string.error_version_conflict

        // The command is not legal from where the ride actually is (ADD Appendix B.2).
        ErrorCode.ILLEGAL_TRANSITION -> R.string.error_illegal_transition

        // D-08's daily-fee gate on the second trip of the day, not a dispatch failure (US-9.1).
        ErrorCode.INSUFFICIENT_WALLET -> R.string.error_insufficient_wallet

        // AL-47: this ride has already settled another way, so there is nothing to attest to.
        ErrorCode.PAYMENT_ALREADY_SETTLED -> R.string.error_payment_settled

        else -> null
    }

    /**
     * C073 · the wallet cluster (SCR-DA-021…025).
     *
     * The four codes `wallet.yaml` declares that nothing else in this app can meet. Each is written
     * to be true wherever it lands rather than to name a screen: `not-found` on a credit transfer
     * is a Driver ID nobody has, and the copy says *"check what you entered"*, which is the right
     * advice in both readings.
     */
    @StringRes
    private fun walletCode(code: ErrorCode?): Int? = when (code) {
        // Below a gateway's floor, above the field's ceiling, or a voucher denomination that is not
        // a tier — `POST /v1/wallet/voucher/purchase` refuses an amount between tiers rather than
        // rounding it, because interpolating one would invent a rate no admin set.
        ErrorCode.INVALID_AMOUNT -> R.string.error_invalid_amount

        // OnePay or the bank IPG did not answer. Nothing was charged and nothing was credited.
        ErrorCode.GATEWAY_ERROR -> R.string.error_gateway_error

        // Approving a request somebody already answered, or a top-up session that has moved on.
        ErrorCode.CONFLICT -> R.string.error_already_done

        ErrorCode.NOT_FOUND -> R.string.error_not_found

        else -> null
    }

    /**
     * C075 · the comms, safety and support cluster (SCR-DA-031…034).
     *
     * Three codes, and the first is the one that matters: `400 no-emergency-contact` is a **setup**
     * failure, and `SafetyApi`'s own KDoc says it is *"something the app should have prevented on
     * the safety screen, not something to surface mid-emergency"*. SCR-DA-032 warns about it before
     * the alarm is armed; this copy is what a driver sees if it slipped through anyway, and it says
     * that the alert still went to the operators.
     */
    @StringRes
    private fun safetyCode(code: ErrorCode?): Int? = when (code) {
        ErrorCode.NO_EMERGENCY_CONTACT -> R.string.error_no_emergency_contact

        // A call or an alarm raised against a ride that has already ended. On SCR-DA-031 this is
        // what makes the direct-dial fallback wrong advice — there is nobody left to reach.
        ErrorCode.RIDE_TERMINAL -> R.string.error_ride_terminal

        // Somebody else's ride. Reachable only from a stale screen or a stale deep link.
        ErrorCode.NOT_RIDE_PARTICIPANT -> R.string.error_not_ride_participant

        else -> null
    }
}

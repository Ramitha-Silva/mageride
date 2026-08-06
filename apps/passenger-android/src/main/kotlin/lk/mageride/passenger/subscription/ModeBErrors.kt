package lk.mageride.passenger.subscription

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Cluster 6's code table — the Mode B access request, the subscription and its money.
 *
 * **D-26 again: never a `ProblemDetails` title or detail.** Every arm is a code
 * `subscription.yaml` declares on one of the six operations this cluster reaches — the access
 * request, the subscription list, the unsubscribe, the pay, the slip upload and the payment
 * history. C079's `BookingErrors` and C080's `RideErrors` are the other two tables and stay
 * separate for the reason theirs give: one `when` over the whole platform is a function nobody can
 * check against a contract.
 */
internal object ModeBErrors {

    /** The string resource for [cause], falling back to the shell's generic message. */
    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    @StringRes
    @Suppress("ReturnCount", "CyclomaticComplexMethod") // One arm per declared code.
    private fun forCode(code: ErrorCode?): Int = when (code) {
        // AL-49 / BR-31.1's gate, and the one refusal on this screen that is nobody's fault but
        // the fleet's: `payTo` is served ONLY from a verified payout profile, so until an officer
        // approves the owner's bank details there is nowhere to send the money. The passenger is
        // told to pay their collector rather than left staring at a failed sheet.
        ErrorCode.PAYOUT_PROFILE_NOT_VERIFIED -> R.string.error_payout_not_verified

        // The Vehicle ID was typed, and no vehicle has it. The wireframe's drawer entry is the
        // only path where this is reachable — a marker tap carries an id the map just drew.
        ErrorCode.VEHICLE_NOT_FOUND -> R.string.error_vehicle_not_found

        // A request is already open for this vehicle, or the passenger is already subscribed to
        // it. Both are "you have already done this", and neither is worth a retry.
        ErrorCode.CONFLICT -> R.string.error_request_already_open

        // The subscription, the payment or the vehicle is gone — most often a subscription the
        // owner deleted from their roster after this passenger unsubscribed (US-4.12).
        ErrorCode.NOT_FOUND -> R.string.error_subscription_not_found

        // Somebody else's subscription. Reachable only from a stale list or a bad deep link.
        ErrorCode.FORBIDDEN, ErrorCode.NOT_OWNER -> R.string.error_not_your_subscription

        // The transfer screenshot is bigger than the gateway accepts.
        ErrorCode.PAYLOAD_TOO_LARGE -> R.string.error_slip_too_large

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        ErrorCode.GATEWAY_ERROR -> R.string.error_gateway

        ErrorCode.DEPENDENCY_UNAVAILABLE -> R.string.error_dependency_unavailable

        else -> R.string.error_generic
    }
}

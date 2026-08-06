package lk.mageride.passenger.ride

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Cluster 4's code table — the active ride and its money.
 *
 * **D-26 again: never a `ProblemDetails` title or detail.** Every arm is a code one of the five
 * contracts this cluster reaches declares. C079's `BookingErrors` is the booking set and stays
 * separate for the reason its own KDoc gives.
 */
internal object RideErrors {

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
        // AL-57's 402. The rail is refused and cash and driver-QR stay on the screen — never a
        // silent fallback to cash, which `fare.yaml` calls out in as many words.
        ErrorCode.INSUFFICIENT_WALLET -> R.string.error_insufficient_wallet

        // The ride has already settled. Nothing to pay, and the screen should be showing a receipt.
        ErrorCode.PAYMENT_ALREADY_SETTLED -> R.string.error_already_settled

        // The state machine refused the move — a cancel after the driver arrived, a pay before the
        // ride completed. The screen re-reads rather than insisting.
        ErrorCode.ILLEGAL_TRANSITION -> R.string.error_ride_moved_on

        // Somebody else changed the ride between the read and the write (R-03's optimistic
        // concurrency). Same answer: re-read.
        ErrorCode.VERSION_CONFLICT -> R.string.error_ride_moved_on

        ErrorCode.NOT_RIDE_PARTICIPANT -> R.string.error_not_your_ride

        ErrorCode.PAYMENT_METHOD_INVALID -> R.string.error_payment_method_invalid

        ErrorCode.GATEWAY_ERROR -> R.string.error_gateway

        ErrorCode.ATTESTATION_FAILED -> R.string.error_generic

        ErrorCode.NOT_FOUND -> R.string.error_ride_not_found

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        ErrorCode.DEPENDENCY_UNAVAILABLE -> R.string.error_dependency_unavailable

        else -> R.string.error_generic
    }
}

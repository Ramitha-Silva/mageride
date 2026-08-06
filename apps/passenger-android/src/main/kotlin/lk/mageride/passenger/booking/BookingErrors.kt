package lk.mageride.passenger.booking

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode

/**
 * Cluster 3's code table — booking, proxy, package and schedule.
 *
 * **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
 * `code` is the key and the copy is `strings.xml`'s, in all three languages.
 *
 * Every arm below is a code one of this cluster's six contracts declares — `fare.yaml`'s estimate,
 * `ride.yaml`'s request and the three location-request operations, `transit.yaml`'s options,
 * route and parse-maps-link, and `dispatch.yaml`'s schedule. C077's `OnboardingErrors` is the
 * first-run set and stays separate for the reason its own KDoc gives: one `when` over the whole
 * platform is a function nobody can check against a contract.
 */
internal object BookingErrors {

    /** The string resource for [cause], falling back to the shell's generic message. */
    @StringRes
    fun messageFor(cause: Throwable): Int = when (cause) {
        is MageRideError.Network, is MageRideError.Timeout, is MageRideError.CircuitOpen ->
            R.string.error_offline

        // P-12's five-an-hour bucket seen at the gateway, before ride-svc answered a kebab code.
        is MageRideError.RateLimited -> R.string.error_loc_request_rate_limited

        is MageRideError -> forCode(cause.code)

        else -> R.string.error_generic
    }

    @StringRes
    @Suppress("ReturnCount", "CyclomaticComplexMethod") // One arm per declared code; a chain reads worse.
    private fun forCode(code: ErrorCode?): Int = when (code) {
        // US-6A.10b — three post-accept cancels and booking is off for a while. The one refusal a
        // passenger has genuinely earned, so the copy says why rather than "something went wrong".
        ErrorCode.BOOKING_DISABLED -> R.string.error_booking_disabled

        // There is already a ride. The answer is not to retry — it is to go and look at it.
        ErrorCode.ACTIVE_RIDE_EXISTS -> R.string.error_active_ride_exists

        // The quote went stale between the estimate and the tap. Re-quoting is the fix and the
        // screen does it, so this is only ever seen when the re-quote also failed.
        ErrorCode.INVALID_FARE_TOKEN -> R.string.error_fare_expired

        // `cod` on a passenger ride, or a wallet method that is not enabled. Booking-time methods
        // are a narrower set than settlement-time ones (AL-22), which is what this catches.
        ErrorCode.PAYMENT_METHOD_INVALID -> R.string.error_payment_method_invalid

        // Outside an operating city. Not a parse failure and not a network failure — the platform
        // does not go there, and no amount of retrying changes that.
        ErrorCode.UNSERVICEABLE_AREA -> R.string.error_unserviceable_area

        // The router could not connect the two points. Also what `parse-maps-link` answers for a
        // link it followed and found nothing in.
        ErrorCode.ROUTE_UNAVAILABLE -> R.string.error_route_unavailable

        // P-12: 5 location requests an hour, 30 a day, per booker.
        ErrorCode.LOC_REQUEST_RATE_LIMITED -> R.string.error_loc_request_rate_limited

        // The rider's five minutes ran out, or they already answered. SCR-PA-011 shows this and
        // then has nothing left to do — the request is gone.
        ErrorCode.TOKEN_EXPIRED_OR_REVOKED -> R.string.error_request_expired

        ErrorCode.NOT_FOUND -> R.string.error_request_expired

        // The number is not a `+947XXXXXXXX`. Reachable from a contact picker, which returns
        // whatever the phonebook holds — including landlines and foreign numbers.
        ErrorCode.INVALID_PHONE -> R.string.error_phone_invalid

        ErrorCode.VALIDATION_FAILED -> R.string.error_validation_failed

        ErrorCode.DEPENDENCY_UNAVAILABLE -> R.string.error_dependency_unavailable

        else -> R.string.error_generic
    }
}

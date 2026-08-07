import Foundation
import MageRideShared

/// Cluster 3's code table — booking, proxy, package and schedule.
///
/// **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
/// `code` is the key and the copy is `Localizable.strings`', in all three languages.
///
/// Every arm below is a code one of this cluster's six contracts declares — `fare.yaml`'s estimate,
/// `ride.yaml`'s request and the three location-request operations, `transit.yaml`'s options, route
/// and parse-maps-link, and `dispatch.yaml`'s schedule. ``OnboardingErrors`` is the first-run set and
/// stays separate for the reason its own note gives: one `switch` over the whole platform is a
/// function nobody can check against a contract.
///
/// The unwrap is ``OnboardingErrors/kotlinCause(of:)``'s, reused rather than copied — a Kotlin
/// exception does not cross the bridge as itself, and there is one right answer to that.
enum BookingErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else {
            return "error_generic"
        }

        switch failure {
        // The transport-level failures first: none of them carries a kebab code, because the socket
        // or the gateway refused before the service was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        // P-12's five-an-hour bucket seen at the gateway, before ride-svc answered a kebab code.
        case is MageRideErrorRateLimited:
            return "error_loc_request_rate_limited"

        default:
            return key(for: failure.code)
        }
    }

    /// One arm per code this cluster's six contracts declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // US-6A.10b — three post-accept cancels and booking is off for a while. The one refusal a
        // passenger has genuinely earned, so the copy says why rather than "something went wrong".
        case ErrorCode.bookingDisabled: return "error_booking_disabled"

        // There is already a ride. The answer is not to retry — it is to go and look at it.
        case ErrorCode.activeRideExists: return "error_active_ride_exists"

        // The quote went stale between the estimate and the tap. Re-quoting is the fix and the
        // screen does it, so this is only ever seen when the re-quote also failed.
        case ErrorCode.invalidFareToken: return "error_fare_expired"

        // `cod` on a passenger ride, or a rail that is not enabled. Booking-time methods are a
        // narrower set than settlement-time ones (AL-22), which is what this catches.
        case ErrorCode.paymentMethodInvalid: return "error_payment_method_invalid"

        // Outside an operating city. Not a parse failure and not a network failure — the platform
        // does not go there, and no amount of retrying changes that.
        case ErrorCode.unserviceableArea: return "error_unserviceable_area"

        // The router could not connect the two points. Also what `parse-maps-link` answers for a
        // link it followed and found nothing in.
        case ErrorCode.routeUnavailable: return "error_route_unavailable"

        // P-12: 5 location requests an hour, 30 a day, per booker.
        case ErrorCode.locRequestRateLimited: return "error_loc_request_rate_limited"

        // The rider's five minutes ran out, or they already answered. SCR-PI-011 shows this and
        // then has nothing left to do — the request is gone.
        case ErrorCode.tokenExpiredOrRevoked, ErrorCode.notFound: return "error_request_expired"

        // The number is not a `+947XXXXXXXX`. Reachable from a contact picker, which returns
        // whatever the phonebook holds — including landlines and foreign numbers.
        case ErrorCode.invalidPhone: return "error_phone_invalid"

        case ErrorCode.validationFailed: return "error_validation_failed"

        case ErrorCode.dependencyUnavailable: return "error_dependency_unavailable"

        default: return "error_generic"
        }
    }
}

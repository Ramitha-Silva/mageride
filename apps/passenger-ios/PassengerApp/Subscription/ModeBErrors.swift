import Foundation
import MageRideShared

/// Cluster 6's code table — the Mode B access request, the subscription and its money.
///
/// **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
/// `code` is the key and the copy is `Localizable.strings`', in all three languages.
///
/// Every arm below is a code `subscription.yaml` declares on one of the six operations this cluster
/// reaches — the access request, the subscription list, the unsubscribe, the pay, the slip upload and
/// the payment history. ``BookingErrors``, ``RideErrors`` and ``OnboardingErrors`` are the other three
/// tables and stay separate for the reason theirs give: one `switch` over the whole platform is a
/// function nobody can check against a contract.
///
/// The unwrap is ``OnboardingErrors/kotlinCause(of:)``'s, reused rather than copied — a Kotlin
/// exception does not cross the bridge as itself, and there is one right answer to that.
enum ModeBErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else {
            return "error_generic"
        }

        switch failure {
        // The transport-level failures first: none carries a kebab code, because the socket or the
        // gateway refused before subscription-svc was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        default:
            return key(for: failure.code)
        }
    }

    /// One arm per code this cluster's operations declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // AL-49 / BR-31.1's gate, and the one refusal in this cluster that is nobody's fault but the
        // fleet's: `payTo` is served ONLY from a verified payout profile, so until an officer approves
        // the owner's bank details there is nowhere to send the money. The passenger is told to pay
        // their collector rather than left staring at a failed sheet.
        case ErrorCode.payoutProfileNotVerified: return "error_payout_not_verified"

        // The Vehicle ID was typed, and no vehicle has it. The Menu tab's *"Private transport"* row is
        // the only path where this is reachable — a marker tap carries an id the map just drew.
        case ErrorCode.vehicleNotFound: return "error_vehicle_not_found"

        // A request is already open for this vehicle, or the passenger is already subscribed to it.
        // Both are "you have already done this", and neither is worth a retry.
        case ErrorCode.conflict: return "error_request_already_open"

        // The subscription, the payment or the vehicle is gone — most often a subscription the owner
        // deleted from their roster after this passenger unsubscribed (US-4.12).
        case ErrorCode.notFound: return "error_subscription_not_found"

        // Somebody else's subscription. Reachable only from a stale list or a bad deep link.
        case ErrorCode.forbidden, ErrorCode.notOwner: return "error_not_your_subscription"

        // The transfer screenshot is bigger than the gateway accepts.
        case ErrorCode.payloadTooLarge: return "error_slip_too_large"

        case ErrorCode.validationFailed: return "error_validation_failed"

        case ErrorCode.gatewayError: return "error_gateway"

        case ErrorCode.dependencyUnavailable: return "error_dependency_unavailable"

        default: return "error_generic"
        }
    }
}

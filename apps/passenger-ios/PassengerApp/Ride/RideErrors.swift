import Foundation
import MageRideShared

/// Cluster 4's code table — the active ride and its money.
///
/// **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
/// `code` is the key and the copy is `Localizable.strings`', in all three languages.
///
/// Every arm below is a code one of this cluster's four contracts declares — `ride.yaml`'s read and
/// cancel, `fare.yaml`'s five payment operations, `wallet.yaml`'s balance and `comms.yaml`'s call.
/// ``BookingErrors`` is cluster 3's set and ``OnboardingErrors`` the first-run set, and both stay
/// separate for the reason their own notes give: one `switch` over the whole platform is a function
/// nobody can check against a contract.
///
/// The unwrap is ``OnboardingErrors/kotlinCause(of:)``'s, reused rather than copied — a Kotlin
/// exception does not cross the bridge as itself, and there is one right answer to that.
enum RideErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else {
            return "error_generic"
        }

        switch failure {
        // The transport-level failures first: none carries a kebab code, because the socket or the
        // gateway refused before the service was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        default:
            return key(for: failure.code)
        }
    }

    /// One arm per code this cluster's four contracts declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // AL-57's 402. The rail is refused and **cash and driver-QR stay on the screen** — never a
        // silent fallback to cash, which `fare.yaml` calls out in as many words.
        case ErrorCode.insufficientWallet: return "error_insufficient_wallet"

        // The ride has already settled. Nothing to pay, and the screen should be showing a receipt.
        case ErrorCode.paymentAlreadySettled: return "error_already_settled"

        // The state machine refused the move — a cancel after the driver arrived, a pay before the
        // ride completed. The screen re-reads rather than insisting.
        case ErrorCode.illegalTransition: return "error_ride_moved_on"

        // Somebody else changed the ride between the read and the write (R-03's optimistic
        // concurrency). Same answer: re-read.
        case ErrorCode.versionConflict: return "error_ride_moved_on"

        case ErrorCode.notRideParticipant: return "error_not_your_ride"

        case ErrorCode.paymentMethodInvalid: return "error_payment_method_invalid"

        case ErrorCode.gatewayError: return "error_gateway"

        // No surviving ride rail has a gateway (AL-57/AL-59), so this is reachable only through a
        // dependency of fare-svc's own. Generic is the honest answer: there is nothing a passenger
        // can do differently, and cash is still on the screen.
        case ErrorCode.attestationFailed: return "error_generic"

        case ErrorCode.notFound: return "error_ride_not_found"

        case ErrorCode.validationFailed: return "error_validation_failed"

        case ErrorCode.dependencyUnavailable: return "error_dependency_unavailable"

        default: return "error_generic"
        }
    }
}

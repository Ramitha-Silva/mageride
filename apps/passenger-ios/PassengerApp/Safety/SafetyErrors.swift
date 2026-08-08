import Foundation
import MageRideShared

/// Cluster 8's safety half — the codes `safety.yaml` declares on the two operations SCR-PI-029
/// reaches.
///
/// **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
/// `code` is the key and the copy is `Localizable.strings`', in all three languages.
///
/// ``OnboardingErrors``, ``BookingErrors``, ``RideErrors``, ``ModeBErrors``, ``SettingsErrors`` and
/// ``SupportErrors`` are the other six tables and each stays separate for the reason its own note
/// gives: one `switch` over the whole platform is a function nobody can check against a contract.
/// The comms half of this cluster has no table at all — SCR-PI-028 reports a ``VoipFailure`` rather
/// than a code, because voip-svc's failure and the media engine's are the same sentence to a
/// passenger and only the *cause* changes the advice.
///
/// **`no-emergency-contact` is a setup failure, and the copy says what to do about it.** It is the
/// one code on this surface a passenger can fix themselves, and the fix is SCR-PI-027b — which is
/// also where SCR-PI-029 sends them from the empty-contacts state, *before* an emergency rather than
/// during one.
///
/// The unwrap is ``OnboardingErrors/kotlinCause(of:)``'s, reused rather than copied — a Kotlin
/// exception does not cross the bridge as itself, and there is one right answer to that.
enum SafetyErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else {
            return "error_generic"
        }

        switch failure {
        // The transport-level failures first: none carries a kebab code, because the socket or the
        // gateway refused before safety-svc was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        default:
            return key(for: failure.code)
        }
    }

    /// One arm per code this cluster's safety operations declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // AL-13. `POST /v1/sos` refuses outright when the account has nobody on file, so the alert
        // is not raised at all — which is why SCR-PI-029 warns about it while the disc is still
        // armed rather than waiting for the refusal.
        case ErrorCode.noEmergencyContact: return "error_no_emergency_contact"

        // The ride ended between the tap and the request. A share link has nothing left to follow
        // (D-34's window is trip end + 1 h and this one never opened).
        case ErrorCode.rideTerminal: return "error_ride_ended"

        case ErrorCode.notRideParticipant, ErrorCode.forbidden: return "error_not_your_ride"

        case ErrorCode.notFound: return "error_ride_not_found"

        case ErrorCode.validationFailed: return "error_validation_failed"

        case ErrorCode.dependencyUnavailable: return "error_dependency_unavailable"

        // D-30. The handset could not attest, and `POST /v1/sos` is one of the twenty attested
        // operations — nothing the passenger can act on, so it reads as a plain failure.
        case ErrorCode.attestationFailed: return "error_generic"

        default: return "error_generic"
        }
    }
}

import Foundation
import MageRideShared

/// Cluster 8's support half — the codes `support.yaml` declares.
///
/// **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
/// `code` is the key and the copy is `Localizable.strings`', in all three languages.
///
/// A small surface: the FAQ pair answers `unauthorized` / `not-found`, the ticket read adds
/// `forbidden`, `POST /v1/support/tickets` adds `validation-failed` (which is what a
/// `screenshotFileId` belonging to somebody else comes back as), and the upload adds
/// `payload-too-large`.
///
/// Separate from ``SafetyErrors`` because they are separate contracts — one `switch` over the whole
/// platform is a table nobody can check against anything. That is the same argument
/// ``OnboardingErrors``, ``BookingErrors``, ``RideErrors``, ``ModeBErrors`` and ``SettingsErrors``
/// each make; this is the seventh and last.
///
/// The unwrap is ``OnboardingErrors/kotlinCause(of:)``'s, reused rather than copied — a Kotlin
/// exception does not cross the bridge as itself, and there is one right answer to that.
enum SupportErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else {
            return "error_generic"
        }

        switch failure {
        // The transport-level failures first: none carries a kebab code, because the socket or the
        // gateway refused before support-svc was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        default:
            return key(for: failure.code)
        }
    }

    /// One arm per code this cluster's support operations declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // The screenshot upload's own ceiling. The ticket is unaffected — see ``SupportModel``,
        // which submits without the attachment rather than losing what the passenger wrote.
        case ErrorCode.payloadTooLarge: return "error_screenshot_too_large"

        // A ticket, an article or an attachment that is not there. `getSupportScreenshot` answers
        // `403` for an unknown id on purpose, so a forged link tells its author nothing — which is
        // why the two codes share one sentence.
        case ErrorCode.notFound, ErrorCode.forbidden: return "error_ticket_not_found"

        case ErrorCode.validationFailed: return "error_validation_failed"

        case ErrorCode.dependencyUnavailable: return "error_dependency_unavailable"

        default: return "error_generic"
        }
    }
}

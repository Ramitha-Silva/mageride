import Foundation
import MageRideShared

/// Cluster 7's code table — the address book, the profile, its two preferences and the SOS list.
///
/// **D-26 again: never a `ProblemDetails` title or detail.** Those are operator English; the kebab
/// `code` is the key and the copy is `Localizable.strings`', in all three languages.
///
/// Every arm below is a code `iam.yaml` declares on one of the eleven operations this cluster
/// reaches. ``OnboardingErrors``, ``BookingErrors``, ``RideErrors`` and ``ModeBErrors`` are the
/// other four tables and stay separate for the reason each of theirs gives: one `switch` over the
/// whole platform is a function nobody can check against a contract.
///
/// `GET /v1/geo/reverse` is deliberately absent. It is the one call in this cluster whose failure is
/// not shown — ``AddressBook/describe(_:)`` answers `nil` and the sheet opens with empty lines,
/// because a geocoder that cannot name a coordinate has not stopped the passenger saving it.
///
/// The unwrap is ``OnboardingErrors/kotlinCause(of:)``'s, reused rather than copied — a Kotlin
/// exception does not cross the bridge as itself, and there is one right answer to that.
enum SettingsErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError else {
            return "error_generic"
        }

        switch failure {
        // The transport-level failures first: none carries a kebab code, because the socket or the
        // gateway refused before iam-svc was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        default:
            return key(for: failure.code)
        }
    }

    /// The same, for `DELETE /v1/users/me` — where a `409` means something else entirely.
    ///
    /// **One code, two meanings, and they are not interchangeable.** A conflict on a saved address
    /// is *"you already have a Home"*; a conflict on the erasure request is *"one is already open"*
    /// (E-06). A single arm would have to pick one of those sentences and be wrong on the other
    /// screen, so the delete path names its own rather than the table guessing from a code.
    static func deletionMessageKey(for error: Error) -> String {
        guard
            let failure = OnboardingErrors.kotlinCause(of: error) as? MageRideError,
            let code = failure.code
        else {
            return messageKey(for: error)
        }

        switch code {
        case ErrorCode.conflict: return "settings_delete_already_requested"
        default: return messageKey(for: error)
        }
    }

    /// One arm per code this cluster's operations declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // `POST /v1/me/saved-addresses` — the C003 partial unique indexes refuse a **second** Home
        // or Work. Reachable only from a stale list: the screen edits the row it already has.
        case ErrorCode.conflict: return "error_address_shortcut_taken"

        // The address or the contact is gone — deleted on a second handset, most often.
        case ErrorCode.notFound: return "error_address_not_found"

        // `POST` / `PUT /v1/me/emergency-contacts` on a number iam-svc cannot parse as E.164.
        case ErrorCode.invalidPhone: return "error_phone_invalid"

        case ErrorCode.validationFailed: return "error_validation_failed"

        case ErrorCode.dependencyUnavailable: return "error_dependency_unavailable"

        default: return "error_generic"
        }
    }
}

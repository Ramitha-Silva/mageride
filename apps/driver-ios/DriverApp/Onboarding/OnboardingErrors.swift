import Foundation
import MageRideShared

/// Turns a failure into copy a driver can read, in their own language.
///
/// **D-26: an app never renders `title`, `detail` or `message` from a `ProblemDetails`.** Those are
/// English strings written for an operator, and putting one on a Sinhala screen is how a trilingual
/// app becomes an English one at exactly the moment it matters. The kebab `code` is the key; the
/// copy is `Localizable.strings`', in all three languages.
///
/// **Cluster 1's codes only.** The Android twin carries the whole app's table because C068 shipped
/// after every other Android screen group had named its codes; here the dashboard, the wallet and
/// the safety clusters have not landed, and a key with no renderer is a translation nobody can
/// check. C087–C093 extend this the way they extend the strings files — one `case` and three
/// values, together.
enum OnboardingErrors {

    /// The string key for [error], falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = kotlinCause(of: error) as? MageRideError else { return "error_generic" }

        switch failure {
        // A document too large for the gateway. Its own message, because "try again" is wrong
        // advice for a photograph that will be the same size next time.
        case is MageRideErrorPayloadTooLarge:
            return "error_image_too_large"

        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        case is MageRideErrorRateLimited:
            return "error_otp_rate_limited"

        default:
            return key(for: failure.code)
        }
    }

    /// The Kotlin throwable behind a Swift `Error`.
    ///
    /// **A Kotlin exception does not cross the bridge as itself.** Kotlin/Native wraps whatever a
    /// suspend function threw in an `NSError` and puts the original under `KotlinException`; a
    /// `catch let error as MageRideError` therefore never matches, and every failure in the app
    /// would resolve to the generic message. This is the unwrap, written once — reach for it in any
    /// screen group that has to tell one server failure from another.
    static func kotlinCause(of error: Error) -> Any {
        (error as NSError).userInfo["KotlinException"] ?? error
    }

    /// The code table for the five cluster-1 screens.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        case ErrorCode.invalidOtp: return "error_otp_invalid"

        case ErrorCode.otpExpired: return "error_otp_expired"

        case ErrorCode.otpLocked: return "error_otp_locked"

        case ErrorCode.otpRateLimited: return "error_otp_rate_limited"

        // AL-08's single-active-device rule seen from the login side: the OTP was requested from
        // one install and verified from another, which is a different sign-in attempt entirely.
        case ErrorCode.deviceMismatch: return "error_device_mismatch"

        case ErrorCode.userBlocked: return "error_user_blocked"

        case ErrorCode.validationFailed: return "error_validation_failed"

        default: return "error_generic"
        }
    }
}

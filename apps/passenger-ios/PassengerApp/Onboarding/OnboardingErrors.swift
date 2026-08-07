import Foundation
import MageRideShared

/// Turns a failure into copy a passenger can read, in their own language.
///
/// **D-26: an app never renders `title`, `detail` or `message` from a `ProblemDetails`.** Those are
/// English strings written for an operator, and putting one on a Sinhala screen is how a trilingual
/// app becomes an English one at exactly the moment it matters. The kebab `code` is the key; the
/// copy is `Localizable.strings`', in all three languages.
///
/// **This is the first-run set only.** Every code below is one `iam.yaml` declares on the operations
/// SCR-PI-003 and SCR-PI-004 can reach — the OTP request, the resend, the verify and the profile
/// save. C096–C102 add their own arms for the codes their contracts declare; one `switch` over the
/// whole platform would be a function nobody can check against a contract, which is the only way a
/// table like this stays true. (The driver app's grew that way, one cluster at a time.)
enum OnboardingErrors {

    /// The string key for `error`, falling back to the shell's generic message.
    static func messageKey(for error: Error) -> String {
        guard let failure = kotlinCause(of: error) as? MageRideError else { return "error_generic" }

        switch failure {
        // The transport-level failures first: none of them carries a kebab code, because the socket
        // or the gateway refused before iam-svc was reached.
        case is MageRideErrorNetwork, is MageRideErrorTimeout, is MageRideErrorCircuitOpen:
            return "error_offline"

        // D-32's bucket seen at the transport layer.
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
    /// would resolve to the generic message. This is the unwrap, written once — C086 found it on the
    /// driver side and it is the first thing to reach for in any screen group here that has to tell
    /// one server failure from another.
    static func kotlinCause(of error: Error) -> Any {
        (error as NSError).userInfo["KotlinException"] ?? error
    }

    /// One arm per code the first-run operations declare.
    private static func key(for code: ErrorCode?) -> String {
        guard let code else { return "error_generic" }

        switch code {
        // The six digits were wrong. The attempt survives with one fewer try — the screen says how
        // many are left, which is what stops a passenger burning the last one guessing.
        case ErrorCode.invalidOtp: return "error_otp_invalid"

        // The five-minute window closed. A new code is the only way forward, so the copy says so.
        case ErrorCode.otpExpired: return "error_otp_expired"

        // Out of attempts. `AuthSessionManager` has already dropped back to `SignedOut`, so the
        // screen returns to the number — there is no seventh box to offer.
        case ErrorCode.otpLocked: return "error_otp_locked"

        // D-32: five requests an hour. Not the same as `otpLocked` — the code was never wrong.
        case ErrorCode.otpRateLimited: return "error_otp_rate_limited"

        // The number itself is not a `+947XXXXXXXX`. Reachable in practice only from a paste that
        // survived `PhoneNumber.normalise`, which is why the copy names the format.
        case ErrorCode.invalidPhone: return "error_phone_invalid"

        // AL-08's single-active-device rule seen from the login side: the OTP was requested from one
        // install and verified from another, which is a different sign-in attempt entirely.
        case ErrorCode.deviceMismatch: return "error_device_mismatch"

        // The `authId` is gone — a verify against an attempt the server has already retired. Same
        // copy as an expiry, because that is what it is from the passenger's side.
        case ErrorCode.authNotFound: return "error_otp_expired"

        case ErrorCode.userBlocked: return "error_user_blocked"

        case ErrorCode.validationFailed: return "error_validation_failed"

        default: return "error_generic"
        }
    }
}

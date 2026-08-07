import Foundation
import MageRideShared

/// Turns a failure into copy a driver can read, in their own language.
///
/// **D-26: an app never renders `title`, `detail` or `message` from a `ProblemDetails`.** Those are
/// English strings written for an operator, and putting one on a Sinhala screen is how a trilingual
/// app becomes an English one at exactly the moment it matters. The kebab `code` is the key; the
/// copy is `Localizable.strings`', in all three languages.
///
/// **Every code this app's screens can reach**, which is now the whole table: C093 added the safety
/// and support cluster's, which is what the note here previously asked for. A code with no renderer
/// is still deliberately absent — a translation nobody can check is worse than the generic message.
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

    /// The code table for the five cluster-1 screens and cluster 2's four.
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

        // ---- C087 · the Mode-C wizard (AL-27, D-37) ----------------------------------
        //
        // `registration-exists` is resolved here as well as inline on Step 1/4's plate field: the
        // wizard renders it beside the one field that has to change, and anything else reaching this
        // code — a resume that races another handset's registration — still gets copy rather than
        // the generic message.

        case ErrorCode.registrationExists: return "error_registration_exists"

        // AL-27's fence, seen from the server: a bus, a school van or a route permit is the Fleet
        // Portal's, and this app cannot register one.
        case ErrorCode.modeNotAllowed: return "error_mode_not_allowed"

        case ErrorCode.notOwner: return "error_not_owner"

        case ErrorCode.vehicleNotFound: return "error_vehicle_not_found"

        // ---- C088 · the dashboard, the offer and the ride ----------------------------
        //
        // `offer-already-accepted` and `offer-expired` are deliberately absent: `OfferSession` keeps
        // them apart as `OfferOutcome.Taken` / `.Expired` all the way out, and SCR-DI-014 renders
        // each with its own copy. Resolving them here as well would give one failure two messages
        // and lose the distinction the server went to the trouble of making.

        // US-9.6, seen from the server: the toggle was live and the vehicle was not.
        case ErrorCode.vehicleNotApproved: return "error_vehicle_not_approved"

        // D-03's single-publisher mutex. A ride or a Mode A/B session is already running — usually
        // on another handset, which is exactly what the driver needs told.
        case ErrorCode.driverAlreadyLive: return "error_driver_already_live"

        // DT-01: a Directional filter is a standby filter, and there is no standby to filter.
        case ErrorCode.notOnline: return "error_not_online"

        case ErrorCode.directionalLimitReached: return "error_directional_limit"

        // D-08's daily-fee gate, not a dispatch failure — the driver can act on it (US-9.1).
        case ErrorCode.insufficientWallet: return "error_insufficient_wallet"

        // R-14. Somebody else moved the ride; the answer is to re-read and decide again, never to
        // bump the version and retry.
        case ErrorCode.versionConflict: return "error_version_conflict"

        // ADD Appendix B.2's table refused the move — a passenger who cancelled while the driver
        // was tapping, most often.
        case ErrorCode.illegalTransition: return "error_illegal_transition"

        case ErrorCode.rideTerminal: return "error_ride_terminal"

        // ---- C091 · the wallet (SCR-DI-021…025) --------------------------------------
        //
        // `insufficient-wallet` is above and is deliberately not repeated: C088 named it for the
        // daily-fee gate on an accept and it means the same thing on a transfer approval — the wallet
        // is short — which is advice a driver can act on in both readings.

        // Below a gateway's floor, above the field's ceiling, or a voucher denomination that is not a
        // tier — `POST /v1/vouchers/purchase` refuses an amount between tiers rather than rounding it,
        // because interpolating one would invent a rate no admin set.
        case ErrorCode.invalidAmount: return "error_invalid_amount"

        // OnePay or the bank IPG did not answer. Nothing was charged and nothing was credited.
        case ErrorCode.gatewayError: return "error_gateway_error"

        // Approving a request somebody already answered, or a top-up session that has moved on.
        case ErrorCode.conflict: return "error_already_done"

        case ErrorCode.notFound: return "error_not_found"

        // ---- C093 · the call, the alarm and support ----------------------------------
        //
        // `ride-terminal` is already above — C088 named it for a command on a ride that had ended,
        // and it means the same thing to SCR-DI-031: there is nobody left to call. That is also why
        // `VoipCallState.canDialDirectly` stays false on it (a terminal ride carries no
        // `counterpartyPhone`), so the copy and the offered action agree.

        // AL-13, seen from safety-svc: the alarm **was** recorded and reached the admin live feed;
        // the SMS leg had nowhere to go. Deliberately not phrased as a failure — `SosSmsStatus`
        // makes the same distinction in the success body, and this is the 400 arm of it.
        case ErrorCode.noEmergencyContact: return "error_no_emergency_contact"

        // A call or an alarm raised against a ride this driver is not on — a stale takeover left
        // open across a reassignment, most often.
        case ErrorCode.notRideParticipant: return "error_not_ride_participant"

        default: return "error_generic"
        }
    }
}

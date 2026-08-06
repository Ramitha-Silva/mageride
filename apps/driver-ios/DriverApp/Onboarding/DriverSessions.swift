import Foundation
import MageRideShared

/// An OTP attempt in flight, as SCR-DI-003 needs it.
///
/// A Swift value rather than `:shared`'s `OtpChallenge`, for one concrete reason: that type carries
/// a `kotlin.time.Instant`, which the Objective-C export flattens into an opaque object a countdown
/// cannot subtract from. The conversion happens once, in ``SharedDriverSessions``, and the screen
/// works in `Date`.
///
/// - Parameters:
///   - attemptsRemaining: Entries left before `423 otp-locked`.
///   - resendAllowedAt: When a resend stops being refused locally (D-32).
///   - isBlocked: The number is blocked outright. The server still answers `200` so the screen
///     cannot enumerate blocked numbers by timing; no code will arrive.
struct LoginChallenge: Equatable {
    let attemptsRemaining: Int
    let resendAllowedAt: Date
    let isBlocked: Bool
}

/// The half of C014's session manager SCR-DI-003 uses.
///
/// **A protocol because `AuthSessionManager` is a Kotlin `class`.** Swift can implement an exported
/// Kotlin *interface*, but not stand in for a class — so a login-screen test with no gateway needs
/// a seam on this side of the bridge. It is deliberately five methods and one flag: everything
/// about tokens, the device binding and the single-active-device rule is C014's, and a screen that
/// could reach further would be a screen that could hold a bearer token.
protocol DriverSessions: AnyObject {

    /// Reads the stored session at cold start. Call once, before the first screen.
    func restore() async

    /// Whether a session for this surface is in hand (AL-08).
    var isSignedIn: Bool { get }

    /// The attempt in flight, or `nil` when there is none. Read after a failed verify to tell a
    /// wrong digit from a dead attempt.
    var awaitingChallenge: LoginChallenge? { get }

    /// `POST /v1/auth/otp/request`.
    func requestOtp(phone: String, pushToken: String?) async throws -> LoginChallenge

    /// `POST /v1/auth/otp/resend`.
    func resendOtp() async throws -> LoginChallenge

    /// `POST /v1/auth/otp/verify`.
    func verifyOtp(_ code: String) async throws

    /// Abandons the attempt without leaving the screen — the wireframe's `‹ Back` from the code
    /// half to the number.
    func cancelOtp() async
}

/// ``DriverSessions`` over C014's `AuthSessionManager`.
///
/// Nothing here decides anything. It converts two Kotlin shapes into Swift ones — an `Instant` into
/// a `Date`, a `SessionState` into a flag — and forwards; every rule about what a session is stays
/// on the Kotlin side, where both apps read the same one.
final class SharedDriverSessions: DriverSessions {

    private let sessions: AuthSessionManager

    init(sessions: AuthSessionManager) {
        self.sessions = sessions
    }

    func restore() async {
        try? await sessions.restore()
    }

    var isSignedIn: Bool { sessions.state.value is SessionStateSignedIn }

    var awaitingChallenge: LoginChallenge? {
        (sessions.state.value as? SessionStateAwaitingOtp).map { Self.challenge($0.challenge) }
    }

    func requestOtp(phone: String, pushToken: String?) async throws -> LoginChallenge {
        Self.challenge(try await sessions.requestOtp(phone: phone, fcmToken: pushToken))
    }

    func resendOtp() async throws -> LoginChallenge {
        Self.challenge(try await sessions.resendOtp())
    }

    func verifyOtp(_ code: String) async throws {
        _ = try await sessions.verifyOtp(otp: code)
    }

    func cancelOtp() async {
        try? await sessions.cancelOtp()
    }

    /// `kotlin.time.Instant` reaches Swift as an opaque object; `toEpochMilliseconds()` is the one
    /// reading the rest of this repository uses, and it is all a countdown needs.
    private static func challenge(_ challenge: OtpChallenge) -> LoginChallenge {
        let millis = challenge.resendAllowedAt.toEpochMilliseconds()
        return LoginChallenge(
            attemptsRemaining: Int(challenge.attemptsRemaining),
            resendAllowedAt: Date(timeIntervalSince1970: TimeInterval(millis) / 1000),
            isBlocked: challenge.isBlocked
        )
    }
}

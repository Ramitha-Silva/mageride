import Foundation
import MageRideShared

/// An OTP attempt in flight, as SCR-PI-003 needs it.
///
/// A Swift value rather than `:shared`'s `OtpChallenge`, for one concrete reason: that type carries
/// a `kotlin.time.Instant`, which the Objective-C export flattens into an opaque object a countdown
/// cannot subtract from. The conversion happens once, in ``SharedPassengerSessions``, and the screen
/// works in `Date`.
///
/// - Parameters:
///   - attemptsRemaining: Entries left before `423 otp-locked`.
///   - resendAllowedAt: When a resend stops being refused locally (US-1.10's 60 seconds, D-32's
///     five an hour).
///   - isBlocked: The number is blocked outright. The server still answers `200` so the screen
///     cannot enumerate blocked numbers by timing; no code will arrive.
struct LoginChallenge: Equatable {
    let attemptsRemaining: Int
    let resendAllowedAt: Date
    let isBlocked: Bool
}

/// The half of C014's session manager cluster 1 uses.
///
/// **A protocol because `AuthSessionManager` is a Kotlin `class`.** Swift can implement an exported
/// Kotlin *interface*, but not stand in for a class — so a login-screen test with no gateway needs a
/// seam on this side of the bridge. It is deliberately narrow: everything about tokens, the device
/// binding and the single-active-device rule is C014's, and a screen that could reach further would
/// be a screen that could hold a bearer token.
///
/// **Phone-OTP only** (AL-07). `IamApi` carries `signInWithGoogle`, `signInWithApple` and
/// `signInWithPassword` for the Fleet and Admin portals; none of them appears here, so no screen in
/// cluster 1 can reach one however it is written. The URD's own cost decision is the reason —
/// Firebase Phone Auth is ~Rs 90 an SMS in Sri Lanka against Rs 0.50–1.50 through a local gateway.
protocol PassengerSessions: AnyObject {

    /// Reads the stored session at cold start. Call once, before the first screen.
    func restore() async

    /// Whether a session for this surface is in hand (AL-08).
    var isSignedIn: Bool { get }

    /// The signed-in passenger's id, or `nil` when there is no session.
    ///
    /// This is the id every passenger-scoped read takes — `GET /v1/rides/passenger/{id}/active` is
    /// the one cluster 1 makes. It is `SessionState.SignedIn.userId`; nothing mints or stores one of
    /// its own, because a second id in the app would be a second answer to who is riding.
    var userId: String? { get }

    /// The attempt in flight, or `nil` when there is none. Read after a failed verify to tell a
    /// wrong digit from a dead attempt.
    var awaitingChallenge: LoginChallenge? { get }

    /// `POST /v1/auth/otp/request`.
    func requestOtp(phone: String, pushToken: String?) async throws -> LoginChallenge

    /// `POST /v1/auth/otp/resend`.
    func resendOtp() async throws -> LoginChallenge

    /// `POST /v1/auth/otp/verify`.
    func verifyOtp(_ code: String) async throws

    /// Abandons the attempt without leaving the screen — the wireframe's `‹ Back` from the code half
    /// to the number.
    func cancelOtp() async

    /// `POST /v1/auth/logout` — end this device's session (US-1.7).
    ///
    /// On the seam rather than through `IamApi`, because `AuthSessionManager` is what *holds* the
    /// session: calling the route without telling it would leave a signed-out app whose
    /// `SessionState` still said `SignedIn` until the next 401. The local half happens **whether or
    /// not the call succeeds**, and it raises `SessionEvent.RouteToLogin`, which
    /// ``PassengerShellModel`` is the single subscriber to. Nothing else navigates.
    ///
    /// Not reached from cluster 1 — SCR-PI-027's *Log out* is C101's — but it belongs on this
    /// protocol rather than on a second one, for the reason above.
    func logOut() async
}

/// ``PassengerSessions`` over C014's `AuthSessionManager`.
///
/// Nothing here decides anything. It converts two Kotlin shapes into Swift ones — an `Instant` into
/// a `Date`, a `SessionState` into a flag — and forwards; every rule about what a session is stays
/// on the Kotlin side, where all four apps read the same one.
final class SharedPassengerSessions: PassengerSessions {

    private let sessions: AuthSessionManager

    init(sessions: AuthSessionManager) {
        self.sessions = sessions
    }

    func restore() async {
        try? await sessions.restore()
    }

    var isSignedIn: Bool { sessions.state.value is SessionStateSignedIn }

    var userId: String? { (sessions.state.value as? SessionStateSignedIn)?.userId }

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

    /// `try?` because `AuthSessionManager.logout()` already swallows the gateway's half by design and
    /// only the cancellation of the calling task can reach here. There is nothing a screen could do
    /// with a failure: the local session is gone either way.
    func logOut() async {
        try? await sessions.logout()
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

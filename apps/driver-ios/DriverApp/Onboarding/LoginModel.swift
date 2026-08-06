import Foundation

/// Which half of SCR-DI-003 is live. The wireframe draws both on one screen.
enum LoginPhase {

    /// Only the `+94` field is enabled; Continue requests the code.
    case phone

    /// A code is out. The OTP cells are enabled and the resend counts down.
    case otp
}

/// SCR-DI-003's state.
///
/// - Parameters:
///   - phone: The national number, always normalised — see ``PhoneNumber``.
///   - otp: What has been typed into the six cells.
///   - isBusy: A request is in flight; the CTA shows its inline loader.
///   - errorKey: The resolved copy for the last failure, or `nil`.
///   - resendInSeconds: Seconds until Resend is offered again (D-32's 60-second bucket).
///   - attemptsRemaining: Verifies left before `423 otp-locked`, once the server has said.
struct LoginState {

    var phase: LoginPhase = .phone
    var phone: String = ""
    var otp: String = ""
    var isBusy = false
    var errorKey: String?
    var resendInSeconds: Int = 0
    var attemptsRemaining: Int?

    /// D5' §14.1 — six digits.
    static let otpLength = 6

    /// Whether the number is a complete `+947XXXXXXXX`.
    var isPhoneValid: Bool { PhoneNumber.isValid(phone) }

    /// Whether the six digits are all there.
    var isOtpComplete: Bool { otp.count == LoginState.otpLength }

    /// The CTA is live when the step it belongs to can be submitted.
    var canSubmit: Bool {
        guard !isBusy else { return false }
        return phase == .phone ? isPhoneValid : isOtpComplete
    }

    /// Resend is refused locally inside the cooldown — a call inside it spends a D-32 attempt.
    var canResend: Bool { phase == .otp && !isBusy && resendInSeconds <= 0 }
}

/// SCR-DI-003 — `+94` phone, then the SMS OTP.
///
/// **Phone-OTP only** (AL-07 / US-11.5). `IamApi` carries Google, Apple and password sign-in for
/// the Fleet and Admin portals and none of them may be reached from here; ``DriverSessions`` is the
/// only door this screen has, and it has no other. The wireframe says so on the screen because it
/// is a promise to the driver, not a note to us.
///
/// Everything about tokens, the device binding and the single-active-device rule is C014's — this
/// model owns the two things a screen owns: what is in the field, and what to say when the server
/// says no.
@MainActor
final class LoginModel: ObservableObject {

    @Published private(set) var state = LoginState()

    /// Set once the driver is through; the screen navigates and this screen is replaced.
    @Published private(set) var destination: OnboardingDestination?

    private let sessions: DriverSessions
    private let onboarding: OnboardingRepository
    private let profiles: DriverProfileRepository
    private let preferences: OnboardingPreferences
    private let pushTokens: PushTokenProvider

    private var countdown: Task<Void, Never>?

    init(
        sessions: DriverSessions,
        onboarding: OnboardingRepository,
        profiles: DriverProfileRepository,
        preferences: OnboardingPreferences,
        pushTokens: PushTokenProvider
    ) {
        self.sessions = sessions
        self.onboarding = onboarding
        self.profiles = profiles
        self.preferences = preferences
        self.pushTokens = pushTokens
    }

    deinit {
        countdown?.cancel()
    }

    /// A session restored from the Keychain means the driver never needed this screen — the shell
    /// can land on it from a `RouteToLogin`, and from a deep link on a cold start.
    func start() async {
        guard sessions.isSignedIn, destination == nil else { return }
        await finish()
    }

    func onPhoneChanged(_ input: String) {
        state.phone = PhoneNumber.normalise(input)
        state.errorKey = nil
    }

    func onOtpChanged(_ input: String) {
        state.otp = input
        state.errorKey = nil
    }

    /// Back from the OTP half to the number, without abandoning the attempt server-side.
    func editPhoneNumber() async {
        countdown?.cancel()
        await sessions.cancelOtp()
        state.phase = .phone
        state.otp = ""
        state.errorKey = nil
        state.resendInSeconds = 0
        state.attemptsRemaining = nil
    }

    /// The CTA: requests the code on the phone half, verifies it on the OTP half.
    func submit() async {
        guard state.canSubmit else { return }

        switch state.phase {
        case .phone:
            let phone = PhoneNumber.toE164(state.phone)
            await execute {
                // The push token rides along on the OTP request so the very first notification —
                // the approval push a new driver is waiting for — has somewhere to go.
                let token = await self.pushTokens.current()
                let challenge = try await self.sessions.requestOtp(phone: phone, pushToken: token)
                self.state.phase = .otp
                self.state.otp = ""
                self.apply(challenge)
            }

        case .otp:
            let code = state.otp
            await execute(onFailure: { [weak self] _ in self?.onVerifyFailed() }) {
                try await self.sessions.verifyOtp(code)
                await self.finish()
            }
        }
    }

    /// `POST /v1/auth/otp/resend`. Refused locally while ``LoginState/resendInSeconds`` is positive.
    func resend() async {
        guard state.canResend else { return }
        await execute(onFailure: { [weak self] _ in self?.state.otp = "" }) {
            self.apply(try await self.sessions.resendOtp())
        }
    }

    // MARK: -

    /// A verify that did not take.
    ///
    /// A wrong digit keeps the attempt alive with one fewer try; a **dead** attempt
    /// (`423 otp-locked`, `400 otp-expired`, `404 auth-not-found`) takes C014 back to `SignedOut`,
    /// because that `authId` can never succeed again — so the screen goes back to the number rather
    /// than offering a seventh box.
    private func onVerifyFailed() {
        let awaiting = sessions.awaitingChallenge
        state.otp = ""
        if awaiting == nil {
            state.phase = .phone
            countdown?.cancel()
            state.resendInSeconds = 0
        }
        state.attemptsRemaining = awaiting?.attemptsRemaining ?? state.attemptsRemaining
    }

    /// What happens the moment there is a bearer token.
    ///
    /// The two first-run preferences are pushed to `iam.users` here because this is the first point
    /// at which they can be (SCR-DI-002 runs signed out), and the destination is computed from the
    /// profile the server actually holds rather than from `isNewUser` — a driver who installed,
    /// signed in and killed the app before Profile Setup is not a new user and still has no profile.
    private func finish() async {
        _ = await onboarding.syncPreferences()
        destination = OnboardingRouter.next(
            signedIn: true,
            firstRunComplete: preferences.firstRunComplete,
            profileComplete: await hasProfile(),
            permissionsAcknowledged: preferences.permissionsAcknowledged
        )
    }

    /// Whether this driver already has a `registry.driver_profiles` row (US-2.21).
    ///
    /// A failure answers `false` — the opposite of the splash's choice, and for the opposite
    /// reason. Here the driver has just signed in, so the network was working a moment ago; the
    /// safe outcome is to show Profile Setup, which is idempotent (`PUT /v1/drivers/profile`) and
    /// where a brand-new driver belongs anyway.
    private func hasProfile() async -> Bool {
        do {
            let name = try await profiles.profileName()
            return !(name?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
        } catch {
            return false
        }
    }

    /// Puts a fresh challenge on screen: the attempt counter, and the countdown that gates Resend.
    ///
    /// The countdown reads ``LoginChallenge/resendAllowedAt`` on every tick rather than counting
    /// down from a number — a screen backgrounded for thirty seconds comes back with thirty seconds
    /// gone, not with the timer where it was left.
    private func apply(_ challenge: LoginChallenge) {
        state.attemptsRemaining = challenge.attemptsRemaining
        countdown?.cancel()
        // `Task { }` started from a `@MainActor` context runs on the main actor, so the assignment
        // below needs no hop of its own.
        countdown = Task { [weak self] in
            while !Task.isCancelled {
                let remaining = Int(challenge.resendAllowedAt.timeIntervalSinceNow.rounded(.up))
                self?.state.resendInSeconds = max(0, remaining)
                if remaining <= 0 { return }
                try? await Task.sleep(nanoseconds: NSEC_PER_SEC)
            }
        }
    }

    /// The one shape every call on this screen has: busy on, error cleared, error resolved on the
    /// way out.
    /// Both closures are `@MainActor` because both touch ``state``. A non-`Sendable` escaping
    /// closure written inside a main-actor method already inherits that isolation; spelling it out
    /// is what keeps it true when C103 raises `SWIFT_STRICT_CONCURRENCY`.
    private func execute(
        onFailure: @escaping @MainActor (Error) -> Void = { _ in },
        _ block: @escaping @MainActor () async throws -> Void
    ) async {
        state.isBusy = true
        state.errorKey = nil
        do {
            try await block()
        } catch is CancellationError {
            // The screen going away, not a failure the driver caused.
        } catch {
            onFailure(error)
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }
}

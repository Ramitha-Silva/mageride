import Foundation

/// Which half of SCR-PI-003 is live. The wireframe draws both on one screen.
enum LoginPhase {

    /// Only the `+94` field is enabled; Continue requests the code.
    case phone

    /// A code is out. The OTP cells are enabled and the resend counts down.
    case otp
}

/// SCR-PI-003's state.
///
/// - Parameters:
///   - phone: The national number, always normalised — see ``PhoneNumber``.
///   - otp: What has been typed into the six cells.
///   - isBusy: A request is in flight; the CTA shows its inline loader.
///   - errorKey: The resolved copy for the last failure, or `nil`.
///   - resendInSeconds: Seconds until Resend is offered again (US-1.10's 60-second cooldown).
///   - attemptsRemaining: Verifies left before `423 otp-locked`, once the server has said.
struct LoginState: Equatable {

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

    /// Resend is refused **locally** inside the cooldown.
    ///
    /// US-1.10 is a 60-second wait and D-32 caps requests at five an hour — so a tap inside the
    /// cooldown does not merely fail, it *spends* one of the five. Gating the button is what keeps a
    /// passenger who taps four times in frustration from locking themselves out for an hour, and the
    /// countdown is **shown** rather than hidden because a bare disabled "Resend" tells them nothing
    /// and they tap it until they are locked out.
    var canResend: Bool { phase == .otp && !isBusy && resendInSeconds <= 0 }
}

/// SCR-PI-003 — `+94` phone, then the SMS OTP.
///
/// **Phone-OTP only** (AL-07). The URD's own cost decision is the reason: Firebase Phone Auth is
/// ~Rs 90 an SMS in Sri Lanka against Rs 0.50–1.50 through a local gateway. `IamApi` carries Google,
/// Apple and password sign-in for the Fleet and Admin portals and **none of them may be reached from
/// here**; ``PassengerSessions`` is the only door this screen has, and it has no other.
///
/// Everything about tokens, the device binding and the single-active-device rule is C014's — this
/// model owns the two things a screen owns: what is in the field, and what to say when the server
/// says no.
@MainActor
final class LoginModel: ObservableObject {

    @Published private(set) var state = LoginState()

    /// Set once the passenger is through; the screen navigates and this screen is replaced.
    @Published private(set) var destination: PassengerDestination?

    private let sessions: PassengerSessions
    private let onboarding: OnboardingRepository
    private let profiles: PassengerProfileRepository
    private let preferences: AppPreferences
    private let pushTokens: PushTokenProvider

    private var countdown: Task<Void, Never>?

    init(
        sessions: PassengerSessions,
        onboarding: OnboardingRepository,
        profiles: PassengerProfileRepository,
        preferences: AppPreferences,
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

    /// A session restored from the Keychain means the passenger never needed this screen — the shell
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
                // The push token rides along on the OTP request so the very first notification a
                // passenger can receive — a driver assigned, a package on its way — has somewhere to
                // land before they have opened the app a second time.
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

    /// Clears the failure the alert is showing.
    ///
    /// The alert's dismissal is the only thing that calls this: the error is what makes the alert
    /// presented, so clearing it is what makes it go away. A screen that owned a second "is the
    /// alert up" flag would have two answers to one question.
    func dismissError() {
        state.errorKey = nil
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
    /// The language chosen on SCR-PI-002 is pushed to `iam.users` here because this is the first
    /// point at which it can be (that screen runs signed out), and the destination is computed from
    /// the profile the server actually holds rather than from `isNewUser` — a passenger who
    /// installed, signed in and killed the app before Profile Setup is not a new user and still has
    /// no profile.
    private func finish() async {
        await onboarding.syncPreferences()
        destination = OnboardingRouter.next(
            signedIn: true,
            firstRunComplete: preferences.firstRunComplete,
            profileComplete: await hasProfile(),
            locationAcknowledged: preferences.locationRationaleAcknowledged
        )
    }

    /// Whether this passenger already has a name on `iam.users` (US-1.5).
    ///
    /// **A failure answers `false` — the opposite of the splash's choice, and for the opposite
    /// reason.** Here the passenger has just signed in, so the network was working a moment ago; the
    /// safe outcome is to show Profile Setup, which is idempotent (`PUT /v1/users/me`) and where a
    /// brand-new passenger belongs anyway. ``SplashModel/hasProfile()`` argues the other side.
    private func hasProfile() async -> Bool {
        guard let profile = try? await profiles.me() else { return false }
        return !(profile.firstName?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true)
    }

    /// Puts a fresh challenge on screen: the attempt counter, and the countdown that gates Resend.
    ///
    /// The countdown reads ``LoginChallenge/resendAllowedAt`` on every tick rather than counting down
    /// from a number — a screen backgrounded for thirty seconds comes back with thirty seconds gone,
    /// not with the timer where it was left.
    private func apply(_ challenge: LoginChallenge) {
        state.attemptsRemaining = challenge.attemptsRemaining
        countdown?.cancel()
        // Seeded HERE, before the ticker, and not only inside it. A `Task` started from a
        // `@MainActor` context is scheduled rather than run, so between `apply` returning and the
        // first tick `resendInSeconds` was still 0 — which makes `canResend` true and lets a resend
        // reach the gateway inside the cooldown the challenge just declared. The loop below then
        // re-reads the same clock every second, which is what survives a backgrounded screen.
        state.resendInSeconds = max(0, Int(challenge.resendAllowedAt.timeIntervalSinceNow.rounded(.up)))
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
    ///
    /// Both closures are `@MainActor` because both touch ``state``. A non-`Sendable` escaping closure
    /// written inside a main-actor method already inherits that isolation; spelling it out is what
    /// keeps it true when `SWIFT_STRICT_CONCURRENCY` is raised.
    private func execute(
        onFailure: @escaping @MainActor (Error) -> Void = { _ in },
        _ block: @escaping @MainActor () async throws -> Void
    ) async {
        state.isBusy = true
        state.errorKey = nil
        do {
            try await block()
        } catch is CancellationError {
            // The screen going away, not a failure the passenger caused.
        } catch {
            onFailure(error)
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isBusy = false
    }
}

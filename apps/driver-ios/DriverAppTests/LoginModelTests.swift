import Foundation
import MageRideShared
import XCTest

@testable import DriverApp

/// SCR-DI-003 — the phone half, the code half, and what the screen says when the server says no.
@MainActor
final class LoginModelTests: XCTestCase {

    private func makeModel(
        sessions: FakeDriverSessions = FakeDriverSessions(),
        onboarding: FakeOnboardingRepository = FakeOnboardingRepository(),
        profiles: FakeDriverProfileRepository = FakeDriverProfileRepository(),
        preferences: FakeOnboardingPreferences = .firstRunDone
    ) -> LoginModel {
        LoginModel(
            sessions: sessions,
            onboarding: onboarding,
            profiles: profiles,
            preferences: preferences,
            pushTokens: PushTokenProvider()
        )
    }

    // MARK: - The phone half

    func testTheCtaIsDeadUntilTheNumberIsComplete() {
        let model = makeModel()

        model.onPhoneChanged("77123")
        XCTAssertFalse(model.state.canSubmit)

        model.onPhoneChanged("0771234567")
        XCTAssertTrue(model.state.canSubmit)
        XCTAssertEqual(model.state.phone, "771234567", "the trunk zero never reaches the field")
    }

    func testSubmittingTheNumberRequestsACodeAndOpensTheOtpHalf() async {
        let sessions = FakeDriverSessions()
        let model = makeModel(sessions: sessions)

        model.onPhoneChanged("0771234567")
        await model.submit()

        XCTAssertEqual(sessions.requestedPhone, "+94771234567", "the contract takes E.164")
        XCTAssertEqual(model.state.phase, .otp)
        XCTAssertEqual(model.state.attemptsRemaining, 5)
        XCTAssertFalse(model.state.isBusy)
    }

    // MARK: - The code half

    func testTheCtaIsDeadUntilAllSixDigitsAreThere() async {
        let model = makeModel()
        model.onPhoneChanged("0771234567")
        await model.submit()

        model.onOtpChanged("7123")
        XCTAssertFalse(model.state.canSubmit)

        model.onOtpChanged("712345")
        XCTAssertTrue(model.state.canSubmit)
    }

    /// A wrong digit keeps the attempt alive with one fewer try — the screen stays on the code half
    /// and says how many are left.
    func testAWrongCodeKeepsTheAttemptAliveAndClearsTheCells() async {
        let sessions = FakeDriverSessions()
        let model = makeModel(sessions: sessions)
        model.onPhoneChanged("0771234567")
        await model.submit()

        sessions.awaitingChallenge = LoginChallenge(
            attemptsRemaining: 4,
            resendAllowedAt: Date().addingTimeInterval(60),
            isBlocked: false
        )
        sessions.nextFailure = TestFailure()
        model.onOtpChanged("000000")
        await model.submit()

        XCTAssertEqual(model.state.phase, .otp)
        XCTAssertEqual(model.state.otp, "")
        XCTAssertEqual(model.state.attemptsRemaining, 4)
        XCTAssertNotNil(model.state.errorKey)
        XCTAssertNil(model.destination)
    }

    /// A **dead** attempt (`423 otp-locked`, `400 otp-expired`, `404 auth-not-found`) takes C014
    /// back to `SignedOut`, because that `authId` can never succeed again — so the screen goes back
    /// to the number rather than offering a seventh box.
    func testADeadAttemptSendsTheDriverBackToTheNumber() async {
        let sessions = FakeDriverSessions()
        let model = makeModel(sessions: sessions)
        model.onPhoneChanged("0771234567")
        await model.submit()

        sessions.awaitingChallenge = nil
        sessions.nextFailure = TestFailure()
        model.onOtpChanged("000000")
        await model.submit()

        XCTAssertEqual(model.state.phase, .phone)
        XCTAssertEqual(model.state.otp, "")
    }

    /// D-32 gives a number five OTPs an hour and a 60-second bucket between them, so a tap inside
    /// the window would spend one of the five on a message the server was never going to send.
    func testResendIsRefusedInsideTheCooldown() async {
        let sessions = FakeDriverSessions()
        let model = makeModel(sessions: sessions)
        model.onPhoneChanged("0771234567")
        await model.submit()

        XCTAssertFalse(model.state.canResend, "the challenge came back with 60 seconds to run")
        await model.resend()
        XCTAssertEqual(sessions.resendCount, 0)
    }

    func testResendIsOfferedOnceTheCooldownHasPassed() async {
        let sessions = FakeDriverSessions()
        sessions.challenge = LoginChallenge(
            attemptsRemaining: 5,
            resendAllowedAt: Date().addingTimeInterval(-1),
            isBlocked: false
        )
        let model = makeModel(sessions: sessions)
        model.onPhoneChanged("0771234567")
        await model.submit()

        XCTAssertTrue(model.state.canResend)
        await model.resend()
        XCTAssertEqual(sessions.resendCount, 1)
    }

    /// Back from the code half returns to the number without abandoning the attempt on the client
    /// side only — `cancelOtp` is what tells C014 the challenge is over.
    func testEditingTheNumberCancelsTheChallenge() async {
        let sessions = FakeDriverSessions()
        let model = makeModel(sessions: sessions)
        model.onPhoneChanged("0771234567")
        await model.submit()

        await model.editPhoneNumber()

        XCTAssertEqual(sessions.cancelCount, 1)
        XCTAssertEqual(model.state.phase, .phone)
        XCTAssertEqual(model.state.resendInSeconds, 0)
        XCTAssertNil(model.state.attemptsRemaining)
    }

    // MARK: - What happens after the verify

    /// The destination is computed from the profile the server actually holds rather than from
    /// `isNewUser` — a driver who installed, signed in and killed the app before Profile Setup is
    /// not a new user and still has no profile.
    func testANewDriverLandsOnProfileSetup() async {
        let sessions = FakeDriverSessions()
        let profiles = FakeDriverProfileRepository()
        profiles.name = nil
        let model = makeModel(sessions: sessions, profiles: profiles)

        await signIn(model)

        XCTAssertEqual(model.destination, .profileSetup)
    }

    func testAReturningDriverWithPermissionsAcknowledgedLandsOnHome() async {
        let profiles = FakeDriverProfileRepository()
        profiles.name = "K. Fernando"
        let preferences = FakeOnboardingPreferences.firstRunDone
        preferences.permissionsAcknowledged = true
        let model = makeModel(profiles: profiles, preferences: preferences)

        await signIn(model)

        XCTAssertEqual(model.destination, .home)
    }

    /// A failed profile read answers "no profile" here — the opposite of the splash's choice, and
    /// for the opposite reason: the driver signed in a moment ago, so the network was working, and
    /// Profile Setup is idempotent and is where a brand-new driver belongs anyway.
    func testAFailedProfileReadShowsProfileSetup() async {
        let profiles = FakeDriverProfileRepository()
        profiles.nameFailure = TestFailure()
        let model = makeModel(profiles: profiles)

        await signIn(model)

        XCTAssertEqual(model.destination, .profileSetup)
    }

    /// SCR-DI-002 runs signed out, so the first authenticated pass is the first point at which the
    /// language and city can reach `iam.users` (D-26, AL-27).
    func testTheFirstRunPreferencesArePushedOnceThereIsASession() async {
        let onboarding = FakeOnboardingRepository()
        let model = makeModel(onboarding: onboarding)

        await signIn(model)

        XCTAssertEqual(onboarding.syncCount, 1)
    }

    /// The shell can land on this screen from a `RouteToLogin` and from a deep link on a cold
    /// start, and a driver whose session was restored never needed it.
    func testARestoredSessionSkipsTheScreenEntirely() async {
        let sessions = FakeDriverSessions()
        sessions.isSignedIn = true
        let profiles = FakeDriverProfileRepository()
        profiles.name = "K. Fernando"
        let model = makeModel(sessions: sessions, profiles: profiles)

        await model.start()

        XCTAssertEqual(model.destination, .permissions)
    }

    private func signIn(_ model: LoginModel) async {
        model.onPhoneChanged("0771234567")
        await model.submit()
        model.onOtpChanged("712345")
        await model.submit()
    }
}

extension FakeOnboardingPreferences {

    /// A handset that has answered SCR-DI-002 and nothing else.
    static var firstRunDone: FakeOnboardingPreferences {
        let preferences = FakeOnboardingPreferences()
        preferences.language = Language.si
        preferences.operatingCityCode = "colombo"
        return preferences
    }
}

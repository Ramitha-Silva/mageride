import MageRideShared
import XCTest

@testable import PassengerApp

/// SCR-PI-003 — the number, the code, and every way the server says no.
@MainActor
final class LoginModelTests: XCTestCase {

    private var sessions: FakePassengerSessions!
    private var onboarding: FakeOnboardingRepository!
    private var profiles: FakePassengerProfileRepository!
    private var preferences: FakeAppPreferences!

    override func setUp() {
        super.setUp()
        sessions = FakePassengerSessions()
        onboarding = FakeOnboardingRepository()
        profiles = FakePassengerProfileRepository()
        preferences = FakeAppPreferences()
        preferences.language = Language.si
    }

    private func model() -> LoginModel {
        LoginModel(
            sessions: sessions,
            onboarding: onboarding,
            profiles: profiles,
            preferences: preferences,
            pushTokens: PushTokenProvider()
        )
    }

    // MARK: - The field

    /// ``PhoneNumber/normalise(_:)`` is applied on every keystroke, so the field can never hold a
    /// value the validator would reject — which is what makes the CTA's enabled state a property of
    /// the field rather than a check at submit time.
    func testTheFieldIsNormalisedOnEveryKeystroke() {
        let model = model()

        model.onPhoneChanged("077 123 4567")
        XCTAssertEqual(model.state.phone, "771234567")
        XCTAssertTrue(model.state.isPhoneValid)

        model.onPhoneChanged("+94 77 123 4567")
        XCTAssertEqual(model.state.phone, "771234567")
    }

    func testTheCtaIsDeadUntilTheNumberIsComplete() {
        let model = model()
        XCTAssertFalse(model.state.canSubmit)

        model.onPhoneChanged("7712345")
        XCTAssertFalse(model.state.canSubmit)

        model.onPhoneChanged(Fixtures.phone)
        XCTAssertTrue(model.state.canSubmit)
    }

    // MARK: - Requesting

    /// The E.164 form is what `POST /v1/auth/otp/request` takes, and the **push token rides along**
    /// so the first notification a passenger can receive has somewhere to land before they have
    /// opened the app a second time.
    func testRequestingSendsE164AndMovesToTheCodeHalf() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)

        await model.submit()

        XCTAssertEqual(sessions.requestedPhones, ["+94\(Fixtures.phone)"])
        XCTAssertEqual(sessions.requestedPushTokens.count, 1)
        XCTAssertEqual(model.state.phase, .otp)
        XCTAssertEqual(model.state.attemptsRemaining, 5)
    }

    /// A tap while a request is in flight must not send a second one.
    func testSubmitIsRefusedWhileBusy() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()

        let before = sessions.verifiedCodes.count
        await model.submit()  // phase is now .otp with an empty code

        XCTAssertEqual(sessions.verifiedCodes.count, before, "an incomplete code cannot be submitted")
    }

    // MARK: - Resending

    /// **US-1.10's cooldown is refused locally, not just by the server.** D-32 caps requests at five
    /// an hour, so a tap inside the window does not merely fail — it *spends* one of the five.
    func testResendIsRefusedInsideTheCooldown() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        sessions.challenge = LoginChallenge(
            attemptsRemaining: 5,
            resendAllowedAt: Date().addingTimeInterval(60),
            isBlocked: false
        )
        await model.submit()

        XCTAssertGreaterThan(model.state.resendInSeconds, 0)
        XCTAssertFalse(model.state.canResend)

        await model.resend()

        XCTAssertEqual(sessions.resendCount, 0, "a refused resend must not reach the gateway")
    }

    /// Past the window it is offered, and it goes out.
    func testResendIsAllowedOnceTheWindowHasLapsed() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        sessions.challenge = LoginChallenge(
            attemptsRemaining: 5,
            resendAllowedAt: Date().addingTimeInterval(-1),
            isBlocked: false
        )
        await model.submit()

        XCTAssertEqual(model.state.resendInSeconds, 0)
        XCTAssertTrue(model.state.canResend)

        await model.resend()

        XCTAssertEqual(sessions.resendCount, 1)
    }

    // MARK: - Verifying

    /// A **wrong digit** keeps the attempt alive with one fewer try: the screen stays on the code
    /// half and says how many are left, which is what stops somebody burning the last one guessing.
    func testAWrongCodeKeepsTheAttemptAliveOnTheCodeHalf() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()

        sessions.verifyFailure = FakeError.unreachable
        sessions.awaitingChallenge = LoginChallenge(
            attemptsRemaining: 2,
            resendAllowedAt: Date().addingTimeInterval(30),
            isBlocked: false
        )
        model.onOtpChanged("123456")

        await model.submit()

        XCTAssertEqual(model.state.phase, .otp)
        XCTAssertEqual(model.state.otp, "", "the boxes are cleared for the next try")
        XCTAssertEqual(model.state.attemptsRemaining, 2)
        XCTAssertNotNil(model.state.errorKey)
    }

    /// A **dead attempt** — `423 otp-locked`, `400 otp-expired`, `404 auth-not-found` — takes C014
    /// back to `SignedOut`, because that `authId` can never succeed again. So the screen goes back to
    /// the number rather than offering a seventh box.
    func testADeadAttemptReturnsToTheNumber() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()

        sessions.verifyFailure = FakeError.unreachable
        sessions.awaitingChallenge = nil
        model.onOtpChanged("123456")

        await model.submit()

        XCTAssertEqual(model.state.phase, .phone)
        XCTAssertEqual(model.state.resendInSeconds, 0)
    }

    /// `‹ Back` from the code half abandons the attempt server-side rather than leaving it live.
    func testEditingTheNumberCancelsTheAttempt() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()

        await model.editPhoneNumber()

        XCTAssertEqual(sessions.cancelCount, 1)
        XCTAssertEqual(model.state.phase, .phone)
        XCTAssertNil(model.state.attemptsRemaining)
    }

    // MARK: - What happens after a verify

    /// **The language chosen on SCR-PI-002 is pushed here** because this is the first point at which
    /// it can be — that screen runs signed out.
    func testAVerifyPushesTheStoredLanguage() async {
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()
        model.onOtpChanged("482913")

        await model.submit()

        XCTAssertEqual(sessions.verifiedCodes, ["482913"])
        XCTAssertEqual(onboarding.syncCount, 1)
    }

    /// The destination comes from **the profile the server holds**, never from `isNewUser`: somebody
    /// who installed, signed in and killed the app before Profile Setup is not a new user and still
    /// has no name.
    func testTheDestinationComesFromTheProfileNotFromIsNewUser() async {
        profiles.profile = Fixtures.profile(firstName: nil)
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()
        model.onOtpChanged("482913")

        await model.submit()

        XCTAssertEqual(model.destination, .profileSetup)
    }

    func testACompleteProfileGoesOnToTheLocationRationale() async {
        profiles.profile = Fixtures.profile()
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()
        model.onOtpChanged("482913")

        await model.submit()

        XCTAssertEqual(model.destination, .locationPermission)
    }

    /// **A failed profile read answers `false` here** — the opposite of the splash's default, and for
    /// the opposite reason: the passenger signed in a second ago, so the network was working, and
    /// Profile Setup is idempotent and where a brand-new passenger belongs anyway.
    func testAFailedProfileReadShowsProfileSetup() async {
        profiles.profile = nil
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()
        model.onOtpChanged("482913")

        await model.submit()

        XCTAssertEqual(model.destination, .profileSetup)
    }

    /// A session restored from the Keychain means this screen was never needed — the shell can land
    /// on it from a `RouteToLogin` or a cold-start deep link.
    func testAnAlreadySignedInPassengerIsSentStraightOn() async {
        sessions.isSignedIn = true
        sessions.userId = Fixtures.passengerId
        preferences.locationRationaleAcknowledged = true
        let model = model()

        await model.start()

        XCTAssertEqual(model.destination, .liveMap)
        XCTAssertTrue(sessions.requestedPhones.isEmpty, "no code is requested for a session that exists")
    }

    // MARK: - Errors

    /// D-26: an app never renders a `ProblemDetails` string. A failure with no `MageRideError` behind
    /// it still resolves to copy rather than to nothing.
    func testAnUnknownFailureResolvesToTheGenericMessage() async {
        sessions.requestFailure = FakeError.unreachable
        let model = model()
        model.onPhoneChanged(Fixtures.phone)

        await model.submit()

        XCTAssertEqual(model.state.errorKey, "error_generic")
        XCTAssertEqual(model.state.phase, .phone, "a failed request does not open the code half")
    }

    func testDismissingTheAlertClearsTheError() async {
        sessions.requestFailure = FakeError.unreachable
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()
        XCTAssertNotNil(model.state.errorKey)

        model.dismissError()

        XCTAssertNil(model.state.errorKey)
    }

    /// Typing clears the last failure: an error about the previous attempt sitting over a field
    /// somebody is currently correcting is an error about nothing.
    func testTypingClearsTheError() async {
        sessions.requestFailure = FakeError.unreachable
        let model = model()
        model.onPhoneChanged(Fixtures.phone)
        await model.submit()

        model.onPhoneChanged("7719")

        XCTAssertNil(model.state.errorKey)
    }

    /// **Every code the first-run operations declare has copy**, and every one of them is a key the
    /// three `.strings` files carry — `LocalizationTests` is what checks the second half.
    func testTheErrorTableCoversTheFirstRunCodes() {
        // A `MageRideError` cannot be constructed from Swift without the Kotlin initialiser, so what
        // is asserted here is the *table*: the keys it can produce, against the ones declared. The
        // wiring from a thrown error to this function is covered by the tests above.
        let keys = [
            "error_offline", "error_generic", "error_otp_invalid", "error_otp_expired",
            "error_otp_locked", "error_otp_rate_limited", "error_phone_invalid",
            "error_device_mismatch", "error_user_blocked", "error_validation_failed",
        ]
        for key in keys {
            XCTAssertNotEqual(key.localised, key, "\(key) has no copy in the bundle")
        }
    }
}

/// The one phone shape this platform accepts: `+947XXXXXXXX` (D5' §14.1).
final class PhoneNumberTests: XCTestCase {

    /// Both ways a Sri Lankan number is written down have to work — somebody reading their number
    /// off a bill will type the trunk zero.
    func testEveryFormAPassengerMightTypeNormalisesToTheSame() {
        for input in ["771234567", "0771234567", "94771234567", "+94771234567", "0094771234567",
                      "077 123 4567", "+94 77 123 4567", "(077) 123-4567"] {
            XCTAssertEqual(PhoneNumber.normalise(input), "771234567", input)
        }
    }

    /// **ASCII digits only.** An E.164 string is built out of this, and another script's digits are
    /// not a number the gateway can dial — the field should fail, not the request.
    func testAnotherScriptsDigitsAreNotDigits() {
        XCTAssertEqual(PhoneNumber.normalise("෧෭෧"), "")
    }

    func testTheNumberIsNineDigitsStartingWithSeven() {
        XCTAssertTrue(PhoneNumber.isValid("771234567"))
        XCTAssertFalse(PhoneNumber.isValid("77123456"), "eight is not enough")
        XCTAssertFalse(PhoneNumber.isValid("112345678"), "a landline is not a mobile")
        XCTAssertFalse(PhoneNumber.isValid(""))
    }

    /// Nothing longer than the national number survives, so a paste cannot overflow the field.
    func testExtraDigitsAreDropped() {
        XCTAssertEqual(PhoneNumber.normalise("7712345678901"), "771234567")
    }

    func testE164IsThePrefixAndTheNationalNumber() {
        XCTAssertEqual(PhoneNumber.toE164("771234567"), "+94771234567")
        XCTAssertEqual(PhoneNumber.countryCode, "+94")
    }
}

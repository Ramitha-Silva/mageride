import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 1's seams, faked.
///
/// **Every one of these stands in for a Swift protocol, never for a Kotlin class.** That is the whole
/// reason `PassengerSessions`, `OnboardingRepository`, `PassengerProfileRepository`,
/// `ActiveRideLookup` and `LocationPermission` exist: `AuthSessionManager` is a Kotlin *class* and
/// `IamApi`/`RideApi` are Kotlin interfaces with `suspend` methods, and Swift can stand in for
/// neither. The seams are what let every rule C095 owns be asserted with no gateway and no handset.

// MARK: - Preferences

final class FakeAppPreferences: AppPreferences {
    var language: Language?
    var languagePendingSync = false
    var locationRationaleAcknowledged = false
    var defaultPaymentMethod: String?
}

// MARK: - Sessions

final class FakePassengerSessions: PassengerSessions {

    var isSignedIn = false
    var userId: String?
    var awaitingChallenge: LoginChallenge?

    /// Programmable answers. `nil` means "succeed"; a value is thrown.
    var requestFailure: Error?
    var verifyFailure: Error?
    var resendFailure: Error?

    /// What a successful request or resend answers.
    var challenge = LoginChallenge(
        attemptsRemaining: 5,
        resendAllowedAt: Date().addingTimeInterval(60),
        isBlocked: false
    )

    /// Every call, in order — several rules here are about *what was sent* rather than about state.
    private(set) var requestedPhones: [String] = []
    private(set) var requestedPushTokens: [String?] = []
    private(set) var verifiedCodes: [String] = []
    private(set) var restoreCount = 0
    private(set) var resendCount = 0
    private(set) var cancelCount = 0
    private(set) var logOutCount = 0

    func restore() async { restoreCount += 1 }

    func requestOtp(phone: String, pushToken: String?) async throws -> LoginChallenge {
        requestedPhones.append(phone)
        requestedPushTokens.append(pushToken)
        if let requestFailure { throw requestFailure }
        return challenge
    }

    func resendOtp() async throws -> LoginChallenge {
        resendCount += 1
        if let resendFailure { throw resendFailure }
        return challenge
    }

    func verifyOtp(_ code: String) async throws {
        verifiedCodes.append(code)
        if let verifyFailure { throw verifyFailure }
        isSignedIn = true
        userId = userId ?? Fixtures.passengerId
    }

    func cancelOtp() async { cancelCount += 1 }

    func logOut() async { logOutCount += 1 }
}

// MARK: - Onboarding

final class FakeOnboardingRepository: OnboardingRepository {

    var slidesResponse: [OnboardingSlide] = []
    var stored: Language?
    var syncResult = true

    private(set) var chosen: [Language] = []
    private(set) var syncCount = 0

    func slides() async -> [OnboardingSlide] { slidesResponse }

    func selectedLanguage() -> Language { stored ?? LanguageDisplay.default }

    func storedLanguage() -> Language? { stored }

    func choose(_ language: Language) {
        chosen.append(language)
        stored = language
    }

    @discardableResult
    func syncPreferences() async -> Bool {
        syncCount += 1
        return syncResult
    }
}

// MARK: - Profile

final class FakePassengerProfileRepository: PassengerProfileRepository {

    /// What `me()` answers. `nil` throws — which is the case both the splash and the login screen
    /// have an opinion about, and they disagree.
    var profile: UserProfile? = Fixtures.profile()
    var saveFailure: Error?

    private(set) var savedNames: [String] = []
    private(set) var savedLanguages: [Language] = []
    private(set) var savedNotifications: [Bool] = []
    private(set) var meCount = 0

    func me() async throws -> UserProfile {
        meCount += 1
        guard let profile else { throw FakeError.unreachable }
        return profile
    }

    @discardableResult
    func save(name: String, language: Language, notificationsEnabled: Bool) async throws -> UserProfile {
        savedNames.append(name)
        savedLanguages.append(language)
        savedNotifications.append(notificationsEnabled)
        if let saveFailure { throw saveFailure }
        return profile ?? Fixtures.profile(firstName: name, language: language)
    }

    @discardableResult
    func update(name: String?, notifPrefs: [String: Bool]?) async throws -> UserProfile {
        profile ?? Fixtures.profile()
    }

    @discardableResult
    func saveLanguage(_ language: Language) async throws -> Language { language }
}

// MARK: - Active ride

final class FakeActiveRideLookup: ActiveRideLookup {

    var rideId: String?
    private(set) var lookups: [String] = []

    func activeRideId(passengerId: String) async -> String? {
        lookups.append(passengerId)
        return rideId
    }
}

// MARK: - Location

final class FakeLocationPermission: LocationPermission {

    var authorisation: LocationAuthorisation = .notDetermined

    /// What the system dialog answers. Applied to ``authorisation`` on request, as the real one does.
    var grants: LocationAuthorisation = .granted

    private(set) var requestCount = 0
    private(set) var settingsCount = 0

    func request() async -> LocationAuthorisation {
        requestCount += 1
        // The real one is a no-op once a refusal has happened — see `SystemLocationPermission`.
        guard authorisation == .notDetermined else { return authorisation }
        authorisation = grants
        return grants
    }

    func openSettings() { settingsCount += 1 }
}

// MARK: -

enum FakeError: Error {
    /// A failure with no `MageRideError` behind it — resolves to the generic message.
    case unreachable
}

/// The canonical values cluster 1's tests are written against.
enum Fixtures {

    /// The same ULID shape `:shared`'s own `Fixtures` uses, so a value read across the two reads the
    /// same way.
    static let passengerId = "01JQ9F8Z6N0000000000000001"
    static let rideId = "01JQ9F8Z6N0000000000000003"

    /// A complete Sri Lankan mobile, national form.
    static let phone = "771234567"

    /// A `UserProfile` with every field the export requires.
    ///
    /// Thirteen arguments because a Kotlin default does not survive the Objective-C export — the
    /// same wall every constructor on this bridge hits. Wrapped here so no test spells it twice.
    static func profile(
        firstName: String? = "Ramith de Silva",
        language: Language? = Language.si,
        notifPrefs: [String: Bool]? = nil
    ) -> UserProfile {
        UserProfile(
            userId: passengerId,
            phone: "+94\(phone)",
            email: nil,
            firstName: firstName,
            photoUrl: nil,
            role: Role.passenger,
            roles: nil,
            fleetRole: nil,
            language: language,
            operatingCityCode: nil,
            defaultPaymentMethod: nil,
            notifPrefs: notifPrefs?.mapValues { KotlinBoolean(value: $0) },
            createdAt: nil
        )
    }

    /// One content-svc slide.
    static func slide(slot: Int, title: String, body: String, ref: String = "onboarding_1") -> OnboardingSlide {
        OnboardingSlide(
            slot: Int32(slot),
            illustrationRef: ref,
            title: TrilingualText(si: "\(title) SI", ta: "\(title) TA", en: title),
            body: TrilingualText(si: "\(body) SI", ta: "\(body) TA", en: body)
        )
    }
}

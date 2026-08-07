import Foundation
import MageRideShared

@testable import DriverApp

/// The fakes cluster 1's models are driven by.
///
/// **Every seam here is a Swift protocol, and that is why they exist.** `AuthSessionManager`,
/// `ContentApi` and `RegistryApi` are Kotlin, and a Kotlin *class* cannot be stood in for from
/// Swift at all — so a login-screen test with no gateway needs the seam on this side of the bridge.
/// The Kotlin-backed implementations (`SharedDriverSessions`, `ApiOnboardingRepository`,
/// `ApiDriverProfileRepository`) convert and forward and decide nothing; what these tests cover is
/// everything that does decide.

// MARK: - Preferences

/// ``OnboardingPreferences`` in memory. `UserDefaults` would leak between tests through the shared
/// suite, and the router's whole job is a function of these four values.
final class FakeOnboardingPreferences: OnboardingPreferences {
    var language: Language?
    var operatingCityCode: String?
    var preferencesPendingSync = false
    var permissionsAcknowledged = false
}

// MARK: - Sessions

/// ``DriverSessions`` with no gateway behind it.
final class FakeDriverSessions: DriverSessions {

    var isSignedIn = false
    var awaitingChallenge: LoginChallenge?

    /// The signed-in driver (Δ C088). Set alongside ``isSignedIn`` where a test needs a driver id.
    var userId: String?

    /// What the next call throws, or `nil` to succeed.
    var nextFailure: Error?

    /// What ``requestOtp(phone:pushToken:)`` and ``resendOtp()`` answer.
    var challenge = LoginChallenge(
        attemptsRemaining: 5,
        resendAllowedAt: Date().addingTimeInterval(60),
        isBlocked: false
    )

    private(set) var restoreCount = 0
    private(set) var requestedPhone: String?
    private(set) var requestedPushToken: String?
    private(set) var verifiedCode: String?
    private(set) var resendCount = 0
    private(set) var cancelCount = 0
    private(set) var logOutCount = 0

    func restore() async {
        restoreCount += 1
    }

    func requestOtp(phone: String, pushToken: String?) async throws -> LoginChallenge {
        requestedPhone = phone
        requestedPushToken = pushToken
        try throwIfProgrammed()
        awaitingChallenge = challenge
        return challenge
    }

    func resendOtp() async throws -> LoginChallenge {
        resendCount += 1
        try throwIfProgrammed()
        awaitingChallenge = challenge
        return challenge
    }

    func verifyOtp(_ code: String) async throws {
        verifiedCode = code
        try throwIfProgrammed()
        isSignedIn = true
        awaitingChallenge = nil
    }

    func cancelOtp() async {
        cancelCount += 1
        awaitingChallenge = nil
    }

    /// Δ C092. Never throws, for the reason ``SharedDriverSessions/logOut()`` does not: the local
    /// session goes whatever the gateway answered.
    func logOut() async {
        logOutCount += 1
        isSignedIn = false
        userId = nil
    }

    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

// MARK: - Onboarding repository

/// ``OnboardingRepository`` over an in-memory city list.
final class FakeOnboardingRepository: OnboardingRepository {

    var cityList: [OperatingCity] = [.colombo, .kandy]
    var citiesFailure: Error?
    var stored: OnboardingSelection = OnboardingSelection(language: Language.si, cityCode: nil)
    var storedLanguageValue: Language?
    var syncResult = true

    private(set) var chosen: (language: Language, cityCode: String)?
    private(set) var cityCallCount = 0
    private(set) var syncCount = 0

    func cities() async throws -> [OperatingCity] {
        cityCallCount += 1
        if let citiesFailure { throw citiesFailure }
        return cityList
    }

    func selection() -> OnboardingSelection { stored }

    func storedLanguage() -> Language? { storedLanguageValue }

    func choose(language: Language, cityCode: String) {
        chosen = (language, cityCode)
    }

    func syncPreferences() async -> Bool {
        syncCount += 1
        return syncResult
    }
}

// MARK: - Profile repository

/// ``DriverProfileRepository`` with a programmable verdict.
final class FakeDriverProfileRepository: DriverProfileRepository {

    var name: String?
    var nameFailure: Error?
    var submitFailure: Error?
    var extraction = LicenceExtraction.allConfirmed

    private(set) var submitted: [ProfileDraft] = []
    private(set) var nameCallCount = 0

    func profileName() async throws -> String? {
        nameCallCount += 1
        if let nameFailure { throw nameFailure }
        return name
    }

    func submit(_ draft: ProfileDraft) async throws -> LicenceExtraction {
        submitted.append(draft)
        if let submitFailure { throw submitFailure }
        return extraction
    }
}

// MARK: - Permissions

/// ``DriverPermissions`` with no system behind it.
@MainActor
final class FakeDriverPermissions: DriverPermissions {

    var granted: Set<DriverPermission> = []
    var permanentlyDenied: Set<DriverPermission> = []

    /// What ``request(_:)`` grants. A permission not in here is refused.
    var grantsOnRequest: Set<DriverPermission> = []

    private(set) var requested: [DriverPermission] = []
    private(set) var settingsOpenedFor = 0
    private(set) var refreshCount = 0

    func refresh() async {
        refreshCount += 1
    }

    func isGranted(_ permission: DriverPermission) -> Bool { granted.contains(permission) }

    func isDeniedPermanently(_ permission: DriverPermission) -> Bool {
        permanentlyDenied.contains(permission)
    }

    func request(_ permission: DriverPermission) async -> Bool {
        requested.append(permission)
        guard grantsOnRequest.contains(permission) else { return false }
        granted.insert(permission)
        return true
    }

    func openSettings() {
        settingsOpenedFor += 1
    }
}

// MARK: - Fixtures

extension OperatingCity {

    /// `sortOrder` puts Colombo first, which is why the screen pre-selects it — the app expresses
    /// no default of its own (AL-27).
    static let colombo = OperatingCity(
        code: "colombo",
        nameEn: "Colombo",
        nameSi: "කොළඹ",
        nameTa: "கொழும்பு",
        centroid: GeoPoint(lat: 6.9271, lng: 79.8612),
        sortOrder: 1
    )

    static let kandy = OperatingCity(
        code: "kandy",
        nameEn: "Kandy",
        nameSi: "මහනුවර",
        nameTa: "கண்டி",
        centroid: GeoPoint(lat: 7.2906, lng: 80.6337),
        sortOrder: 2
    )
}

extension LicenceExtraction {

    /// Every field read and confirmed — the case where SCR-DI-003a has nothing to show the driver
    /// and continues on the first Save.
    static let allConfirmed = LicenceExtraction(
        fields: LicenceFieldKeys.order.map {
            LicenceField(key: $0, value: "read", source: FieldSource.ai, needsOfficerReview: false)
        },
        displayName: "K. Fernando"
    )

    /// The NIC came back pending, which is BR-25.2's "doubtful, unread or typed" — the driver stays
    /// on the card.
    static let nicPending = LicenceExtraction(
        fields: LicenceFieldKeys.order.map { key in
            LicenceField(
                key: key,
                value: key == LicenceFieldKeys.nicNo ? nil : "read",
                source: FieldSource.ai,
                needsOfficerReview: key == LicenceFieldKeys.nicNo
            )
        },
        displayName: "K. Fernando"
    )
}

extension ProfileDraft {

    /// A draft with everything Save requires (AL-27): a name, a photo and both licence sides.
    static func complete(name: String = "K. Fernando") -> ProfileDraft {
        ProfileDraft(
            name: name,
            photo: .stub("profile-photo.jpg", CaptureSource.gallery),
            licenceFront: .stub("licence-front.jpg", CaptureSource.cameraDragCrop),
            licenceBack: .stub("licence-back.jpg", CaptureSource.cameraDragCrop)
        )
    }
}

extension CapturedImage {

    /// A one-byte image. Nothing under test reads the bytes; what travels with them is the
    /// provenance (AL-43).
    static func stub(_ fileName: String, _ capturedVia: CaptureSource) -> CapturedImage {
        CapturedImage(fileName: fileName, data: Data([0x00]), capturedVia: capturedVia)
    }
}

/// A failure that is not a `MageRideError`, for the "anything else is the generic message" arm.
struct TestFailure: Error {}

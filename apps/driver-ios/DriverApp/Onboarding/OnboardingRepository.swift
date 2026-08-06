import Foundation
import MageRideShared

/// SCR-DI-002's current answers.
///
/// - Parameters:
///   - language: Sinhala until the driver says otherwise (AL-26).
///   - cityCode: `nil` until a city is picked; the CTA is disabled while it is.
struct OnboardingSelection: Equatable {
    let language: Language
    let cityCode: String?
}

/// SCR-DI-002's data: the launch cities, and where the two answers end up.
///
/// A protocol so ``LanguageCityModel`` can be driven by a fake. The production implementation is
/// ``ApiOnboardingRepository``; everything below the protocol line is `:shared`'s.
protocol OnboardingRepository: AnyObject {

    /// The active launch cities, `sortOrder` first.
    func cities() async throws -> [OperatingCity]

    /// What SCR-DI-002 currently shows as selected — Sinhala by default (AL-26).
    func selection() -> OnboardingSelection

    /// The language the app is currently rendering in, or `nil` on a first run.
    func storedLanguage() -> Language?

    /// Records the driver's answers on the device and marks them for the server.
    func choose(language: Language, cityCode: String)

    /// Pushes the stored answers to `iam.users`. Returns whether both landed.
    func syncPreferences() async -> Bool
}

/// ``OnboardingRepository`` over content-svc and iam-svc.
///
/// **The city list is never a constant** (AL-27). `config.operating_cities` is admin-managed and
/// `GET /v1/config/cities` is the only route that reads it, precisely so activating a new launch
/// city needs no app release (US-1.3a). A hard-coded list here would silently strand a city the
/// Admin Portal had already opened.
///
/// The screen runs before sign-in, so the two answers are written locally first and pushed to
/// `iam.users` by ``syncPreferences()`` on the first authenticated pass — the login screen calls it
/// once the OTP has been verified.
final class ApiOnboardingRepository: OnboardingRepository {

    private let content: ContentApi
    private let iam: IamApi
    private let preferences: OnboardingPreferences

    private var cachedTag: String?
    private var cachedCities: [OperatingCity] = []

    init(content: ContentApi, iam: IamApi, preferences: OnboardingPreferences) {
        self.content = content
        self.iam = iam
        self.preferences = preferences
    }

    /// A conditional GET: `/v1/config/cities` is the one route in the whole contract that declares
    /// an `ETag` and a `304`, and the first-run screen is exactly the caller it was declared for. A
    /// `NotModified` answers from the cache, so a driver who backs out of the screen and returns
    /// pays no body.
    ///
    /// `Conditional` is read through ``valueOrNull``/``etagOrNull`` rather than by casting to
    /// `Conditional.Value`: Kotlin/Native erases a generic interface's type parameter, so what
    /// arrives here is an unparameterised existential and the nested generic class has no spelling
    /// in Swift. A `nil` value is the `304` — keep the cached list and the tag that produced it.
    func cities() async throws -> [OperatingCity] {
        let answer = try await content.getOperatingCities(ifNoneMatch: cachedTag)

        guard let page = answer.valueOrNull as? OperatingCityListResponse else { return cachedCities }

        cachedCities = page.cities
        cachedTag = answer.etagOrNull
        return cachedCities
    }

    func selection() -> OnboardingSelection {
        OnboardingSelection(language: preferences.language ?? Language.si, cityCode: preferences.operatingCityCode)
    }

    /// Distinct from ``selection()``'s Sinhala default: `nil` here means "the handset's locale is
    /// what is on screen", and choosing සිංහල over it is still a change that has to be applied.
    func storedLanguage() -> Language? { preferences.language }

    /// Local first, and unconditionally: the flow continues to Login whether or not the handset can
    /// reach the gateway, and a driver who chose සිංහල must not be shown an English login screen
    /// because a preference call timed out.
    func choose(language: Language, cityCode: String) {
        preferences.language = language
        preferences.operatingCityCode = cityCode
        preferences.preferencesPendingSync = true
    }

    /// Pushes the stored answers to `iam.users` (D-26 language, AL-27 operating city).
    ///
    /// Called after sign-in, and best effort: neither preference is worth failing a login over, and
    /// the flag stays set so the next authenticated pass tries again.
    func syncPreferences() async -> Bool {
        guard preferences.preferencesPendingSync else { return true }

        do {
            if let language = preferences.language {
                _ = try await iam.setLanguagePreference(request: LanguagePreference(language: language))
            }
            if let city = preferences.operatingCityCode {
                _ = try await iam.setOperatingCity(request: OperatingCityPreference(operatingCityCode: city))
            }
            preferences.preferencesPendingSync = false
            return true
        } catch {
            // Left pending on purpose — see the doc comment.
            return false
        }
    }
}

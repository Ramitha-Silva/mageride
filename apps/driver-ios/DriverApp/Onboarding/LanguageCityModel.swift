import Foundation
import MageRideShared

/// SCR-DI-002's state.
///
/// - Parameters:
///   - language: The selected language. Sinhala until the driver changes it.
///   - cities: The active launch cities from `GET /v1/config/cities`. Empty while loading.
///   - cityCode: The selected city's code; `nil` disables Continue.
///   - isLoadingCities: Whether the city call is in flight.
///   - citiesFailed: Whether it failed — the screen offers Retry.
struct LanguageCityState {

    var language: Language = Language.si
    var cities: [OperatingCity] = []
    var cityCode: String?
    var isLoadingCities = true
    var citiesFailed = false

    /// AL-26's vertical boxes, in the one order the spec fixes: Sinhala first and default.
    ///
    /// Not `Language.values()`: that order is the wire enum's, and changing this list is the only
    /// way to change what the screen shows.
    var languages: [Language] { LanguageCityState.languages }

    /// The wireframe's CTA is dead until a city has been chosen; the city list is a radio group.
    var canContinue: Bool { cityCode != nil }

    /// *"language as **vertical boxes (Sinhala first & default)**, then Tamil, English"* —
    /// D2' §B SCR-DI-002, AL-26.
    static let languages: [Language] = [Language.si, Language.ta, Language.en]
}

/// SCR-DI-002 — first-run language and operating city.
///
/// Two selections and one rule each: the language defaults to Sinhala (AL-26) and the city list is
/// **always** the server's (AL-27, US-1.3a). Both are written to the device on Continue and pushed
/// to `iam.users` after sign-in — the screen runs before there is a session to write them against.
@MainActor
final class LanguageCityModel: ObservableObject {

    @Published private(set) var state: LanguageCityState

    private let repository: OnboardingRepository
    private let applyLanguage: (Language) -> Void

    /// - Parameter applyLanguage: What ``confirm()`` does with the answer. Defaulted to
    ///   ``DriverLocale/apply(_:)``, and injectable because that call swaps the app bundle's class
    ///   and writes `AppleLanguages` — process-wide effects a test must not leave behind.
    init(repository: OnboardingRepository, applyLanguage: @escaping (Language) -> Void = { DriverLocale.apply($0) }) {
        self.repository = repository
        self.applyLanguage = applyLanguage
        let selection = repository.selection()
        self.state = LanguageCityState(language: selection.language, cityCode: selection.cityCode)
    }

    /// `GET /v1/config/cities`. Retried by the screen's error state, never silently.
    func loadCities() async {
        state.isLoadingCities = true
        state.citiesFailed = false

        do {
            let cities = try await repository.cities()
            state.cities = cities
            // Colombo is first because `sortOrder` puts it there, not because the app says so.
            // Pre-selecting the first row saves a tap without inventing a default the server did
            // not express.
            state.cityCode = state.cityCode ?? cities.first?.code
            state.isLoadingCities = false
        } catch {
            state.isLoadingCities = false
            state.citiesFailed = true
        }
    }

    func select(language: Language) {
        state.language = language
    }

    func select(cityCode: String) {
        state.cityCode = cityCode
    }

    /// Stores both answers and applies the language to the app's own strings.
    ///
    /// Returns whether there was anything to store — `false` only when no city is selected, which
    /// the disabled CTA already prevents.
    ///
    /// **Δ Section C.** Android returns "did the language change?" so the screen can `recreate()`
    /// the Activity; iOS has no `recreate()` and needs none, because ``DriverLocale`` redirects the
    /// bundle every subsequent lookup goes through. The next screen is therefore already in the
    /// chosen language — see that file for the half that only lands on the next launch.
    @discardableResult
    func confirm() -> Bool {
        guard let cityCode = state.cityCode else { return false }
        repository.choose(language: state.language, cityCode: cityCode)
        applyLanguage(state.language)
        return true
    }
}

/// The language's own name in its own script — `සිංහල`, `தமிழ்`, `English`.
///
/// **Deliberately not a string resource.** An endonym is the same string in all three locales, so
/// the three `Localizable.strings` files would carry three identical values — which
/// `LocalizationTests` correctly rejects as "a key copied into si and ta and never translated". It
/// is the same argument `OperatingCity`'s own KDoc makes about city names: a proper noun is
/// **data**, not copy. Whoever is looking for their language is looking for the script they read.
enum LanguageDisplay {

    /// The endonym.
    static func endonym(_ language: Language) -> String {
        switch language {
        case Language.ta: return "தமிழ்"
        case Language.en: return "English"
        default: return "සිංහල"
        }
    }

    /// The English gloss beside it, so a driver who reads neither other script can still navigate.
    ///
    /// `nil` for English, where the gloss would repeat the endonym — which is exactly what the
    /// wireframe draws: `සිංහල Sinhala`, `தமிழ் Tamil`, and a bare `English`.
    static func englishName(_ language: Language) -> String? {
        switch language {
        case Language.ta: return "Tamil"
        case Language.en: return nil
        default: return "Sinhala"
        }
    }
}

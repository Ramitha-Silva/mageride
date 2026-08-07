import Foundation
import MageRideShared

/// SCR-PI-002's data: the carousel, and where the language answer ends up.
///
/// The screen runs **before sign-in**, so the answer is written locally first and pushed to
/// `iam.users` by ``syncPreferences()`` on the first authenticated pass — the login screen calls it
/// once the OTP has been verified.
///
/// A protocol because the model is what is under test: `ContentApi` and `IamApi` are Kotlin
/// interfaces with `suspend` methods, which Swift can call and should not try to implement (see
/// ``NearbySnapshots`` for the same argument on the live plane).
protocol OnboardingRepository: AnyObject {

    /// AL-28's three slides, or an empty list when content-svc cannot be reached.
    func slides() async -> [OnboardingSlide]

    /// What SCR-PI-002 shows as selected — Sinhala by default (AL-26).
    func selectedLanguage() -> Language

    /// The language the app is currently rendering in, or `nil` on a first run.
    func storedLanguage() -> Language?

    /// Records the choice on the device and marks it for the server.
    func choose(_ language: Language)

    /// Pushes the stored language to `iam.users` (D-26). Answers whether it landed.
    @discardableResult
    func syncPreferences() async -> Bool
}

/// ``OnboardingRepository`` over content-svc and iam-svc.
final class ApiOnboardingRepository: OnboardingRepository {

    private let content: ContentApi
    private let iam: IamApi
    private let preferences: AppPreferences

    /// Held for the screen's life because the language picker is on the same screen: switching to
    /// සිංහල re-renders from the same response rather than re-fetching, which is precisely why the
    /// payload carries all three languages at once.
    private var cached: [OnboardingSlide] = []

    init(content: ContentApi, iam: IamApi, preferences: AppPreferences) {
        self.content = content
        self.iam = iam
        self.preferences = preferences
    }

    /// **Empty is a supported answer, not a failure.** The screen falls back to
    /// ``FeatureSlides/fallback``, which is bundled and trilingual: first launch is exactly when a
    /// passenger is most likely to be on a bad connection, and a carousel is not worth blocking the
    /// language picker over.
    ///
    /// `GET /v1/content/onboarding/passenger` is public and unauthenticated (AL-28, BR-25.1)
    /// precisely because it is drawn on a screen that runs before sign-in.
    func slides() async -> [OnboardingSlide] {
        if !cached.isEmpty { return cached }
        guard let response = try? await content.listOnboardingSlides(audience: OnboardingAudience.passenger) else {
            return []
        }
        cached = response.slides.sorted { $0.slot < $1.slot }
        return cached
    }

    func selectedLanguage() -> Language {
        preferences.language ?? LanguageDisplay.default
    }

    /// Distinct from ``selectedLanguage()``'s Sinhala default: `nil` here means *"the handset's own
    /// locale is what is on screen"*, and choosing සිංහල over it is still a change the bundle
    /// redirect has to be told about.
    func storedLanguage() -> Language? {
        preferences.language
    }

    /// **Local first, and unconditionally.** The flow continues to Login whether or not the handset
    /// can reach the gateway, and a passenger who chose සිංහල must not be shown an English login
    /// screen because a preference call timed out.
    func choose(_ language: Language) {
        preferences.language = language
        preferences.languagePendingSync = true
    }

    /// Called after sign-in, and best effort: a preference is not worth failing a login over, and
    /// the flag stays set so the next authenticated pass tries again.
    @discardableResult
    func syncPreferences() async -> Bool {
        guard preferences.languagePendingSync else { return true }
        guard let language = preferences.language else {
            // Nothing to send. Clearing the flag is right: a pending sync with no value would retry
            // for ever.
            preferences.languagePendingSync = false
            return true
        }

        do {
            _ = try await iam.setLanguagePreference(request: LanguagePreference(language: language))
            preferences.languagePendingSync = false
            return true
        } catch {
            // Left pending on purpose — see the protocol's note.
            return false
        }
    }
}

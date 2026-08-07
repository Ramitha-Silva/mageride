import Foundation
import MageRideShared

/// SCR-PI-002's state.
///
/// - Parameters:
///   - language: The highlighted row — Sinhala until the passenger says otherwise (AL-26).
///   - slides: What the carousel draws. Never empty: ``FeatureSlides/resolved(_:language:)`` falls
///     back to the bundled three, so the screen has one non-empty list to page over and no branch.
///   - page: Which slide is showing, 0-based. The dots read it.
struct OnboardingState: Equatable {
    var language: Language = LanguageDisplay.default
    var slides: [FeatureSlide] = FeatureSlides.fallback
    var page: Int = 0
}

/// SCR-PI-002 — the three-slide carousel and the language picker.
///
/// **No gating.** BR-25.1: the carousel is *"presentation only"* — a passenger can reach Get Started
/// from slide 1 without swiping, and Skip is the same action minus the reading. Nothing here waits
/// for content-svc, because nothing here needs it: the language boxes are the screen's actual job.
///
/// **Get Started and Skip are one method**, and that is the wireframe read properly. Skip sits above
/// the carousel and the CTA below the language list; neither skips the *language*, because there is
/// nothing to skip — Sinhala is already chosen (AL-26) and ``select(_:)`` has already stored whatever
/// is highlighted. So both doors do exactly one thing.
@MainActor
final class OnboardingModel: ObservableObject {

    @Published private(set) var state = OnboardingState()

    private let onboarding: OnboardingRepository

    /// The last response, kept so a language change re-resolves rather than re-fetches.
    private var serverSlides: [OnboardingSlide] = []

    init(onboarding: OnboardingRepository) {
        self.onboarding = onboarding
        self.state.language = onboarding.selectedLanguage()
    }

    // **There is no `renderingLanguage` here and there is one in `OnboardingViewModel.kt`** — the
    // Android model has to know whether the choice *changed*, because applying it means
    // `Activity.recreate()` and re-creating for no reason would flash the screen. On this platform
    // ``PassengerLocale/apply(_:)`` re-points the bundle and the next view built resolves against
    // it, so applying the same language twice costs a dictionary write. Δ Section C.

    /// Fetches the carousel. Idempotent — `.task` may run again after a scene change.
    func start() async {
        let slides = await onboarding.slides()
        serverSlides = slides
        state.slides = FeatureSlides.resolved(slides, language: state.language)
    }

    /// A language box was tapped.
    ///
    /// Two things happen and both are immediate: the choice is recorded locally (see
    /// ``OnboardingRepository/choose(_:)``) and the app's strings are re-pointed, so the carousel,
    /// the CTA and the rows underneath are all in the new language before the finger leaves the
    /// screen. **That is the whole Section C difference from Android**, which cannot re-inflate
    /// resources without recreating the Activity — see ``PassengerLocale``.
    func select(_ language: Language) {
        onboarding.choose(language)
        PassengerLocale.apply(language)
        state.language = language
        // Re-resolved rather than re-fetched: the payload carries all three languages, which is
        // precisely why the picker is on this screen.
        state.slides = FeatureSlides.resolved(serverSlides, language: language)
    }

    func onPageChanged(_ page: Int) {
        state.page = page
    }

    /// Get Started, and Skip.
    ///
    /// Written **unconditionally** rather than only on a change: ``AppPreferences/firstRunComplete``
    /// is *"SCR-PI-002 has been answered"*, and a passenger who accepted the Sinhala default without
    /// touching a box has answered it. Without this write the router would send them straight back
    /// here on the next cold start.
    ///
    /// The bundle is re-pointed here as well as in ``select(_:)`` so that a passenger who never
    /// touched a box still gets a Sinhala login screen — nothing has redirected anything yet on a
    /// first run, and the default is a *pre-selection* rather than a stored value.
    func finish() {
        onboarding.choose(state.language)
        PassengerLocale.apply(state.language)
    }
}

import Foundation
import MageRideShared

/// One slide of SCR-PI-002's carousel, ready to draw.
///
/// Deliberately not `OnboardingSlide`: that is content-svc's wire shape, carrying a `TrilingualText`
/// per field and an illustration *reference*. This is what the screen needs after a language has
/// been chosen — one string per field, and a symbol it can actually render.
struct FeatureSlide: Equatable, Identifiable {

    /// The slot, 1-based. Also the page index the carousel is keyed on.
    let slot: Int
    let title: String
    let body: String

    /// The illustration panel's own label — see ``FeatureSlides``.
    let caption: String

    /// The SF Symbol standing in for the artwork.
    let symbolName: String

    var id: Int { slot }
}

/// US-1.2's **three-slide tutorial**, and where its copy comes from.
///
/// **The slides are content-svc's** — `GET /v1/content/onboarding/passenger` (AL-28, BR-25.1),
/// public and unauthenticated because it is drawn on a screen that runs before sign-in. All three
/// languages arrive in one answer precisely because the language picker is on this same screen, so
/// switching to සිංහල re-renders from the response rather than re-fetching.
///
/// **``fallback`` is what the screen draws when that call does not land**, and it is not a
/// placeholder: first launch is exactly when a passenger is most likely to be on a bad connection,
/// and a blank carousel above the language boxes would look broken. Its copy is trilingual in
/// `Localizable.strings` like everything else, so the fallback obeys the same rule the server's copy
/// does.
///
/// **The illustration is a symbol, and that is a dependency wall.** `OnboardingSlide.illustrationRef`
/// is *"an app-bundled asset key, or an absolute https URL"* — content-svc serves the reference and
/// never image bytes — and this app ships no remote image loader, so a URL has nothing to render it.
/// The per-slot SF Symbol below is the honest stand-in, and captioning the panel with the ref is more
/// use to whoever wires the real illustrations than a blank box. C077 recorded the same.
enum FeatureSlides {

    /// How many slides SCR-PI-002 shows, whatever content-svc returns. The wireframe draws three
    /// dots; a server that sent four would draw four, and this is only the fallback's length.
    static let count = 3

    /// The bundled copy, in slot order.
    ///
    /// The three things a passenger has to understand before they pick a language: the map is live,
    /// public transport is on it beside the hires, and paying is not a card-only affair.
    ///
    /// **Computed, not a `static let`.** A stored one is resolved once, on first access, and
    /// SCR-PI-002's whole point is that tapping a language re-draws the screen in it — a cached
    /// fallback would leave the carousel in whatever language the app happened to open in while
    /// everything around it changed. Three string lookups per redraw is not a cost worth caching.
    static var fallback: [FeatureSlide] {[
        FeatureSlide(
            slot: 1,
            title: "onboarding_slide_map_title".localised,
            body: "onboarding_slide_map_body".localised,
            caption: "onboarding_slide_map_caption".localised,
            symbolName: symbolName(forSlot: 1)
        ),
        FeatureSlide(
            slot: 2,
            title: "onboarding_slide_transit_title".localised,
            body: "onboarding_slide_transit_body".localised,
            caption: "onboarding_slide_transit_caption".localised,
            symbolName: symbolName(forSlot: 2)
        ),
        FeatureSlide(
            slot: 3,
            title: "onboarding_slide_pay_title".localised,
            body: "onboarding_slide_pay_body".localised,
            caption: "onboarding_slide_pay_caption".localised,
            symbolName: symbolName(forSlot: 3)
        ),
    ]}

    /// The symbol for a slide, by its 1-based `slot`.
    ///
    /// Falls back to the map for a slot content-svc invents beyond the three: a slide with no symbol
    /// would draw an empty panel, and a fourth slide is a content change rather than an error.
    static func symbolName(forSlot slot: Int) -> String {
        switch slot {
        case 2: return "bus.fill"
        case 3: return "creditcard.fill"
        default: return "map.fill"
        }
    }

    /// One server slide, resolved into `language`.
    ///
    /// `TrilingualText`'s Kotlin `operator get` reaches Swift as `get(language:)` — a subscript on a
    /// Kotlin class does not become a Swift subscript, which is the kind of thing that reads as a
    /// typo when this file is put beside `FeatureSlides.kt`.
    static func resolve(_ slide: OnboardingSlide, language: Language) -> FeatureSlide {
        FeatureSlide(
            slot: Int(slide.slot),
            title: slide.title.get(language: language),
            body: slide.body.get(language: language),
            caption: slide.illustrationRef,
            symbolName: symbolName(forSlot: Int(slide.slot))
        )
    }

    /// What the carousel draws: the server's slides when there are any, the bundled three otherwise.
    ///
    /// The one place that decision is made, so the screen has a single non-empty list to page over
    /// and never a branch of its own.
    static func resolved(_ slides: [OnboardingSlide], language: Language) -> [FeatureSlide] {
        guard !slides.isEmpty else { return fallback }
        return slides.map { resolve($0, language: language) }
    }
}

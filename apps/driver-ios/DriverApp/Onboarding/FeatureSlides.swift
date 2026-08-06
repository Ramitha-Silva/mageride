import Foundation

/// One slide of SCR-DI-002's feature carousel.
///
/// - Parameters:
///   - titleKey: The slide's headline.
///   - bodyKey: One sentence under it.
///   - captionKey: The illustration panel's own label.
///   - symbolName: Stands in for the illustration — see ``FeatureSlides``.
struct FeatureSlide: Identifiable {
    let titleKey: String
    let bodyKey: String
    let captionKey: String
    let symbolName: String

    var id: String { titleKey }
}

/// AL-28's **3-slide feature infographic**, mirroring the passenger app's SCR-PA-002.
///
/// BR-25.1: *"a 3-slide feature-infographic carousel (content-svc strings, Si/Ta/En) above the
/// language & city selectors. Presentation only; no gating."* The features it has to land before a
/// driver picks a language are the ones the wireframe's own slide-1 copy names — onboarding,
/// 15-second dispatch, Directional Travel, and the in-app wallet and daily fee.
///
/// ### Why the strings are resources rather than content-svc's
///
/// The same three keys, the same three sentences and the same three icons as
/// `apps/driver-android/.../onboarding/FeatureSlides.kt`, and for a reason that is now a *parity*
/// reason rather than a contract one. C068 shipped bundled copy because `content.yaml` declared no
/// route that served it; **MCS-03 has since added `GET /v1/content/onboarding/{audience}`** and
/// C076's passenger app consumes it. Moving one driver app onto the route and not the other would
/// make the two carousels differ on first run, which is exactly what this component's parity fence
/// forbids — so both move together, in a paired change. Recorded in the C086 handoff.
///
/// **The illustration is an SF Symbol, and that is the same dependency wall C076 hit.**
/// `OnboardingSlide.illustrationRef` is *"an app-bundled asset key, or an absolute https URL"* —
/// content-svc serves the reference and never image bytes — and this target ships no artwork and no
/// remote image loader. The per-slot symbol is the honest stand-in.
enum FeatureSlides {

    /// The three slides, in order. The dots and the pager both count from this list.
    static let all: [FeatureSlide] = [
        FeatureSlide(
            titleKey: "onboarding_slide_earn_title",
            bodyKey: "onboarding_slide_earn_body",
            captionKey: "onboarding_slide_earn_caption",
            symbolName: "megaphone"
        ),
        FeatureSlide(
            titleKey: "onboarding_slide_dispatch_title",
            bodyKey: "onboarding_slide_dispatch_body",
            captionKey: "onboarding_slide_dispatch_caption",
            symbolName: "bolt"
        ),
        FeatureSlide(
            titleKey: "onboarding_slide_wallet_title",
            bodyKey: "onboarding_slide_wallet_body",
            captionKey: "onboarding_slide_wallet_caption",
            symbolName: "wallet.pass"
        ),
    ]
}

import MageRideShared
import SwiftUI

/// **SCR-PI-002 · onboarding + language** — US-1.2's three-slide tutorial and US-1.3's picker.
///
/// The wireframe's column, top to bottom: a `Skip` action in the nav row, the illustration panel,
/// the headline and its sentence, the dots, a `Language` label, three selectable rows, **a spacer**,
/// and `Get Started`.
///
/// **Get Started is pinned to the bottom, below the language rows** — the cell's own `Δ iOS` clause
/// says so in as many words (*"pinned to the bottom of the screen (safe-area inset, below the
/// language rows after the spacer)"*) and the component prompt makes it a fence. The `Spacer()` above
/// it is what makes "pinned" true at every supported height rather than only at the one the
/// wireframe was drawn at; C077 made the same call with a weighted `Box`.
///
/// **D2' §SCR-PA-002 disagrees with the wireframe on two counts and the wireframe wins**: its ASCII
/// sketch draws the CTA *above* the language row, and its component table names a `SegmentedButton`
/// where US-1.3 asks for *"vertical selectable boxes, one per row"*. The wireframes are the
/// team-approved baseline; C077 recorded the same conflict and it still needs a micro-change-set.
///
/// **`TabView(.page)` is the carousel** — the cell's own clause again — with the system's own index
/// dots turned off, because the wireframe draws an 18pt `primary` pill for the active one and the
/// system draws a circle in a colour a `TabView` decides.
@MainActor
struct OnboardingScreen: View {

    @StateObject private var model: OnboardingModel

    private let onContinue: () -> Void

    init(repository: OnboardingRepository, onContinue: @escaping () -> Void) {
        _model = StateObject(wrappedValue: OnboardingModel(onboarding: repository))
        self.onContinue = onContinue
    }

    var body: some View {
        VStack(spacing: MageRideSpacing.sm) {
            skip

            carousel

            PageDots(count: model.state.slides.count, current: model.state.page)

            SectionLabel(key: "onboarding_language_label", alignment: .center)
                .padding(.top, MageRideSpacing.xxs)

            languages

            // The fence. Everything above sits at the top of the screen; Get Started sits at the
            // bottom whatever the handset's height.
            Spacer(minLength: MageRideSpacing.md)

            Button(action: proceed) {
                Text(key: "onboarding_get_started")
            }
            .buttonStyle(.mageCta)
        }
        .padding(.horizontal, MageRideSpacing.md)
        .padding(.bottom, MageRideSpacing.md)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.surface)
        .task { await model.start() }
    }

    /// The wireframe's `.navtop` — a spacer and a trailing `Skip`.
    ///
    /// Skip and Get Started are the **same action**: neither skips the language, because there is
    /// nothing to skip (AL-26 pre-selects Sinhala and every tap has already been stored). See
    /// ``OnboardingModel/finish()``.
    private var skip: some View {
        HStack {
            Spacer()
            TextLink(key: "onboarding_skip", action: proceed)
        }
    }

    /// **The pages are walked by index rather than by `enumerated()`** (Δ C101).
    ///
    /// This loop was `ForEach(Array(model.state.slides.enumerated()), id: \.element.id)`, and
    /// `\.element` is a key path into a **tuple** — which the C087 finding this repository carries
    /// says does not compile, and which `ModeFilterSheet`, `PaymentMethodScreen` and
    /// `HistoryControls` all deliberately avoid for that reason. No host has built this target, so
    /// nothing has settled which reading is right; walking indices is correct under either, which is
    /// why it is written this way rather than left for a macOS runner to adjudicate. (`apps/driver-ios`
    /// has six more of the same shape — that target's to settle, not this one's.)
    ///
    /// The index cannot simply be dropped: `.tag(index)` is what `pageBinding` selects on, so a page
    /// is identified by its position rather than by its slide.
    private var carousel: some View {
        TabView(selection: pageBinding) {
            ForEach(model.state.slides.indices, id: \.self) { index in
                slide(model.state.slides[index])
                    .tag(index)
                    // One announcement per slide: VoiceOver reads the headline and its sentence
                    // together rather than as two elements a reader has to assemble (US-19.1/19.2).
                    .accessibilityElement(children: .combine)
            }
        }
        .tabViewStyle(.page(indexDisplayMode: .never))
        .frame(height: carouselHeight)
    }

    private func slide(_ slide: FeatureSlide) -> some View {
        VStack(spacing: MageRideSpacing.sm) {
            IllustrationPanel(
                symbolName: slide.symbolName,
                caption: slide.caption,
                height: MageRideControl.illustrationPanel
            )

            Text(slide.title)
                .mageFont(.headline)
                .foregroundStyle(MageRideColor.onSurface)
                .multilineTextAlignment(.center)

            Text(slide.body)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)

            Spacer(minLength: 0)
        }
    }

    /// US-1.3's rows, in the order the story fixes: Sinhala, Tamil, English.
    private var languages: some View {
        VStack(spacing: MageRideSpacing.xs) {
            ForEach(LanguageDisplay.choices, id: \.self) { language in
                SelectionRow(
                    label: LanguageDisplay.endonym(language),
                    secondary: LanguageDisplay.englishName(language),
                    isSelected: language == model.state.language
                ) {
                    // `.selection` on a language change — the cell's own clause. The haptic is the
                    // acknowledgement, because the row's own highlight moves at the same instant the
                    // whole screen re-renders in the new language and the eye has nothing to fix on.
                    UISelectionFeedbackGenerator().selectionChanged()
                    model.select(language)
                }
            }
        }
    }

    private var pageBinding: Binding<Int> {
        Binding(get: { model.state.page }, set: { model.onPageChanged($0) })
    }

    private func proceed() {
        model.finish()
        onContinue()
    }

    /// Room for the panel, a two-line headline and a two-line sentence at the largest Dynamic Type
    /// size the wireframe's layout survives.
    ///
    /// A `TabView` needs a bound height — it fills whatever it is given — so this is a measurement of
    /// one control rather than spacing between two, which is why it is not a ``MageRideSpacing``
    /// token. It is the only fixed height on this screen; everything else grows with the text.
    private let carouselHeight: CGFloat = 260
}

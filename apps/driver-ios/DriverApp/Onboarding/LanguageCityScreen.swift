import MageRideShared
import SwiftUI

/// **SCR-DI-002 · language / city** — first run only.
///
/// Top to bottom, exactly as the wireframe draws it: a large "Welcome" title, the AL-28 three-slide
/// carousel with its paging dots, the AL-26 vertical language boxes (**Sinhala first and
/// selected**), the AL-27 operating-city radio list loaded from `config.operating_cities`, and the
/// Continue CTA pinned under a spacer on the grouped background.
///
/// **Δ Section C:** the carousel is a paged `TabView`, where Android's is a `HorizontalPager`. Same
/// three slides, same dots, same "presentation only, no gating" (BR-25.1).
///
/// `@MainActor` on the whole view, not on its initialiser: every member here reads a `@MainActor`
/// model, and annotating the type once is what keeps a helper added later from being the one
/// non-isolated member that stops compiling when C103 raises `SWIFT_STRICT_CONCURRENCY`.
@MainActor
struct LanguageCityScreen: View {

    @StateObject private var model: LanguageCityModel
    @State private var slide = 0

    private let onContinue: () -> Void

    init(repository: OnboardingRepository, onContinue: @escaping () -> Void) {
        _model = StateObject(wrappedValue: LanguageCityModel(repository: repository))
        self.onContinue = onContinue
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                    carousel

                    SectionLabel(key: "onboarding_language_label")
                    languageBoxes

                    SectionLabel(key: "onboarding_city_label")
                    cityPicker

                    Button(action: cont) {
                        Text(key: "action_continue")
                    }
                    .buttonStyle(.mageCta)
                    .disabled(!model.state.canContinue)
                    .padding(.top, MageRideSpacing.xs)
                }
                .padding(MageRideSpacing.md)
            }
            .background(MageRideColor.surface)
            .navigationTitle(Text(key: "onboarding_welcome_title"))
            .navigationBarTitleDisplayMode(.large)
        }
        .task { await model.loadCities() }
    }

    // MARK: - The carousel (AL-28)

    /// Three client-paged slides, swipeable, with the wireframe's own dots.
    ///
    /// `.page(indexDisplayMode: .never)` and ``PageDots`` rather than the system indicator: the
    /// wireframe's active dot is a `primary` pill, and a `TabView`'s own is a circle in a colour it
    /// chooses. Presentation only — a driver who never swipes still reaches the selectors below,
    /// which is BR-25.1's explicit rule.
    private var carousel: some View {
        VStack(spacing: MageRideSpacing.xs) {
            TabView(selection: $slide) {
                ForEach(Array(FeatureSlides.all.enumerated()), id: \.offset) { index, feature in
                    VStack(spacing: MageRideSpacing.xs) {
                        IllustrationPanel(symbolName: feature.symbolName, captionKey: feature.captionKey)
                        Text(key: feature.titleKey)
                            .mageFont(.title)
                            .foregroundStyle(MageRideColor.onSurface)
                            .multilineTextAlignment(.center)
                        Text(key: feature.bodyKey)
                            .mageFont(.bodySmall)
                            .foregroundStyle(MageRideColor.onSurfaceVariant)
                            .multilineTextAlignment(.center)
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, MageRideSpacing.xxs)
                    .tag(index)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            // A paged `TabView` has no intrinsic height and collapses to nothing inside a
            // `ScrollView`; the frame is what the three slides are laid out in.
            .frame(height: 260)

            PageDots(count: FeatureSlides.all.count, current: slide)
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: - Language (AL-26)

    private var languageBoxes: some View {
        GroupedList {
            ForEach(Array(model.state.languages.enumerated()), id: \.offset) { index, language in
                SelectionRow(
                    label: LanguageDisplay.endonym(language),
                    secondary: LanguageDisplay.englishName(language),
                    isSelected: language == model.state.language,
                    showsSeparator: index < model.state.languages.count - 1,
                    onSelect: { model.select(language: language) }
                )
            }
        }
    }

    // MARK: - Operating city (AL-27, US-1.3a)

    /// The city radio list, and the two states a network-loaded list has that a hard-coded one does
    /// not.
    ///
    /// A failure is offered as Retry rather than swallowed into an empty list: an empty city picker
    /// and an unreachable gateway look identical to a driver, and only one of them is worth waiting
    /// out.
    @ViewBuilder
    private var cityPicker: some View {
        if model.state.isLoadingCities {
            ProgressView()
                .frame(maxWidth: .infinity)
                .padding(.vertical, MageRideSpacing.sm)
        } else if model.state.citiesFailed {
            VStack(alignment: .leading, spacing: MageRideSpacing.xs) {
                FormErrorText(messageKey: "onboarding_city_load_failed")
                Button(action: { Task { await model.loadCities() } }) {
                    Text(key: "action_retry")
                        .mageFont(.bodyEmphasis)
                        .foregroundStyle(MageRideColor.primary)
                }
            }
        } else {
            GroupedList {
                ForEach(Array(model.state.cities.enumerated()), id: \.offset) { index, city in
                    SelectionRow(
                        label: city.name(language: model.state.language),
                        isSelected: city.code == model.state.cityCode,
                        showsSeparator: index < model.state.cities.count - 1,
                        onSelect: { model.select(cityCode: city.code) }
                    )
                }
            }
        }
    }

    /// Stores both answers, applies the language, and moves on to SCR-DI-003.
    private func cont() {
        model.confirm()
        onContinue()
    }
}

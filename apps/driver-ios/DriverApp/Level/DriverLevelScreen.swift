import MageRideShared
import SwiftUI

/// **SCR-DI-019 · Driver Level & stats** (US-6A.6, US-6A.14).
///
/// The wireframe, top to bottom: a `‹` navigation bar, the big `L3` badge in a `primaryContainer`
/// square, the points line, the progress bar, the **Acceptance** / **No-shows** pair, and the warning
/// that three passenger reports cost a level and a temporary delisting.
///
/// Read ``DriverLevelModel`` before changing the points line or the warning — both differ from the
/// wireframe's sample text for reasons that are recorded, not accidental.
///
/// Opened from SCR-DI-010's `L3` badge, which is the only entry point D2' names for this screen and
/// which C088 already wired. The wireframe's own `‹ Profile` is the back label a *different* entry
/// point would produce; on this platform the label is the previous screen's title and the system
/// draws it.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct DriverLevelScreen: View {

    @StateObject private var model: DriverLevelModel

    init(model: @autoclosure @escaping () -> DriverLevelModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                LevelBadge(level: model.state.level)
                pointsLine
                LevelProgressBar(progress: model.state.progress)

                HStack(spacing: MageRideSpacing.xs) {
                    MetricCard(
                        labelKey: "level_acceptance",
                        value: model.state.acceptancePercent.map(LevelLabels.percent) ?? LevelLabels.unknown
                    )
                    MetricCard(
                        labelKey: "level_no_shows",
                        value: model.state.noShows.map(String.init) ?? LevelLabels.unknown
                    )
                }

                delistingWarning
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "level_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.refresh() }
    }

    /// *"310 / 500 points to Level 3"*, or *"Level 3 is the highest level"* at the cap.
    ///
    /// D5' §4.2 stops at three; the wireframe's *"→ Level 4"* does not exist. See ``DriverLevelModel``.
    private var pointsLine: some View {
        Text(pointsText)
            .mageFont(.title)
            .foregroundStyle(MageRideColor.onSurface)
            .multilineTextAlignment(.center)
    }

    private var pointsText: String {
        guard model.state.level != nil else { return "level_loading".localised }
        guard let next = model.state.nextLevel else {
            return "level_top".localisedFormat(Int(DriverLevelRules.companion.MAX_LEVEL))
        }
        return "level_points_to_next".localisedFormat(model.state.points, model.state.threshold, next)
    }

    /// D5' §4.2's level-down rule, said out loud.
    ///
    /// **Always shown, because it can only be the rule.** No app-facing read carries the report count
    /// — see ``DriverLevelModel`` — so this cannot become "you are on your second report", and a
    /// banner that appeared only sometimes would imply it had.
    private var delistingWarning: some View {
        DashboardBanner(
            text: "level_reports_warning".localisedFormat(Int(DriverLevelRules.companion.REPORTS_PER_LEVEL_DOWN)),
            accent: MageRideColor.warning,
            symbolName: "exclamationmark.triangle.fill"
        )
        .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }
}

/// The 96pt `primaryContainer` square with `L3` in it. `—` while reputation has not answered.
private struct LevelBadge: View {

    let level: Int?

    var body: some View {
        Text(level.map(DashboardLabels.level) ?? LevelLabels.unknown)
            .mageFont(.display)
            .foregroundStyle(MageRideColor.primary)
            .frame(width: MageRideControl.levelBadge, height: MageRideControl.levelBadge)
            .background(
                MageRideColor.primaryContainer,
                in: RoundedRectangle(cornerRadius: MageRideRadius.card, style: .continuous)
            )
            .accessibilityElement(children: .combine)
    }
}

/// The wireframe's 8pt bar. D2' §SCR-DI-019's animation is *"progress fill"*, so it animates.
///
/// A drawn capsule rather than a `ProgressView(value:)`, for ``StepProgress``' reason: the wireframe
/// fixes the track colour and the height, and `ProgressView`'s linear style takes both from the
/// platform.
private struct LevelProgressBar: View {

    let progress: Double

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(MageRideColor.surfaceVariant)
                Capsule()
                    .fill(MageRideColor.primary)
                    .frame(width: proxy.size.width * min(max(progress, 0), 1))
            }
        }
        .frame(height: MageRideControl.levelProgress)
        .animation(.easeOut(duration: 0.3), value: progress)
        .accessibilityHidden(true)
    }
}

/// The two values on this screen that are **symbols rather than sentences**.
///
/// An em dash means "we have not been told" and `92%` is a number with a sign after it, in all three
/// languages alike; three identical entries in the three `Localizable.strings` files is exactly what
/// `LocalizationTests` fails on. Same rule `Rs`, `+94` and `L3` follow.
enum LevelLabels {

    /// See ``MageRideSymbols/unknown`` — the same em dash, not a second one.
    static let unknown = MageRideSymbols.unknown

    /// `92` → `92%` — US-6A.14's acceptance rate.
    static func percent(_ value: Int) -> String { "\(value)%" }
}

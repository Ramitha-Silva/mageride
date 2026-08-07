import Charts
import MageRideShared
import SwiftUI

/// The three windows SCR-DI-020 offers, in the order the wireframe prints them.
///
/// `EarningsPeriod.entries` is Kotlin's and does not cross the bridge as anything a Swift `for` can
/// use, so the order is written out here — the same shape ``VehicleOnboardingSteps`` uses for the
/// wizard's four steps. It is not a second source of truth: `EarningsPeriodTests` asserts this table
/// against the shared enum's own `ordinal`s, so a fourth window added to `query.yaml` fails a test
/// here rather than quietly losing a tab.
enum EarningsPeriods {

    static let all: [EarningsPeriod] = [
        EarningsPeriod.today,
        EarningsPeriod.week,
        EarningsPeriod.month,
    ]
}

extension EarningsPeriod {

    /// The trilingual name of a period tab.
    var labelKey: String {
        switch self {
        case EarningsPeriod.week: return "earnings_period_week"
        case EarningsPeriod.month: return "earnings_period_month"
        // `today`, and the arm a Kotlin enum forces on every Swift `switch` over one.
        default: return "earnings_period_today"
        }
    }
}

/// The wireframe's `tabbar2` — **Today · Week · Month**.
///
/// **Δ Section C, and the wireframe is what says so.** D2' §SCR-DA-020 gives Android a `TabRow`;
/// `driver_ios.html` draws `.tabbar2` as `background:var(--surfaceVariant); border-radius:9px` with
/// a white, shadowed pill on the selected entry — which is `UISegmentedControl`, not a tab strip. So
/// this is a `Picker` in the segmented style, and its selected-pill colour is the platform's own for
/// the reason §0.2 gives about the green `Toggle` and the blue `.alert` actions: where the wireframe
/// draws a genuine HIG control, the app uses the platform's rendering of it rather than repainting
/// one in MageRide's palette.
struct EarningsPeriodTabs: View {

    let selected: EarningsPeriod
    let onSelect: (EarningsPeriod) -> Void

    var body: some View {
        Picker(
            selection: Binding(get: { selected }, set: onSelect),
            label: Text(key: "earnings_title")
        ) {
            ForEach(EarningsPeriods.all, id: \.ordinal) { period in
                Text(key: period.labelKey).tag(period)
            }
        }
        .pickerStyle(.segmented)
    }
}

/// The wireframe's `.bars` — the earnings trend, drawn as bars rather than as a line.
///
/// **Swift Charts, which is `driver_ios.html`'s own `Δ iOS` clause for this cell** (*"Swift Charts
/// trend"*), and the one place this cluster is not a transcription of its Android twin: that one
/// draws the bars by hand out of Compose primitives, because D2' §SCR-DA-020 offers `Canvas`/Vico and
/// neither is a first-party control. On this platform the first-party control exists, is available on
/// the 16.0 floor, and brings the axis, the Dynamic Type layout and the VoiceOver chart rotor with
/// it. The specification of the control is unchanged: height ∝ value, one bucket highlighted.
///
/// **Heights are relative to the biggest bucket, not to an absolute rupee scale** — Swift Charts'
/// own default domain, which is the same rule the Android twin implements by dividing by the peak. A
/// driver reading this wants to see which hour or day was the good one; a fixed scale would flatten a
/// quiet week into nothing.
///
/// An empty bucket draws no bar and **keeps its place on the axis**, which is what the Android twin
/// spends a `MIN_BAR` sliver on: a categorical x-axis lists every bucket it was given, so a quiet
/// hour is a labelled gap rather than an hour that vanished.
struct EarningsChart: View {

    let buckets: [EarningsBucket]

    /// The axis tick label, at §0.2's caption size on §0.2's caption Dynamic Type curve.
    ///
    /// `.mageFont(_:)` cannot be used here: `AxisValueLabel` is a chart component rather than a
    /// `View`, so it takes a `Font` value and not a modifier. This is the same `@ScaledMetric` the
    /// modifier is built on, applied one level up — both halves of §0.2's type row still hold, which
    /// is the whole point of that file's rule.
    @ScaledMetric private var labelSize: CGFloat

    init(buckets: [EarningsBucket]) {
        self.buckets = buckets
        _labelSize = ScaledMetric(
            wrappedValue: MageRideTextRole.caption.size,
            relativeTo: MageRideTextRole.caption.textStyle
        )
    }

    var body: some View {
        if buckets.isEmpty {
            EmptyView()
        } else {
            chart
        }
    }

    private var chart: some View {
        Chart(buckets) { bucket in
            BarMark(
                x: .value("earnings_chart_bucket".localised, bucket.label),
                y: .value("earnings_net".localised, bucket.plottable)
            )
            .foregroundStyle(bucket.isCurrent ? MageRideColor.primary : MageRideColor.primaryContainer)
            .cornerRadius(MageRideRadius.sm)
        }
        // The wireframe's `.bars` has no y-axis and no gridlines — it is a shape, not a report. The
        // figures underneath it are the card, and they are query-svc's own.
        .chartYAxis(.hidden)
        .chartXAxis {
            AxisMarks { _ in
                AxisValueLabel()
                    .font(.system(size: labelSize, weight: MageRideTextRole.caption.weight))
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
        }
        .frame(height: MageRideControl.earningsChart)
        .accessibilityLabel(Text(key: "earnings_chart_label"))
    }
}

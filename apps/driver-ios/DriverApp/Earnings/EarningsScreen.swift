import MageRideShared
import SwiftUI

/// **SCR-DI-020 · the earnings dashboard** (US-9.22, US-8.8).
///
/// The wireframe, top to bottom: the large title *"Earnings"*, the **Today / Week / Month** segmented
/// control, the period's net in display type, the bar chart, and the card that shows what the net is
/// made of — fares received, the daily fee deducted in `error`, and the bold **Net** line. The
/// per-trip rows D2' lists in its component table follow underneath.
///
/// Read ``EarningsModel`` before adding a figure: the summary is query-svc's and is printed as sent,
/// and the payment-method split the deliverable asks for has no read behind it.
///
/// Opened from SCR-DI-010's *"Today: 4 trips · Rs 3,180"* line — which **is** this screen's Today
/// figure, the same `EarningsSummary.netMinor` through the same endpoint — and C088 already wired it.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct EarningsScreen: View {

    @StateObject private var model: EarningsModel

    init(model: @autoclosure @escaping () -> EarningsModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                EarningsPeriodTabs(selected: model.state.period) { period in
                    Task { await model.select(period) }
                }

                if let errorKey = model.state.errorKey {
                    DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                        .onTapGesture(perform: model.dismissError)
                }

                periodTotal
                EarningsChart(buckets: model.state.buckets)
                breakdownCard

                if model.state.isEmptyPeriod {
                    Text(key: "earnings_empty")
                        .mageFont(.body)
                        .foregroundStyle(MageRideColor.onSurfaceVariant)
                        .multilineTextAlignment(.center)
                        .padding(MageRideSpacing.lg)
                        .frame(maxWidth: .infinity)
                } else {
                    SectionLabel(key: "earnings_trips")
                    ForEach(model.state.trips, id: \.tripId) { trip in
                        TripRow(trip: trip)
                    }
                }
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "earnings_title"))
        .navigationBarTitleDisplayMode(.large)
        .task { await model.refresh() }
    }

    /// *"Today — what you keep · Rs 3,180"*. The label names the selected window, not the day.
    private var periodTotal: some View {
        VStack(spacing: 1) {
            Text("earnings_net_for".localisedFormat(model.state.period.labelKey.localised))
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(MoneyFormat.rupees(model.state.netMinor))
                .mageFont(.display)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
    }

    /// What the net is made of.
    ///
    /// The wireframe prints three lines — fares received, the daily fee, the net. Penalties
    /// (D5' §7.1) and tips (E-10) are two more fields `EarningsSummary` carries, and each is shown
    /// **only when the server sent a non-zero one**: a driver who has never been penalised should not
    /// have a penalty line on their dashboard, and a driver who has must not have it folded silently
    /// into the net.
    private var breakdownCard: some View {
        VStack(spacing: MageRideSpacing.xxs) {
            MoneyRow(
                label: "earnings_fares_received".localisedFormat(model.state.tripCount),
                value: MoneyFormat.rupees(model.state.grossMinor)
            )

            if model.state.tipMinor > 0 {
                MoneyRow(label: "earnings_tips".localised, value: MoneyFormat.rupees(model.state.tipMinor))
            }

            if model.state.dailyFeeMinor > 0 {
                MoneyRow(
                    label: "earnings_daily_fee".localised,
                    value: MoneyFormat.rupees(-model.state.dailyFeeMinor),
                    accent: MageRideColor.error
                )
            }

            if model.state.penaltyMinor > 0 {
                MoneyRow(
                    label: "earnings_penalties".localised,
                    value: MoneyFormat.rupees(-model.state.penaltyMinor),
                    accent: MageRideColor.error
                )
            }

            Divider().overlay(MageRideColor.outlineVariant)

            MoneyRow(
                label: "earnings_net".localised,
                value: MoneyFormat.rupees(model.state.netMinor),
                isEmphasised: true
            )
        }
        .padding(MageRideSpacing.sm)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }
}

/// The wireframe's `kv` — a label on the left and an amount on the right.
private struct MoneyRow: View {

    let label: String
    let value: String
    var accent: Color?
    var isEmphasised = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            Text(label)
                .mageFont(isEmphasised ? .bodyEmphasis : .bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(value)
                .mageFont(isEmphasised ? .bodyEmphasis : .bodySmall)
                .foregroundStyle(accent ?? MageRideColor.onSurface)
        }
        .accessibilityElement(children: .combine)
    }
}

/// One completed trip (US-8.8).
///
/// The time is the trip's **Colombo** end time, for the same reason the chart's buckets are — the
/// period the card totalled is a Colombo one.
private struct TripRow: View {

    let trip: SessionEarning

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            Text(ScheduleLabels.time(trip.endedAt))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(MoneyFormat.rupees(trip.grossMinor))
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .frame(maxWidth: .infinity, alignment: .trailing)
            Text(MoneyFormat.rupees(trip.netMinor))
                .mageFont(.title)
                .foregroundStyle(MageRideColor.onSurface)
        }
        .padding(.vertical, MageRideSpacing.xxs)
        .accessibilityElement(children: .combine)
    }
}

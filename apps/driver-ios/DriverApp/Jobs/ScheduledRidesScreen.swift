import MageRideShared
import SwiftUI

/// **SCR-DI-018 · scheduled rides** (US-6A.15).
///
/// The wireframe: a `‹ Jobs` navigation bar over a list of cards, the imminent one outlined in
/// `primary` with an amber *"in 28 min"* pill and the note that its reminder has fired, the rest
/// carrying a blue *"Accepted"* pill and their vehicle type.
///
/// **A no-show here costs a Driver Level** (US-6A.7), which is why the imminent card is the loud one.
///
/// A pushed destination with the system's own `‹` back button, which is the wireframe's `‹ Jobs`.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct ScheduledRidesScreen: View {

    @StateObject private var model: ScheduledRidesModel

    init(model: @autoclosure @escaping () -> ScheduledRidesModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        VStack(spacing: 0) {
            if let errorKey = model.state.errorKey {
                DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                    .onTapGesture(perform: model.dismissError)
            }

            if model.state.isEmpty {
                Text(key: "scheduled_empty")
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
                    .padding(MageRideSpacing.lg)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: MageRideSpacing.xs) {
                        ForEach(model.state.rows) { row in
                            ScheduledCard(row: row) { Task { await model.cancel(row) } }
                        }
                    }
                    .padding(MageRideSpacing.sm)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "scheduled_title"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            model.start()
            await model.refresh()
        }
        .onDisappear(perform: model.stop)
    }
}

/// One upcoming ride.
///
/// The imminent card is outlined in `primary` — the wireframe's `box-shadow:inset 0 0 0 2px
/// var(--primary)` — which is the only styling difference between the two rows it draws. Everything
/// else about the card is the same, deliberately: a driver scanning the list is looking for *which*
/// is next, not for two kinds of card.
private struct ScheduledCard: View {

    let row: ScheduledRideRow
    let onCancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(headline)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                    .frame(maxWidth: .infinity, alignment: .leading)

                pill
            }

            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(subtitle)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .frame(maxWidth: .infinity, alignment: .leading)

                // Disabled from T-30: the ride exists by then and ride-svc's cancel — with its
                // penalty matrix — is the only door left (D5' §7).
                Button(action: onCancel) {
                    Text(key: "scheduled_cancel")
                        .mageFont(.bodySmall)
                        .foregroundStyle(MageRideColor.primary)
                        .frame(minHeight: MageRideControl.minimumTapTarget)
                }
                .buttonStyle(.plain)
                .disabled(!row.isScheduled || row.isCancelling)
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surface,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
                .strokeBorder(
                    row.hasReminderFired ? MageRideColor.primary : MageRideColor.outlineVariant,
                    lineWidth: row.hasReminderFired ? 2 : MageRideControl.hairline
                )
        }
    }

    /// *"Today 08:30 · Maharagama → Fort"*.
    private var headline: String {
        let day: String
        switch ScheduleLabels.day(row.ride.pickupTime, now: nowTimestamp) {
        case .today: day = "schedule_today".localised
        case .tomorrow: day = "schedule_tomorrow".localised
        case .on(let text): day = text
        }

        return day + " " + ScheduleLabels.time(row.ride.pickupTime)
            + MageRideSymbols.separator
            + ScheduleLabels.route(pickup: row.ride.pickup, dropoff: row.ride.dropoff)
    }

    /// *"Sedan · reminder sent"*.
    ///
    /// The wireframe also prints the fare (`Sedan · Rs 980 · reminder fired`). **`ScheduledRide`
    /// carries no fare on any read** — the C072 handoff's spec gap 1 — so the type and the reminder
    /// are what is left, and a number is not invented to fill the gap.
    private var subtitle: String {
        let type = row.ride.vehicleType.labelKey.localised
        let reminder = row.hasReminderFired ? "scheduled_reminder_fired".localised : nil
        return [type, reminder].compactMap { $0 }.joined(separator: MageRideSymbols.separator)
    }

    /// The amber countdown while the reminder window is open, the blue *"Accepted"* pill before it.
    @ViewBuilder
    private var pill: some View {
        if row.hasReminderFired {
            StatusPill(label: "scheduled_in_minutes".localisedFormat(row.minutesToPickup), tone: .pending)
        } else {
            StatusPill(label: "scheduled_accepted".localised, tone: .info)
        }
    }

    /// The row's own clock, which the model advances once a second — so *"Today"* turns over on the
    /// same tick the countdown does rather than on whenever this view happens to be rebuilt.
    private var nowTimestamp: Timestamp {
        IosInstantKt.timestampFromEpochMillis(millis: Int64(row.at.timeIntervalSince1970 * 1000))
    }
}

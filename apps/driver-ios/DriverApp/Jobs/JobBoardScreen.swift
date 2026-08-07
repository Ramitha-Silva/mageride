import MageRideShared
import SwiftUI

/// **SCR-DI-017 · the Job Board** (US-6A.5, US-6A.8, D-06).
///
/// The wireframe, top to bottom: the large title *"Job Board"*, then one card per future scheduled
/// ride — the pickup time and route above the fare, the day, distance and vehicle type below it, and
/// on the right either the **Post intent** link or the *"Intent posted ✓"* pill. Tab 2, so it has no
/// back button and the tab bar stays under it.
///
/// **There is no accept button and there must not be one.** Acceptance happens at T-30 min on
/// SCR-DI-014, where the ride arrives as an ordinary offer; see ``JobBoardModel``.
///
/// - Parameter onOpenScheduled: SCR-DI-018. The board is what a driver bids on and the upcoming list
///   is what came of it, so the two are one tap apart. The wireframe draws SCR-DI-018 with a
///   `‹ Jobs` navigation bar and no screen that opens it — the C072 handoff's wireframe gap, closed
///   the same way here.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct JobBoardScreen: View {

    @StateObject private var model: JobBoardModel

    private let onOpenScheduled: () -> Void

    init(model: @autoclosure @escaping () -> JobBoardModel, onOpenScheduled: @escaping () -> Void) {
        _model = StateObject(wrappedValue: model())
        self.onOpenScheduled = onOpenScheduled
    }

    var body: some View {
        VStack(spacing: 0) {
            if let errorKey = model.state.errorKey {
                DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                    .onTapGesture(perform: model.dismissError)
            }

            content
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "job_board_title"))
        .navigationBarTitleDisplayMode(.large)
        .toolbar { toolbar }
        .task {
            model.start()
            await model.refresh()
        }
        .onDisappear(perform: model.stop)
    }

    /// The four things this screen can be showing, and only ever one of them.
    ///
    /// The order matters: the gate is checked before the *"we could not read your level"* notice,
    /// because `isUnavailable` is *"no answer"* and the gate is an answer.
    @ViewBuilder
    private var content: some View {
        if model.state.isLoading {
            BoardNotice(text: "job_board_loading".localised, isSpinning: true)
        } else if model.state.isGated == true {
            // US-6A.8 — a gate, not an error. Level 1 keeps immediate Mode C; what it loses is this
            // board, and the copy names the level that opens it again.
            BoardNotice(text: "job_board_level_gate".localisedFormat(model.state.minimumLevel))
        } else if model.state.isUnavailable {
            // The level did not answer, so neither the gate nor the list is a truthful thing to
            // draw. Never the gate copy — that would tell a Level-3 driver they are Level 1.
            BoardNotice(text: "job_board_unavailable".localised)
        } else if model.state.isEmpty {
            BoardNotice(text: "job_board_empty".localisedFormat(JobBoardScreen.catchment))
        } else {
            board
        }
    }

    /// The board itself. Soonest pickup first — see ``JobBoardModel`` on what §3.7's ranking is.
    private var board: some View {
        ScrollView {
            LazyVStack(spacing: MageRideSpacing.xs) {
                ForEach(model.state.rows) { row in
                    JobCard(row: row) { Task { await model.postIntent(row) } }
                }
            }
            .padding(MageRideSpacing.sm)
        }
    }

    /// The catchment, and the way to the driver's own upcoming list.
    ///
    /// The radius label is the Android app bar's *"≤ 30 km"* — `driver_ios.html` prints the same
    /// figure in SCR-DI-017's own state notes and this is where a large-title screen has room for it.
    /// It sits beside the action rather than replacing it.
    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItem(placement: .navigationBarTrailing) {
            Text("job_board_radius".localisedFormat(JobBoardScreen.catchment))
                .mageFont(.label)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
        }
        ToolbarItem(placement: .navigationBarTrailing) {
            Button(action: onOpenScheduled) {
                Image(systemName: "calendar")
            }
            .accessibilityLabel(Text(key: "scheduled_title"))
        }
    }

    /// `30 km`, from `:shared`'s own D-06 constant rather than a number typed here.
    private static var catchment: String {
        MoneyFormat.radius(metres: Int(JobBoard.companion.CATCHMENT_METRES))
    }
}

/// One board row.
///
/// **The expired card fades rather than vanishing** (D2' §SCR-DI-017's *"card expire fade"*). The row
/// leaves the list a beat later, from the model, so what a driver sees is a job going out of reach
/// rather than a tap that lost them one.
private struct JobCard: View {

    let row: JobBoardRow
    let onPostIntent: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(headline)
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)
                    .frame(maxWidth: .infinity, alignment: .leading)

                // The wireframe prints `Rs 980` here. **`ScheduledRide` carries no fare on any
                // read** — see the C072 handoff, spec gap 1 — so the slot is left empty rather than
                // filled with an estimate the passenger was never quoted.
            }

            HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
                Text(subtitle)
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .frame(maxWidth: .infinity, alignment: .leading)

                JobAction(row: row, onPostIntent: onPostIntent)
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surface,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
        .opacity(row.isExpired ? JobCard.expiredOpacity : 1)
        .animation(.easeOut(duration: 0.3), value: row.isExpired)
    }

    /// *"08:30 · Maharagama → Fort"*.
    private var headline: String {
        ScheduleLabels.time(row.ride.pickupTime)
            + MageRideSymbols.separator
            + ScheduleLabels.route(pickup: row.ride.pickup, dropoff: row.ride.dropoff)
    }

    /// *"Tomorrow · 11.2 km · Sedan"* — the day, how far the pickup is, and the booked type.
    private var subtitle: String {
        let day: String
        switch ScheduleLabels.day(row.ride.pickupTime, now: JobCard.now()) {
        case .today: day = "schedule_today".localised
        case .tomorrow: day = "schedule_tomorrow".localised
        case .on(let text): day = text
        }

        let distance = row.ride.distanceM.map { MoneyFormat.distance(metres: Double($0.int32Value)) }
        let type = row.ride.vehicleType.labelKey.localised

        return [day, distance, type].compactMap { $0 }.joined(separator: MageRideSymbols.separator)
    }

    /// The wall clock, for the Today/Tomorrow label only.
    ///
    /// Deliberately not the model's injected clock: what that one exists for is the **T-30 rule**,
    /// and threading it into a card so a caption can say "Tomorrow" would make a label look like a
    /// decision. A day boundary that turns over while a card is on screen re-renders on the next
    /// tick anyway, because the model reappraises every row once a second.
    private static func now() -> Timestamp {
        IosInstantKt.timestampFromEpochMillis(millis: Int64(Date().timeIntervalSince1970 * 1000))
    }

    /// How much of an expired card is left once it has faded (D2' §SCR-DI-017's expire animation).
    private static let expiredOpacity: Double = 0.38
}

/// The card's single control.
///
/// **Post intent** while the window is open, the *"Intent posted ✓"* pill once this driver has bid,
/// and the *"Closed"* pill once T-30 has passed. Nothing on this card accepts a ride.
private struct JobAction: View {

    let row: JobBoardRow
    let onPostIntent: () -> Void

    var body: some View {
        if row.isPosting {
            ProgressView()
                .tint(MageRideColor.primary)
                .frame(width: MageRideControl.rowIcon, height: MageRideControl.rowIcon)
        } else if row.isPosted {
            StatusPill(label: "job_board_intent_posted".localised, tone: .done)
        } else if row.isExpired {
            StatusPill(label: "job_board_expired".localised, tone: .neutral)
        } else {
            Button(action: onPostIntent) {
                Text(key: "job_board_post_intent")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.primary)
                    .frame(minHeight: MageRideControl.minimumTapTarget)
            }
            .buttonStyle(.plain)
            .disabled(!row.canPost)
        }
    }
}

/// The gate, the empty board and the shimmer are one centred notice in three tones of copy.
private struct BoardNotice: View {

    let text: String
    var isSpinning = false

    var body: some View {
        VStack(spacing: MageRideSpacing.sm) {
            if isSpinning {
                ProgressView().tint(MageRideColor.primary)
            }
            Text(text)
                .mageFont(.body)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .padding(MageRideSpacing.lg)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

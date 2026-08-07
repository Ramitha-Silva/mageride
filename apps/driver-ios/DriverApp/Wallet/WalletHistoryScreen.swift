import MageRideShared
import SwiftUI

/// **SCR-DI-025 · payment & fee history** (US-9A.6, US-9A.19, D-13).
///
/// The wireframe, top to bottom: the large title *"History"*, the four chips, and the ledger rows — a
/// tinted `−`/`＋` disc, the line's name and Colombo date, and the signed amount.
///
/// **Δ iOS — `.searchable` and a date filter, which is the cell's own clause.** The search is the
/// device's and runs over the page already read; see ``WalletHistoryState/matchesQuery(_:)`` for what
/// it matches and why it deliberately does not match the amount. The Android twin has no search at
/// all: a `SearchBar` there is a control a screen builds, and here it is one line on a `NavigationStack`
/// that brings the scroll-to-dismiss, the Cancel button and the VoiceOver behaviour with it.
///
/// **The toolbar carries two actions where the wireframe draws none.** D2' §SCR-DI-025's own text names
/// a *"date-range filter"* and a *"statement download (US-9A.19)"*, and the sketch's title row has no
/// room for either. Both are in the spec text; only the icon count differs.
@MainActor
struct WalletHistoryScreen: View {

    @StateObject private var model: WalletHistoryModel

    @State private var isPickingRange = false

    init(model: @autoclosure @escaping () -> WalletHistoryModel) {
        _model = StateObject(wrappedValue: model())
    }

    var body: some View {
        VStack(spacing: 0) {
            banners
            FilterChips(selected: model.state.filter, onSelect: model.select(filter:))
            ledger
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "wallet_history_title"))
        .navigationBarTitleDisplayMode(.large)
        .searchable(
            text: Binding(get: { model.state.query }, set: model.onQueryChange),
            prompt: Text(key: "wallet_history_search")
        )
        .toolbar {
            ToolbarItemGroup(placement: .navigationBarTrailing) {
                Button { isPickingRange = true } label: {
                    Image(systemName: model.state.hasRange ? "calendar.badge.clock" : "calendar")
                }
                .accessibilityLabel(Text(key: "wallet_history_range"))

                downloadMenu
            }
        }
        .task { await model.refresh() }
        .sheet(isPresented: $isPickingRange) {
            DateRangeSheet(from: model.state.from, to: model.state.to) { from, to in
                isPickingRange = false
                Task { await model.setRange(from: from, to: to) }
            }
        }
        .sheet(
            item: Binding(
                get: { model.state.exported },
                set: { if $0 == nil { model.dismissExported() } }
            )
        ) { statement in
            ActivityView(file: statement.url)
        }
    }

    // MARK: - The strip

    @ViewBuilder
    private var banners: some View {
        if model.state.isLoading || model.state.exporting != nil {
            ProgressView()
                .progressViewStyle(.linear)
                .frame(maxWidth: .infinity)
        }
        if let errorKey = model.state.errorKey {
            DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                .onTapGesture(perform: model.dismissError)
        }
    }

    // MARK: - The list

    /// A `List` rather than a `LazyVStack` in a `ScrollView`: `.searchable` puts its field in the
    /// navigation bar either way, but only a `List` scrolls it away on the first drag, which is the
    /// behaviour a driver expects of a search on this platform.
    @ViewBuilder
    private var ledger: some View {
        if model.state.isEmpty {
            VStack(spacing: MageRideSpacing.xs) {
                Spacer(minLength: 0)
                Text(key: "wallet_history_empty")
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(MageRideSpacing.lg)
        } else {
            List(model.state.visible, id: \.entryId) { line in
                LedgerRow(line: line)
                    .listRowInsets(EdgeInsets(
                        top: MageRideSpacing.xxs,
                        leading: MageRideSpacing.md,
                        bottom: MageRideSpacing.xxs,
                        trailing: MageRideSpacing.md
                    ))
                    .listRowBackground(MageRideColor.background)
            }
            .listStyle(.plain)
            .refreshable { await model.refresh() }
        }
    }

    /// US-9A.19's download, in the two formats `wallet.yaml` declares.
    private var downloadMenu: some View {
        Menu {
            ForEach(StatementFormat.allCases) { format in
                Button(format.fileExtension.uppercased()) {
                    Task { await model.export(format) }
                }
            }
        } label: {
            Image(systemName: "square.and.arrow.down")
        }
        .disabled(model.state.exporting != nil)
        .accessibilityLabel(Text(key: "wallet_statement_download"))
    }
}

/// The wireframe's `All · Fees · Top-ups · Transfers`. Local — switching one re-hits nothing.
private struct FilterChips: View {

    let selected: HistoryFilter
    let onSelect: (HistoryFilter) -> Void

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: MageRideSpacing.xs) {
                ForEach(HistoryFilter.allCases) { filter in
                    Button { onSelect(filter) } label: {
                        Text(key: filter.labelKey)
                            .mageFont(.label)
                            .foregroundStyle(
                                filter == selected ? MageRideColor.onPrimary : MageRideColor.onSurface
                            )
                            .padding(.horizontal, MageRideSpacing.sm)
                            .padding(.vertical, MageRideSpacing.xs)
                            .background(
                                filter == selected ? MageRideColor.primary : MageRideColor.surfaceVariant,
                                in: Capsule()
                            )
                            .contentShape(Capsule())
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(filter == selected ? [.isButton, .isSelected] : .isButton)
                }
            }
            .padding(.horizontal, MageRideSpacing.md)
            .padding(.vertical, MageRideSpacing.xxs)
        }
    }
}

/// One ledger line.
///
/// `amountMinor` is **signed on the wire** — one of the ledger columns D3' §0 exempts from the
/// non-negative rule — so the sign, the disc and the colour all read off the same number rather than
/// off the kind. That is what keeps an `adjustment` (which can go either way) honest.
private struct LedgerRow: View {

    let line: WalletTransaction

    var body: some View {
        HStack(spacing: MageRideSpacing.xs) {
            SignBadge(isDebit: line.isDebit, accent: accent)

            VStack(alignment: .leading, spacing: 1) {
                Text(key: LedgerKinds.labelKey(for: line.kind))
                    .mageFont(.body)
                    .foregroundStyle(MageRideColor.onSurface)
                Text(ScheduleLabels.date(line.occurredAt))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }

            Spacer(minLength: MageRideSpacing.xs)

            VStack(alignment: .trailing, spacing: 1) {
                Text(MoneyFormat.rupees(line.amountMinor))
                    .mageFont(.title)
                    .foregroundStyle(accent)
                Text(MoneyFormat.rupees(line.balanceAfterMinor))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
        }
        .padding(.vertical, MageRideSpacing.xxs)
        .accessibilityElement(children: .combine)
    }

    private var accent: Color { line.isDebit ? MageRideColor.error : MageRideColor.success }
}

/// The wireframe's tinted `−` / `＋` disc. The glyphs are symbols, so they are not translated.
private struct SignBadge: View {

    let isDebit: Bool
    let accent: Color

    var body: some View {
        Text(isDebit ? Self.debit : Self.credit)
            .mageFont(.caption)
            .foregroundStyle(accent)
            .frame(width: MageRideControl.listRowIcon, height: MageRideControl.listRowIcon)
            .background(accent.opacity(Self.tint), in: Circle())
            .accessibilityHidden(true)
    }

    /// How much of the accent a sign disc keeps. Light enough to read the glyph over.
    private static let tint: Double = 0.18

    /// Symbols, not copy — the same rule `Rs` and `→` follow.
    private static let debit = "−"
    private static let credit = "+"
}

/// D2' §SCR-DI-025's date-range filter.
///
/// **Δ iOS, and it removes a trap the Android twin has to work around.** M3's `DateRangePicker` answers
/// **UTC midnight** of the tapped day, so `WalletHistoryScreen.kt` converts through
/// `BusinessCalendar.businessDate` and documents why. Here the two `DatePicker`s are given
/// ``ScheduleLabels/calendar`` and ``ScheduleLabels/zone`` in the environment, so the day the driver
/// taps **is** a Colombo day and the conversion is the same one every other date in the app makes.
/// Two pickers rather than one range control because SwiftUI has no range picker at all before
/// `MultiDatePicker`, whose answer is a *set* of days.
private struct DateRangeSheet: View {

    let onConfirm: (BusinessDate?, BusinessDate?) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var start: Date
    @State private var end: Date

    /// Re-opening the sheet shows the range that is in force, which is why the two bounds come back
    /// through ``ScheduleLabels/zone`` rather than being reset to today: a driver adjusting one end of
    /// a range they set last week should not have to set the other end again.
    init(from: BusinessDate?, to: BusinessDate?, onConfirm: @escaping (BusinessDate?, BusinessDate?) -> Void) {
        self.onConfirm = onConfirm

        let today = Date()
        _start = State(initialValue: from.map(DateRangeSheet.startOfDay) ?? today)
        _end = State(initialValue: to.map(DateRangeSheet.startOfDay) ?? today)
    }

    /// Midnight at the start of a Colombo business date, as a `Date` (through `:shared`'s own helper —
    /// `BusinessCalendar.startOfDay` takes its zone as a defaulted parameter, which the export drops).
    private static func startOfDay(_ date: BusinessDate) -> Date {
        Date(timeIntervalSince1970: TimeInterval(IosBusinessDateKt.colomboStartOfDayMillis(date: date)) / 1000)
    }

    var body: some View {
        NavigationStack {
            Form {
                DatePicker(selection: $start, displayedComponents: .date) {
                    Text(key: "wallet_history_from")
                }
                DatePicker(selection: $end, in: start..., displayedComponents: .date) {
                    Text(key: "wallet_history_to")
                }

                // Clearing the range is the way back to "everything the server sends"; a driver who
                // opened the calendar by accident needs a door out that is not a range.
                Button { onConfirm(nil, nil) } label: {
                    Text(key: "wallet_history_range_clear")
                }
            }
            .environment(\.calendar, ScheduleLabels.calendar)
            .environment(\.timeZone, ScheduleLabels.zone)
            .navigationTitle(Text(key: "wallet_history_range"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button { dismiss() } label: { Text(key: "action_cancel") }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button { onConfirm(businessDate(start), businessDate(end)) } label: {
                        Text(key: "action_save")
                    }
                }
            }
        }
        .presentationDetents([.medium])
    }

    /// The Colombo business date a picked instant falls on (D-13, D-38).
    ///
    /// Through `:shared` rather than a `DateFormatter` here, for the reason `IosBusinessDate.kt` gives:
    /// the zone is written once, in Kotlin, and a driver whose handset came back from abroad still on
    /// its old zone must filter on the days the ledger was written in.
    private func businessDate(_ date: Date) -> BusinessDate {
        IosBusinessDateKt.colomboBusinessDateOf(
            at: IosInstantKt.timestampFromEpochMillis(millis: Int64(date.timeIntervalSince1970 * 1000))
        )
    }
}

import MageRideShared
import SwiftUI

/// **SCR-DI-021 · wallet & fee status** (US-9.7, US-9.1, US-9.9).
///
/// The wireframe, top to bottom: the large title *"Wallet"* — this is tab 3, not a pushed screen, so
/// there is no back button — the read-only balance in display type, the daily-fee card with its three
/// `kv` rows, and the two `btn-row`s that open the other four screens in this group.
///
/// **One thing here is not in the wireframe: the *"Warn me below Rs 200"* row.** D2' §SCR-DI-021's own
/// states line calls the threshold *"driver-set"* and the C091 deliverable asks for the setting, and
/// there is nowhere else in this group it could live. ``WalletPreferences`` explains why it is stored
/// on the handset, and the sheet says so to the driver rather than implying a server-side change.
/// Recorded in the C091 handoff.
///
/// `@MainActor` on the whole view, not on its initialiser — see ``ProfileSetupScreen`` for why.
@MainActor
struct WalletScreen: View {

    @StateObject private var model: WalletModel

    private let onOpen: (DriverRoute) -> Void

    @State private var isEditingThreshold = false

    init(model: @autoclosure @escaping () -> WalletModel, onOpen: @escaping (DriverRoute) -> Void) {
        _model = StateObject(wrappedValue: model())
        self.onOpen = onOpen
    }

    var body: some View {
        ScrollView {
            VStack(spacing: MageRideSpacing.sm) {
                notice

                if let errorKey = model.state.errorKey {
                    DashboardBanner(text: errorKey.localised, accent: MageRideColor.error)
                        .onTapGesture(perform: model.dismissError)
                }

                balance
                dailyFeeCard
                thresholdRow
                actions
            }
            .padding(MageRideSpacing.md)
        }
        .frame(maxWidth: .infinity)
        .background(MageRideColor.background)
        .navigationTitle(Text(key: "wallet_title"))
        .navigationBarTitleDisplayMode(.large)
        .refreshable { await model.refresh() }
        .task { await model.refresh() }
        .sheet(isPresented: $isEditingThreshold) {
            ThresholdSheet(
                currentMinor: model.state.thresholdMinor,
                onSave: { minor in
                    model.setThreshold(minor: minor)
                    isEditingThreshold = false
                },
                onReset: {
                    model.clearThreshold()
                    isEditingThreshold = false
                }
            )
        }
    }

    // MARK: - The balance

    /// The wireframe's centred *"Balance (read-only)"* and its display figure.
    ///
    /// `balance`, not `available`: US-9.7 calls this the balance and D2' marks it read-only. The
    /// spendable figure is what every *decision* in this group is checked against — see
    /// ``WalletState/isBelowDayFee`` and ``CreditTransferModel/rejectionForSend()`` — and it is shown as
    /// its own line only when the two differ, because a driver with no accrued debt should not be
    /// reading two numbers for one wallet.
    private var balance: some View {
        VStack(spacing: 1) {
            Text(key: "wallet_balance_label")
                .mageFont(.caption)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
            Text(model.state.balanceMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty)
                .mageFont(.display)
                .foregroundStyle(MageRideColor.onSurface)

            if let debt = model.state.outstandingDebtMinor {
                Text("wallet_available_after_debt".localisedFormat(MoneyFormat.rupees(debt)))
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.warning)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
    }

    // MARK: - The daily fee

    /// The wireframe's `Daily fee` card — vehicle, rate, and today's status (US-9.1/9.7).
    private var dailyFeeCard: some View {
        VStack(alignment: .leading, spacing: MageRideSpacing.xxs) {
            SectionLabel(key: "wallet_daily_fee")

            if let fee = model.state.standing.dailyFee {
                FeeRow(labelKey: "wallet_fee_vehicle", value: fee.vehicleType.labelKey.localised)
                FeeRow(
                    labelKey: "wallet_fee_rate",
                    value: "wallet_fee_rate_value".localisedFormat(
                        model.state.standing.dailyRateMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty
                    )
                )
                FeeRow(labelKey: "wallet_fee_today", value: feeStatus, accent: feeAccent)
            } else {
                Text(key: "wallet_fee_unavailable")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
        }
        .padding(MageRideSpacing.sm)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            MageRideColor.surfaceVariant,
            in: RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous)
        )
    }

    /// *"PAID ✓ (first trip free)"* is two facts in one line, and the qualifier only belongs on a day
    /// the free trip has not been spent — see ``WalletFeeStanding/isFirstTripStillFree``.
    private var feeStatus: String {
        let status = (model.state.standing.isFeePaid ? "wallet_fee_paid" : "wallet_fee_unpaid").localised
        guard model.state.standing.isFirstTripStillFree else { return status }
        return "wallet_fee_status_first_free".localisedFormat(status)
    }

    private var feeAccent: Color {
        model.state.standing.isFeePaid ? MageRideColor.success : MageRideColor.warning
    }

    // MARK: - The banners

    /// The three banner states, ranked hardest first.
    ///
    /// Overdrawn is D5' §9.4's *"Top Up Required"*, *"below one day's fee"* is D2' §SCR-DI-021's, and
    /// the low-balance nudge is the softest of the three — see ``WalletState/isBelowDayFee`` on why the
    /// first two are different questions rather than one restated.
    @ViewBuilder
    private var notice: some View {
        if let owed = model.state.overdrawnByMinor {
            DashboardBanner(
                text: "wallet_top_up_required".localisedFormat(MoneyFormat.rupees(owed)),
                accent: MageRideColor.error,
                symbolName: "exclamationmark.triangle.fill"
            )
        } else if model.state.isBelowDayFee {
            DashboardBanner(
                text: "wallet_below_day_fee".localisedFormat(
                    model.state.standing.dailyRateMinor.map { MoneyFormat.rupees($0) } ?? MoneyFormat.empty
                ),
                accent: MageRideColor.error,
                symbolName: "exclamationmark.triangle.fill"
            )
        } else if model.state.isLowBalance {
            DashboardBanner(
                text: "wallet_low_balance".localisedFormat(MoneyFormat.rupees(model.state.thresholdMinor)),
                accent: MageRideColor.warning,
                symbolName: "exclamationmark.circle"
            )
        }
    }

    // MARK: - The actions

    /// The wireframe's two `btn-row`s — one CTA and three outlined actions.
    private var actions: some View {
        VStack(spacing: MageRideSpacing.xs) {
            Button { onOpen(.walletTopUp) } label: {
                Text(key: "wallet_action_top_up")
            }
            .buttonStyle(.mageCta)

            HStack(spacing: MageRideSpacing.xs) {
                OutlinedAction(labelKey: "wallet_action_request") { onOpen(.walletRequestCredit) }
                OutlinedAction(labelKey: "wallet_action_transfer") { onOpen(.walletTransfer) }
            }
            OutlinedAction(labelKey: "wallet_action_history") { onOpen(.walletHistory) }
        }
    }

    /// *"Warn me below Rs 200"* — the driver's own low-balance line (US-9.9).
    private var thresholdRow: some View {
        Button { isEditingThreshold = true } label: {
            HStack(spacing: MageRideSpacing.xxs) {
                Image(systemName: "bell.badge")
                    .font(.caption)
                Text("wallet_threshold_row".localisedFormat(MoneyFormat.rupees(model.state.thresholdMinor)))
                    .mageFont(.label)
                Spacer(minLength: 0)
            }
            .foregroundStyle(MageRideColor.primary)
            .frame(minHeight: MageRideControl.minimumTapTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

/// The wireframe's `kv` — a muted label on the left, the value on the right.
private struct FeeRow: View {

    let labelKey: String
    let value: String
    var accent: Color?

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: MageRideSpacing.xs) {
            Text(key: labelKey)
                .mageFont(.bodySmall)
                .foregroundStyle(MageRideColor.onSurfaceVariant)
                .frame(maxWidth: .infinity, alignment: .leading)
            Text(value)
                .mageFont(.bodySmall)
                .foregroundStyle(accent ?? MageRideColor.onSurface)
        }
        .accessibilityElement(children: .combine)
    }
}

/// The wireframe's `.btn-out` — an outlined action beside or under the CTA.
///
/// Not `.bordered`: §0.2 fixes the CTA's height and radius and this is the same bar without the fill,
/// which is what keeps the four buttons on this screen one control at four widths.
struct OutlinedAction: View {

    let labelKey: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(key: labelKey)
                .mageFont(.subtitle)
                .foregroundStyle(MageRideColor.primary)
                .frame(maxWidth: .infinity, minHeight: MageRideControl.ctaHeight)
                .overlay {
                    RoundedRectangle(cornerRadius: MageRideControl.ctaRadius, style: .continuous)
                        .strokeBorder(MageRideColor.outline, lineWidth: 1)
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

/// The threshold sheet.
///
/// The body says the setting lives on this handset, because it does and because a driver who believed
/// they had changed when MageRide texts them would be misled — the `LOW_BALANCE` push runs on the
/// admin's figure and there is no route that would let this one reach it.
///
/// A `.sheet` with `.presentationDetents([.medium])` rather than an `.alert`: an `.alert` on this
/// platform hosts at most one text field and gives it neither a label nor a `Rs` prefix, and this one
/// needs the explanation above it to be readable.
private struct ThresholdSheet: View {

    let currentMinor: Int64
    let onSave: (Int64) -> Void
    let onReset: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var typed: String

    init(currentMinor: Int64, onSave: @escaping (Int64) -> Void, onReset: @escaping () -> Void) {
        self.currentMinor = currentMinor
        self.onSave = onSave
        self.onReset = onReset
        _typed = State(initialValue: WalletInput.rupeesOf(currentMinor))
    }

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                Text(key: "wallet_threshold_body")
                    .mageFont(.bodySmall)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                RupeeField(labelKey: "wallet_amount_label", value: $typed)

                Button(action: onReset) {
                    Text(key: "wallet_threshold_reset")
                        .mageFont(.label)
                        .foregroundStyle(MageRideColor.primary)
                }
                .frame(minHeight: MageRideControl.minimumTapTarget)

                Spacer(minLength: 0)

                Button { WalletInput.amountMinor(typed).map(onSave) } label: {
                    Text(key: "action_save")
                }
                .buttonStyle(.mageCta)
                .disabled(WalletInput.amountMinor(typed) == nil)
            }
            .padding(MageRideSpacing.md)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(MageRideColor.background)
            .navigationTitle(Text(key: "wallet_threshold_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button { dismiss() } label: { Text(key: "action_cancel") }
                }
            }
        }
        .presentationDetents([.medium])
    }
}

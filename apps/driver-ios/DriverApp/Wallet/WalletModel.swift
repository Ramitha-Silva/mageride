import Foundation
import MageRideShared

/// SCR-DI-021's state.
///
/// - Parameters:
///   - isLoading: The three reads are in flight.
///   - standing: The balance, today's fee and the rate table.
///   - thresholdMinor: The **driver's** low-balance line — see ``WalletPreferences``.
///   - errorKey: Resolved copy for the last failure.
struct WalletState {

    var isLoading = true
    var standing = WalletFeeStanding()
    var thresholdMinor: Int64 = WalletRules.shared.DEFAULT_LOW_BALANCE_THRESHOLD.amountMinor
    var errorKey: String?

    /// The read-only figure the screen leads with (US-9.7). `nil` until wallet-svc answers.
    var balanceMinor: Int64? { standing.standing?.balance.amountMinor }

    /// What may actually be spent — the balance net of accrued penalty debt (D-05).
    var availableMinor: Int64? { standing.availableMinor }

    /// Accrued but unsettled cancellation penalties, when there are any (D-05, AL-16).
    ///
    /// `nil` at zero rather than `Rs 0`: the line only belongs on the screen of a driver who has one,
    /// and a driver with no debt should not be reading two numbers for one wallet.
    var outstandingDebtMinor: Int64? {
        standing.standing?.outstandingDebt.amountMinor.flatMap { $0 > 0 ? $0 : nil }
    }

    /// US-9.9 / D5' §9.4, evaluated against the driver's own threshold rather than a baked one.
    var alert: WalletAlert {
        guard let standing = standing.standing else { return WalletAlertNone.shared }
        return WalletRules.shared.alertFor(
            standing: standing,
            threshold: Money.companion.ofMinor(amountMinor: thresholdMinor)
        )
    }

    /// How far below zero the wallet is, when D5' §9.4's banner is the one in force.
    var overdrawnByMinor: Int64? { (alert as? WalletAlertTopUpRequired)?.owed.amountMinor }

    /// Whether US-9.9's soft nudge is the one in force.
    var isLowBalance: Bool { alert is WalletAlertLowBalance }

    /// D2' §SCR-DI-021's *"below one day's fee → Top Up Required"*.
    ///
    /// **A different question from ``alert``, and both are in the spec.** D5' §9.4 draws the
    /// *"Top Up Required"* banner at a **negative** balance and `:shared`'s `WalletRules` implements
    /// that; D2' draws it at **below one day's fee**, which is US-9.1's real consequence — a driver who
    /// cannot cover the rate has their next request refused with `402 insufficient-wallet` fifteen
    /// seconds after an offer arrives. Neither is wrong and neither subsumes the other, so the screen
    /// ranks them: overdrawn is the harder state and wins, this is the next, and the low-balance nudge
    /// is the softest.
    ///
    /// `false` once the day's fee is paid: trips 2..N are free after the deduction (US-9.4), so there
    /// is nothing left today for the balance to be short of.
    var isBelowDayFee: Bool {
        guard !standing.isFeePaid else { return false }
        guard let rate = standing.dailyRateMinor, rate > 0, let available = availableMinor else { return false }
        return available < rate
    }
}

/// **SCR-DI-021 · wallet & daily fee** (US-9.7, US-9.1, US-9.9).
///
/// Three reads in one pass — the balance, today's fee row and the seven-tier rate table — each
/// best-effort, because a driver whose fee read failed still needs to see their money.
///
/// **Nothing here computes a balance.** The ledger is the truth and the server holds it (D-09); this
/// class holds the server's figure and asks `:shared`'s rules what to say about it. The one number
/// that is genuinely the device's is the low-balance threshold, and ``WalletPreferences`` explains at
/// length why.
///
/// The screen is re-read on ``refresh()`` rather than subscribed to: D2' marks the balance as
/// event-updated, `LiveHub` has no wallet event in its contract, and there is no hub client in
/// `:shared` yet (the same gap SCR-DI-015 polls around). A read on open and after every action that
/// moves money is what keeps this screen honest until one lands.
@MainActor
final class WalletModel: ObservableObject {

    @Published private(set) var state = WalletState()

    private let identity: DriverIdentity
    private let wallet: WalletRepository
    private let preferences: WalletPreferences

    init(identity: DriverIdentity, wallet: WalletRepository, preferences: WalletPreferences) {
        self.identity = identity
        self.wallet = wallet
        self.preferences = preferences
        state.thresholdMinor = preferences.lowBalanceThreshold.amountMinor
    }

    /// Re-reads the balance, the fee and the rate table.
    ///
    /// Every read behind ``WalletRepository/standing(driverId:)`` is best-effort, so this cannot fail —
    /// what a dead read produces is a `nil` field and the *"we could not read today's fee"* line, not
    /// an error banner over a balance that was read perfectly well.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        state.isLoading = true
        state.errorKey = nil

        state.standing = await wallet.standing(driverId: driverId)
        state.isLoading = false
    }

    /// Moves the driver's low-balance line (US-9.9's *"driver-set threshold"*).
    ///
    /// Persisted immediately and applied to the state in the same breath — there is no round trip to
    /// wait for, because there is no route to make one to.
    func setThreshold(minor: Int64) {
        preferences.lowBalanceThresholdMinor = minor
        state.thresholdMinor = preferences.lowBalanceThreshold.amountMinor
    }

    /// Puts the threshold back to D5' §9.4's Rs 200.
    func clearThreshold() {
        preferences.lowBalanceThresholdMinor = nil
        state.thresholdMinor = preferences.lowBalanceThreshold.amountMinor
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }
}

import Foundation
import MageRideShared

/// SCR-PI-016's state.
struct PaymentMethodState {

    /// Which rails this ride can use — ``PaymentRails/ride``, plus COD for a parcel.
    var rails: [PaymentMethod] = PaymentRails.ride

    /// The ride, for the map backdrop and the parcel test. `nil` until the read lands.
    var ride: RideDetail?

    /// The fare. **The only number on this screen** — there is no surcharge line, because AL-57
    /// retired the rail that had one.
    var amountMinor: Int64?

    /// The passenger's spendable balance. `nil` while it is being read, and after a wallet-svc
    /// failure — which costs the row its balance line, not the screen.
    var walletBalanceMinor: Int64?

    /// What Confirm will send.
    var chosen: PaymentMethod = PaymentMethod.cash

    /// The stored preference, which is what wears the cell's `DEFAULT` badge (US-22.4, AL-14).
    var preferred: PaymentMethod = PaymentMethod.cash

    /// Set by Confirm, consumed by the screen's navigation. A flag rather than the rail itself,
    /// because `onChange(of:)` needs `Equatable` and ``chosen`` already carries the answer.
    var isConfirmed = false

    var errorKey: String?

    /// Whether the wallet can cover this fare.
    ///
    /// `false` turns the row's action into *"Top up"* rather than disabling it — a passenger who is
    /// Rs 40 short should be one tap from fixing that, not told no. `fare.yaml` answers
    /// `402 insufficient-wallet` if they try anyway, **with cash and driver-QR still offered and never
    /// a silent fallback to cash**.
    var walletCovers: Bool {
        guard let balance = walletBalanceMinor, let amount = amountMinor else { return false }
        return balance >= amount
    }

    /// Whether the wallet row should offer a top-up instead of a selection.
    var walletIsShort: Bool { walletBalanceMinor != nil && !walletCovers }
}

/// SCR-PI-016 — how this ride gets paid.
///
/// **Three rails, and the two that are missing are the story.** AL-57 removed `onepay` (one merchant
/// account per merchant, so a card fare could only land in MageRide's own account — card acceptance
/// moved to the wallet **top-up**, where MageRide legitimately is the payee) and AL-59 removed the
/// platform-merchant `lankaqr` (it collected into the platform account while crediting the driver a
/// read-model row). What survives is cash, the passenger wallet, and the **driver's own** QR.
///
/// **There is therefore no surcharge anywhere on this screen.** The +5 % existed to recover OnePay's
/// ~3 % on the ride and died with it; ``PaymentMethodState`` has no surcharge field to render, which
/// is the Definition-of-Done line *"no surcharge is ever displayed on a ride"* made structural. The
/// wireframe still draws Cash / LankaQR / OnePay +5 %, which predates the 2026-08-01 payment-custody
/// change set; the prompt's own fence and Definition of Done name AL-57/AL-59 explicitly, so the
/// change set wins. Recorded in the C080 handoff as a wireframe that needs a micro-change-set, and
/// restated in C098's.
@MainActor
final class PaymentMethodModel: ObservableObject {

    @Published private(set) var state = PaymentMethodState()

    private let rideId: String
    private let rides: RideRepository
    private let sessions: PassengerSessions
    private let preferences: AppPreferences
    private let selection: PaymentSelection

    private var work: [Task<Void, Never>] = []

    init(
        rideId: String,
        rides: RideRepository,
        sessions: PassengerSessions,
        preferences: AppPreferences,
        selection: PaymentSelection
    ) {
        self.rideId = rideId
        self.rides = rides
        self.sessions = sessions
        self.preferences = preferences
        self.selection = selection

        // US-22.4's *"pre-selected at booking/checkout (and still changeable per trip)"* — this is
        // the checkout half, and C101's SCR-PI-027 row is what sets it. A stored value this build
        // cannot offer reads as Cash rather than as nothing (see ``PaymentRails/fromWire(_:)``).
        let stored = preferences.defaultPaymentMethod.flatMap(PaymentRails.fromWire) ?? PaymentMethod.cash
        state.chosen = stored
        state.preferred = stored
    }

    deinit {
        work.forEach { $0.cancel() }
    }

    /// Reads the ride and the balance. Idempotent.
    func start() {
        guard work.isEmpty else { return }
        work.append(Task { await self.load() })
    }

    func choose(_ method: PaymentMethod) {
        state.chosen = method
        state.errorKey = nil
    }

    /// Confirm.
    ///
    /// This screen does **not** call `POST /v1/fare/pay` — SCR-PI-017 does, because the wallet rail
    /// settles on the spot and the driver-QR rail needs what the initiation returns. What Confirm
    /// does is record the rail and decide which screen comes next; ``PaymentSelection`` is how the
    /// rail gets there, and its own note says why it cannot be a route argument.
    func confirm() {
        selection.choose(rideId: rideId, method: state.chosen)
        state.isConfirmed = true
    }

    func onConfirmConsumed() {
        state.isConfirmed = false
    }

    func clearError() {
        state.errorKey = nil
    }

    // MARK: -

    private func load() async {
        do {
            let ride = try await rides.ride(rideId: rideId)
            guard !Task.isCancelled else { return }
            state.ride = ride
            state.rails = ride.kind == RideKind.package ? PaymentRails.parcel : PaymentRails.ride
            state.amountMinor = ride.amountMinorOrNil
        } catch is CancellationError {
            return
        } catch {
            state.errorKey = RideErrors.messageKey(for: error)
        }

        // The balance is read separately and is allowed to fail on its own: a wallet-svc outage
        // should cost the passenger the wallet row's balance line, not the whole payment screen.
        guard let userId = sessions.userId else { return }
        guard let wallet = try? await rides.wallet(userId: userId), !Task.isCancelled else { return }
        state.walletBalanceMinor = wallet.availableMinor
    }
}

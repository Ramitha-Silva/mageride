import Foundation
import MageRideShared

/// SCR-DI-023's state.
///
/// - Parameters:
///   - holderId: The Driver ID typed into the field — the driver being asked.
///   - amount: The rupee digits.
///   - outgoing: Requests this driver has raised that are still awaiting a decision.
///   - isSubmitting: The POST is in flight.
///   - justRequested: The row the last successful request produced — the wireframe's *"Awaiting
///     driver approval"*.
///   - errorKey: Resolved copy for the last failure.
struct RequestCreditState {

    var holderId = ""
    var amount = ""
    var outgoing: [TransferRow] = []
    var isSubmitting = false
    var justRequested: TransferRow?
    var errorKey: String?

    /// Whether what has been typed is a well-formed platform id. Blank is "not yet", not "wrong".
    var isHolderIdValid: Bool { WalletInput.isDriverId(holderId) }

    /// Whether the field should be drawn in `error` — typed something, and it is not an id.
    var isHolderIdRejected: Bool { !holderId.trimmingCharacters(in: .whitespaces).isEmpty && !isHolderIdValid }

    /// Whether **Request transfer** is live.
    var canRequest: Bool { !isSubmitting && isHolderIdValid && (WalletInput.amountMinor(amount) ?? 0) > 0 }
}

/// **SCR-DI-023 · request credit** (US-9.10, AL-01, AL-34).
///
/// The **pull** half of a driver-to-driver transfer: a driver who is short names a holder by Driver ID
/// and an amount, and the holder approves it on SCR-DI-024. Nothing moves here — the POST creates a
/// `PENDING` row and the money is the holder's until they say otherwise.
///
/// **Driver ID only.** AL-34 removed the QR-scan path this screen used to draw; there is no scanner
/// seam in this class and nothing takes a scanned payload. What the field validates against is the
/// contract's own `Ulid` pattern — see ``PlatformId`` for why the wireframe's `DRV-22011` is not a
/// thing that exists.
///
/// **No balance check, deliberately.** A request costs the requester nothing and the holder's balance
/// is not this driver's to see; `402 insufficient-wallet` is raised at *approval* time, on the
/// holder's screen, against the holder's wallet.
@MainActor
final class RequestCreditModel: ObservableObject {

    @Published private(set) var state = RequestCreditState()

    private let identity: DriverIdentity
    private let transfers: CreditTransferRepository

    init(identity: DriverIdentity, transfers: CreditTransferRepository) {
        self.identity = identity
        self.transfers = transfers
    }

    /// The Driver ID field.
    func onHolderIdChange(_ raw: String) {
        state.holderId = raw
        state.errorKey = nil
    }

    /// The amount field.
    func onAmountChange(_ raw: String) {
        state.amount = WalletInput.rupeeDigits(raw)
        state.errorKey = nil
    }

    /// Re-reads the outstanding requests this driver has raised.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        do {
            // A request this driver raised is one they will RECEIVE credit from, which is the side
            // `TransferRow.direction` describes. `PENDING` is the only status worth listing here: an
            // approved one is money already in the wallet and belongs to the history screen.
            let rows = try await transfers.history(driverId: driverId, direction: TransferDirectionFilter.received)
            state.outgoing = rows.filter { $0.status == TransferStatus.pending }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
    }

    /// **Request transfer** — creates the `PENDING` row and pushes it onto the outgoing list.
    func request() async {
        guard state.canRequest, let amountMinor = WalletInput.amountMinor(state.amount) else { return }
        let holderId = WalletInput.driverId(state.holderId)

        state.isSubmitting = true
        state.errorKey = nil
        do {
            let row = try await transfers.request(holderDriverId: holderId, amountMinor: amountMinor)
            state.justRequested = row
            // The form is cleared because the request now lives in the list below it; leaving it
            // filled invites a second identical ask on the same holder.
            state.holderId = ""
            state.amount = ""
            state.outgoing = [row] + state.outgoing.filter { $0.transferId != row.transferId }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isSubmitting = false
    }

    /// Dismisses the *"Awaiting driver approval"* acknowledgement.
    func dismissAcknowledgement() {
        state.justRequested = nil
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }
}

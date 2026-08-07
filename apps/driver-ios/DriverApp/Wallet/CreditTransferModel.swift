import Foundation
import MageRideShared

/// SCR-DI-024's state.
///
/// - Parameters:
///   - incoming: Requests waiting on this driver's decision (US-9.11/9.12).
///   - recipientId: The Driver ID typed into the send field.
///   - amount: The rupee digits.
///   - standing: This driver's wallet, for the affordability check.
///   - history: Credit sent and received, newest first (US-9A.11).
///   - isLoading: The three reads are in flight.
///   - busyTransferId: The request currently being approved or rejected.
///   - isSubmitting: The direct send is in flight.
///   - sent: The row the last successful send produced.
///   - errorKey: Resolved copy for the last failure.
struct CreditTransferState {

    var incoming: [TransferRow] = []
    var recipientId = ""
    var amount = ""
    var standing: WalletStanding?
    var history: [TransferRow] = []
    var isLoading = true
    var busyTransferId: String?
    var isSubmitting = false
    var sent: TransferRow?
    var errorKey: String?

    /// Whether what has been typed is a well-formed platform id.
    var isRecipientIdValid: Bool { WalletInput.isDriverId(recipientId) }

    /// Whether the field should be drawn in `error`.
    var isRecipientIdRejected: Bool {
        !recipientId.trimmingCharacters(in: .whitespaces).isEmpty && !isRecipientIdValid
    }

    /// What is about to move.
    var amountMinor: Int64? { WalletInput.amountMinor(amount) }

    /// What the sender is debited, and what the recipient is credited: **the same figure** (AL-01).
    ///
    /// Two properties rather than one so the wireframe's *"You send / Recipient gets"* card reads off
    /// the rules that make it true rather than off the same field twice — and so that a change which
    /// introduced a commission would have to say so here, in a screen, where it is visible.
    var debitedMinor: Int64? {
        amountMinor.map {
            CreditTransferRules.shared.debitedFromSender(amount: Money.companion.ofMinor(amountMinor: $0)).amountMinor
        }
    }

    /// - SeeAlso: ``debitedMinor``
    var creditedMinor: Int64? {
        amountMinor.map {
            CreditTransferRules.shared.creditedToRecipient(amount: Money.companion.ofMinor(amountMinor: $0)).amountMinor
        }
    }
}

/// **SCR-DI-024 · credit transfer + requests** (US-9.11, US-9.12, US-9.20, US-9.21, AL-01).
///
/// Two halves of one screen: the holder's approval inbox, and the direct send.
///
/// ### The exact value, and never a commission line
///
/// `CreditTransferRules.feeFor` is zero for every amount and `entryFor` produces two postings that sum
/// to zero — there is no journal kind a fee could post under, so a commission is not a setting this
/// platform has. The *"You send Rs 1,000 / Recipient gets Rs 1,000"* card is that rule rendered, and it
/// reads both figures off `:shared` rather than printing the input twice.
///
/// ### Affordability is checked against the **spendable** balance
///
/// `WalletStanding.available` is the balance net of accrued cancellation debt (D-05), and it is what
/// the daily-fee gate checks too. A driver holding Rs 300 who owes Rs 200 can send Rs 100; offering
/// them Rs 250 would be describing money they do not have. The server checks again at approval time,
/// which is why the balance is re-read after every decision.
///
/// ### The inbox is read, not pushed
///
/// D2' says the incoming requests *"arrive via APNs"* and **no such notification type exists** —
/// `NotificationCatalogue` declares twenty-six and none is a credit transfer, so nothing raises one.
/// The list is therefore read on open and after every action. See ``CreditTransferRepository/pending()``.
@MainActor
final class CreditTransferModel: ObservableObject {

    @Published private(set) var state = CreditTransferState()

    private let identity: DriverIdentity
    private let transfers: CreditTransferRepository
    private let wallet: WalletRepository

    init(identity: DriverIdentity, transfers: CreditTransferRepository, wallet: WalletRepository) {
        self.identity = identity
        self.transfers = transfers
        self.wallet = wallet
    }

    /// The recipient's Driver ID field.
    func onRecipientIdChange(_ raw: String) {
        state.recipientId = raw
        state.errorKey = nil
    }

    /// The amount field.
    func onAmountChange(_ raw: String) {
        state.amount = WalletInput.rupeeDigits(raw)
        state.errorKey = nil
    }

    /// Why the send would be refused, or `nil` when it would go through.
    ///
    /// **`CreditTransferIntent`'s own `init` carries two `require`s, and on this platform one of those
    /// is a crash rather than an exception** — a Kotlin `require` inside a non-suspend, non-`@Throws`
    /// constructor reaches Swift as an uncaught Objective-C exception, which Swift cannot catch. The
    /// Android twin answers the self-transfer and the non-positive amount before building an intent as
    /// a matter of taste; here it is the difference between copy and a terminated app. Do not fold
    /// these two guards into the rules call.
    func rejectionForSend() -> CreditTransferRejection? {
        guard let senderId = identity.driverId else { return nil }
        guard let amountMinor = state.amountMinor, amountMinor > 0 else {
            return CreditTransferRejection.nonPositiveAmount
        }
        let recipientId = WalletInput.driverId(state.recipientId)
        if senderId == recipientId { return CreditTransferRejection.selfTransfer }
        guard let standing = state.standing else { return nil }

        return CreditTransferRules.shared.rejectionFor(
            intent: CreditTransferIntent(
                senderDriverId: senderId,
                recipientDriverId: recipientId,
                amount: Money.companion.ofMinor(amountMinor: amountMinor)
            ),
            standing: standing
        )
    }

    /// Whether **Send credit** is live.
    var canSend: Bool { !state.isSubmitting && state.isRecipientIdValid && rejectionForSend() == nil }

    /// Re-reads the inbox, the balance and the transfer history.
    func refresh() async {
        guard let driverId = identity.driverId else { return }
        state.isLoading = true
        state.errorKey = nil

        do {
            state.incoming = try await transfers.pending()
            state.standing = try await wallet.balance(driverId: driverId)
            state.history = try await transfers.history(driverId: driverId, direction: nil)
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.isLoading = false
    }

    /// **Approve** — debits this driver, credits the requester, no fee leg (US-9.13).
    ///
    /// The row is removed from the inbox on the server's answer, not on the tap: a `409` means somebody
    /// already answered it and a `402` means the balance moved underneath, and in both cases the
    /// request is still there to be looked at.
    func approve(transferId: String) async {
        await decide(transferId: transferId) { try await self.transfers.approve(transferId: transferId) }
    }

    /// **Decline** — nothing is posted (US-9.12).
    func reject(transferId: String) async {
        await decide(transferId: transferId) { try await self.transfers.reject(transferId: transferId) }
    }

    /// **Send credit** — the push half, by Driver ID, exact value (US-9.20/9.21).
    func send() async {
        guard canSend, let amountMinor = state.amountMinor else { return }
        let recipientId = WalletInput.driverId(state.recipientId)

        state.isSubmitting = true
        state.errorKey = nil
        do {
            let row = try await transfers.send(recipientDriverId: recipientId, amountMinor: amountMinor)
            state.sent = row
            state.recipientId = ""
            state.amount = ""
            state.isSubmitting = false
            await refresh()
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
            state.isSubmitting = false
        }
    }

    /// Dismisses the *"sent"* acknowledgement.
    func dismissSent() {
        state.sent = nil
    }

    /// Clears the last failure once its copy has been read.
    func dismissError() {
        state.errorKey = nil
    }

    private func decide(transferId: String, action: () async throws -> TransferRow) async {
        guard state.busyTransferId == nil else { return }
        state.busyTransferId = transferId
        state.errorKey = nil

        do {
            let row = try await action()
            state.incoming = state.incoming.filter { $0.transferId != row.transferId }
            // An approved request is money that has moved; a rejected one is not, and putting a
            // REJECTED row into the history list would be wrong on both counts.
            if row.status == TransferStatus.approved {
                state.history = [row] + state.history.filter { $0.transferId != row.transferId }
            }
        } catch {
            state.errorKey = OnboardingErrors.messageKey(for: error)
        }
        state.busyTransferId = nil

        // The balance moved (or was refused against a figure that has since changed), and the next
        // decision is checked against it.
        await refreshBalance()
    }

    private func refreshBalance() async {
        guard let driverId = identity.driverId else { return }
        if let standing = try? await wallet.balance(driverId: driverId) {
            state.standing = standing
        }
    }
}

import Foundation
import MageRideShared

/// SCR-DI-023 and SCR-DI-024 — driver-to-driver credit, both directions.
///
/// **The two screens are one service and one pair of verbs.** ``request(holderDriverId:amountMinor:)``
/// is the pull half — a driver who is short asks a holder, and the holder approves — and
/// ``send(recipientDriverId:amountMinor:)`` is the push half, the holder sending without being asked.
/// Both write `billing.credit_transfers` and both post the same balanced `driver_transfer` entry, so
/// splitting them by screen would put one operation in two files.
///
/// **A transfer moves the exact value and there is no fee leg** (AL-01). Not a configuration, not a
/// rate of zero: there is no journal kind that could carry a commission, `CreditTransferRules.entryFor`
/// produces two postings that sum to zero, and `LedgerEntry`'s own `init` refuses anything else. A
/// screen in this group that rendered a commission line would be describing money nothing can move.
///
/// **By Driver ID only** (AL-34). The QR-scan path SCR-DI-023 used to draw was removed; nothing here
/// takes a scanned payload and ``WalletInput`` is the only door onto an id.
protocol CreditTransferRepository: AnyObject {

    /// `POST /v1/wallet/credit-transfer/request` — ask a holder for credit (US-9.10).
    func request(holderDriverId: String, amountMinor: Int64) async throws -> TransferRow

    /// `POST /v1/wallet/credit-transfer/initiate` — send credit outright (US-9.20/9.21).
    func send(recipientDriverId: String, amountMinor: Int64) async throws -> TransferRow

    /// `GET /v1/wallet/credit-transfer/pending` — SCR-DI-024's approval inbox.
    func pending() async throws -> [TransferRow]

    /// `GET /v1/wallet/{driverId}/transfers` — sent and received, newest first (US-9A.11).
    func history(driverId: String, direction: TransferDirectionFilter?) async throws -> [TransferRow]

    /// `POST /v1/wallet/credit-transfer/{transferId}/approve` — move the money (US-9.13).
    func approve(transferId: String) async throws -> TransferRow

    /// `POST /v1/wallet/credit-transfer/{transferId}/reject` — decline. Nothing is posted (US-9.12).
    func reject(transferId: String) async throws -> TransferRow
}

/// ``CreditTransferRepository`` over `:shared`'s typed wallet client.
final class ApiCreditTransferRepository: CreditTransferRepository {

    private let wallet: WalletApi

    init(wallet: WalletApi) {
        self.wallet = wallet
    }

    /// Creates a `PENDING` row the holder then approves or rejects; **nothing moves yet**. The request
    /// is attested (D-30) and idempotent on the transport's own key, so a retry of a request the server
    /// already took is a replay rather than a second ask.
    func request(holderDriverId: String, amountMinor: Int64) async throws -> TransferRow {
        try await wallet.requestWalletCreditTransfer(
            request: RequestWalletCreditTransferRequest(holderDriverId: holderDriverId, amountMinor: amountMinor),
            idempotencyKey: nil
        )
    }

    func send(recipientDriverId: String, amountMinor: Int64) async throws -> TransferRow {
        try await wallet.initiateWalletCreditTransfer(
            request: InitiateWalletCreditTransferRequest(
                recipientDriverId: recipientDriverId,
                amountMinor: amountMinor
            ),
            idempotencyKey: nil
        )
    }

    /// **Read on open and on refresh, not waited for on a push.** D2' §SCR-DI-024 says the requests
    /// *"arrive via APNs"*, and no such notification type exists: `NotificationCatalogue` declares
    /// twenty-six and none of them is a credit transfer, so nothing raises one. A list that only filled
    /// when a push arrived would be permanently empty. Recorded in the C073 handoff; if the type is ever
    /// minted it carries `mageride://wallet`, which ``PushRouter`` already resolves.
    ///
    /// **`Page<T>.items` arrives as `[Any]` and both list reads here put the element type back.** The
    /// export emits the property as `NSArray<id>` — `Page<T>` keeps its type parameter across the bridge
    /// and its rows do not. `compactMap` rather than `as!` for this cluster's own reason: a failed force
    /// cast raises an exception Swift cannot catch and the process terminates. Every row is a
    /// `TransferRow` by contract, so nothing is dropped.
    func pending() async throws -> [TransferRow] {
        try await wallet.listPendingWalletCreditTransfers(page: PageRequest.companion.FIRST)
            .items.compactMap { $0 as? TransferRow }
    }

    /// `direction` is left unset rather than sent as `all`: the parameter's own default is every
    /// direction, and a screen that shows both halves of its history should not have to name them.
    func history(driverId: String, direction: TransferDirectionFilter?) async throws -> [TransferRow] {
        try await wallet.listWalletTransfers(
            driverId: driverId,
            direction: direction,
            page: PageRequest.companion.FIRST
        ).items.compactMap { $0 as? TransferRow }
    }

    /// `402 insufficient-wallet` when the holder cannot cover it **at approval time** rather than at
    /// request time, which is why the screen re-reads the balance after every decision; approving twice
    /// is a `409` and moves nothing.
    func approve(transferId: String) async throws -> TransferRow {
        try await wallet.approveWalletCreditTransfer(transferId: transferId, idempotencyKey: nil)
    }

    func reject(transferId: String) async throws -> TransferRow {
        try await wallet.rejectWalletCreditTransfer(transferId: transferId, idempotencyKey: nil)
    }
}

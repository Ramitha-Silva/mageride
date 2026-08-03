package lk.mageride.driver.wallet

import lk.mageride.shared.data.api.wallet.WalletApi
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.wallet.InitiateWalletCreditTransferRequest
import lk.mageride.shared.data.models.wallet.RequestWalletCreditTransferRequest
import lk.mageride.shared.data.models.wallet.TransferDirectionFilter
import lk.mageride.shared.data.models.wallet.TransferRow

/**
 * SCR-DA-023 and SCR-DA-024 — driver-to-driver credit, both directions.
 *
 * **The two screens are one service and one pair of verbs.** `request` is the pull half — a driver
 * who is short asks a holder, and the holder approves — and `initiate` is the push half, the holder
 * sending without being asked. Both write `billing.credit_transfers` and both post the same balanced
 * `driver_transfer` entry, so splitting them by screen would put one operation in two files.
 *
 * **A transfer moves the exact value and there is no fee leg** (AL-01). Not a configuration, not a
 * rate of zero: there is no journal kind that could carry a commission, `CreditTransferRules.entryFor`
 * produces two postings that sum to zero, and `LedgerEntry`'s own `init` refuses anything else. A
 * screen in this group that rendered a commission line would be describing money nothing can move.
 *
 * **By Driver ID only** (AL-34). The QR-scan path SCR-DA-023 used to draw was removed; nothing here
 * takes a scanned payload and `WalletInput` is the only door onto an id.
 */
internal class CreditTransferRepository(private val wallet: WalletApi) {

    /**
     * `POST /v1/wallet/credit-transfer/request` — ask [holderDriverId] for [amountMinor] (US-9.10).
     *
     * Creates a `PENDING` row the holder then approves or rejects; **nothing moves yet**. The
     * request is attested (D-30) and idempotent on the transport's own key, so a retry of a request
     * the server already took is a replay rather than a second ask.
     */
    suspend fun request(holderDriverId: Ulid, amountMinor: Long): TransferRow = wallet.requestWalletCreditTransfer(
        RequestWalletCreditTransferRequest(holderDriverId = holderDriverId, amountMinor = amountMinor),
    )

    /** `POST /v1/wallet/credit-transfer/initiate` — send credit outright (US-9.20/9.21). */
    suspend fun send(recipientDriverId: Ulid, amountMinor: Long): TransferRow = wallet.initiateWalletCreditTransfer(
        InitiateWalletCreditTransferRequest(recipientDriverId = recipientDriverId, amountMinor = amountMinor),
    )

    /**
     * `GET /v1/wallet/credit-transfer/pending` — SCR-DA-024's approval inbox.
     *
     * **Read on open and on refresh, not waited for on a push.** D2' §SCR-DA-024 says the requests
     * "arrive via push", and no such notification type exists: `NotificationCatalogue` declares
     * twenty-six and none of them is a credit transfer, so nothing raises one. A list that only
     * filled when a push arrived would be permanently empty. Recorded in the C073 handoff; if the
     * type is ever minted it carries `mageride://wallet`, which `PushRouter` already resolves.
     */
    suspend fun pending(): List<TransferRow> = wallet.listPendingWalletCreditTransfers(PageRequest.FIRST).items

    /**
     * `GET /v1/wallet/{driverId}/transfers` — sent and received, newest first (US-9A.11).
     *
     * `direction` is left unset rather than sent as `all`: the parameter's own default is every
     * direction, and a screen that shows both halves of its history should not have to name them.
     */
    suspend fun history(driverId: Ulid, direction: TransferDirectionFilter? = null): List<TransferRow> =
        wallet.listWalletTransfers(driverId = driverId, direction = direction, page = PageRequest.FIRST).items

    /**
     * `POST /v1/wallet/credit-transfer/{transferId}/approve` — move the money (US-9.13).
     *
     * `402 insufficient-wallet` when the holder cannot cover it **at approval time** rather than at
     * request time, which is why the screen re-reads the balance after every decision; approving
     * twice is a `409` and moves nothing.
     */
    suspend fun approve(transferId: Ulid): TransferRow = wallet.approveWalletCreditTransfer(transferId)

    /** `POST /v1/wallet/credit-transfer/{transferId}/reject` — decline. Nothing is posted (US-9.12). */
    suspend fun reject(transferId: Ulid): TransferRow = wallet.rejectWalletCreditTransfer(transferId)
}

package lk.mageride.shared.data.api.wallet

import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.request.accept
import io.ktor.client.request.parameter
import io.ktor.http.ContentType
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPostExempt
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.CallbackAck
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.VoucherDiscountTierList
import lk.mageride.shared.data.models.wallet.InitiateWalletCreditTransferRequest
import lk.mageride.shared.data.models.wallet.LankaqrTopupRequest
import lk.mageride.shared.data.models.wallet.OnepayTopupRequest
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupCallback
import lk.mageride.shared.data.models.wallet.TransferDirectionFilter
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.VoucherDiscountTierUsageList
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction

/**
 * wallet-svc — balance, ledger, driver-to-driver transfers and top-ups
 * (`backend/contracts/wallet.yaml`).
 *
 * **Top-up is OnePay or LankaQR only** — bank transfer was removed by AL-05 and the endpoint is
 * deleted, not deprecated.
 *
 * The transaction list is the one contract route that serves three media types: a JSON page for
 * a screen, and CSV or PDF for a downloadable statement. Each is a separate function here because
 * their return types are genuinely different, not because the route is.
 *
 * **Caveat on the statement downloads.** Ktor's content-negotiation plugin appends
 * `application/json, application/problem+json` to whatever `Accept` a call sets, so the wire header
 * is `text/csv, application/json, application/problem+json` — the requested type first, but not
 * exclusively. The service must honour the first acceptable type; C118's contract tests are where
 * that gets pinned against a running wallet-svc.
 */
@Suppress("TooManyFunctions")
public interface WalletApi {

    /** `GET /v1/wallet/{userId}` — balance, available balance and any outstanding debt. */
    public suspend fun getWallet(userId: Ulid): Wallet

    /** `GET /v1/wallet/{userId}/transactions` — the ledger, newest first. */
    public suspend fun listWalletTransactions(
        userId: Ulid,
        from: BusinessDate? = null,
        to: BusinessDate? = null,
        page: PageRequest = PageRequest.FIRST,
    ): Page<WalletTransaction>

    /** `GET /v1/wallet/{userId}/transactions` with `Accept: text/csv` — the same rows as a statement. */
    public suspend fun downloadWalletStatementCsv(
        userId: Ulid,
        from: BusinessDate? = null,
        to: BusinessDate? = null,
    ): String

    /** `GET /v1/wallet/{userId}/transactions` with `Accept: application/pdf`. */
    public suspend fun downloadWalletStatementPdf(
        userId: Ulid,
        from: BusinessDate? = null,
        to: BusinessDate? = null,
    ): ByteArray

    /** `GET /v1/wallet/{driverId}/transfers` — credit sent to and received from other drivers. */
    public suspend fun listWalletTransfers(
        driverId: Ulid,
        direction: TransferDirectionFilter? = null,
        page: PageRequest = PageRequest.FIRST,
    ): Page<TransferRow>

    /** `POST /v1/wallet/credit-transfer/initiate` — send credit to another driver. Attested. */
    public suspend fun initiateWalletCreditTransfer(
        request: InitiateWalletCreditTransferRequest,
        idempotencyKey: String? = null,
    ): TransferRow

    /** `GET /v1/wallet/voucher/discount-tiers` — the active voucher denominations and discounts. */
    public suspend fun listVoucherDiscountTiers(): VoucherDiscountTierList

    /**
     * `POST /v1/wallet/topup/onepay` — start a card or wallet top-up (D6' §7.1). Attested.
     *
     * Runs on the payment timeout budget: the response waits on OnePay for its redirect.
     */
    public suspend fun topupWithOnepay(request: OnepayTopupRequest, idempotencyKey: String? = null): Topup

    /** `POST /v1/wallet/topup/lankaqr` — start a LankaQR top-up (D-12). Attested. */
    public suspend fun topupWithLankaqr(request: LankaqrTopupRequest, idempotencyKey: String? = null): Topup

    /**
     * `POST /v1/wallet/topup/onepay/webhook` — OnePay reports a completed top-up.
     *
     * **Inbound, HMAC-signed and `x-idempotency-exempt`** (R-19). Not an app call; present for
     * contract coverage and never retried by the transport.
     */
    public suspend fun onepayTopupWebhook(request: TopupCallback): CallbackAck

    /** `POST /v1/wallet/topup/lankaqr/confirm` — the bank IPG equivalent of [onepayTopupWebhook]. */
    public suspend fun lankaqrTopupConfirm(request: TopupCallback): CallbackAck

    /**
     * `GET /v1/wallet/admin/voucher-discount-tiers` — tiers with purchase counts and value.
     *
     * The read model is richer than the public one: same tiers, plus usage.
     */
    public suspend fun adminListVoucherDiscountTiers(): VoucherDiscountTierUsageList

    /** `PUT /v1/wallet/admin/voucher-discount-tiers` — Admin Portal replaces the tier table. */
    public suspend fun adminUpdateVoucherDiscountTiers(request: VoucherDiscountTierList): VoucherDiscountTierList
}

@Suppress("TooManyFunctions")
internal class KtorWalletApi(private val transport: ApiTransport) : WalletApi {

    override suspend fun getWallet(userId: Ulid): Wallet =
        transport.apiGet(SERVICE, "getWallet", "$WALLET_PATH/$userId").decode()

    override suspend fun listWalletTransactions(
        userId: Ulid,
        from: BusinessDate?,
        to: BusinessDate?,
        page: PageRequest,
    ): Page<WalletTransaction> = transport.apiGet(SERVICE, "listWalletTransactions", transactionsPath(userId)) {
        dateRange(from, to)
        pageParameters(page)
    }.decode()

    override suspend fun downloadWalletStatementCsv(userId: Ulid, from: BusinessDate?, to: BusinessDate?): String =
        transport.apiGet(SERVICE, "listWalletTransactions", transactionsPath(userId)) {
            accept(ContentType.Text.CSV)
            dateRange(from, to)
        }.decode()

    override suspend fun downloadWalletStatementPdf(userId: Ulid, from: BusinessDate?, to: BusinessDate?): ByteArray =
        transport.apiGet(SERVICE, "listWalletTransactions", transactionsPath(userId)) {
            accept(ContentType.Application.Pdf)
            dateRange(from, to)
        }.decode()

    override suspend fun listWalletTransfers(
        driverId: Ulid,
        direction: TransferDirectionFilter?,
        page: PageRequest,
    ): Page<TransferRow> = transport.apiGet(SERVICE, "listWalletTransfers", "$WALLET_PATH/$driverId/transfers") {
        parameter("direction", direction?.wire)
        pageParameters(page)
    }.decode()

    override suspend fun initiateWalletCreditTransfer(
        request: InitiateWalletCreditTransferRequest,
        idempotencyKey: String?,
    ): TransferRow = transport.apiPost(
        service = SERVICE,
        operationId = "initiateWalletCreditTransfer",
        path = "$WALLET_PATH/credit-transfer/initiate",
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    override suspend fun listVoucherDiscountTiers(): VoucherDiscountTierList =
        transport.apiGet(SERVICE, "listVoucherDiscountTiers", "$WALLET_PATH/voucher/discount-tiers").decode()

    override suspend fun topupWithOnepay(request: OnepayTopupRequest, idempotencyKey: String?): Topup =
        transport.apiPost(
            service = SERVICE,
            operationId = "topupWithOnepay",
            path = "$TOPUP_PATH/onepay",
            idempotencyKey = idempotencyKey,
            attested = true,
            requestTimeout = transport.config.timeouts.paymentRequestTimeout,
        ) { jsonBody(request) }.decode()

    override suspend fun topupWithLankaqr(request: LankaqrTopupRequest, idempotencyKey: String?): Topup =
        transport.apiPost(
            service = SERVICE,
            operationId = "topupWithLankaqr",
            path = "$TOPUP_PATH/lankaqr",
            idempotencyKey = idempotencyKey,
            attested = true,
            requestTimeout = transport.config.timeouts.paymentRequestTimeout,
        ) { jsonBody(request) }.decode()

    override suspend fun onepayTopupWebhook(request: TopupCallback): CallbackAck =
        transport.apiPostExempt(SERVICE, "onepayTopupWebhook", "$TOPUP_PATH/onepay/webhook") {
            jsonBody(request)
        }.decode()

    override suspend fun lankaqrTopupConfirm(request: TopupCallback): CallbackAck =
        transport.apiPostExempt(SERVICE, "lankaqrTopupConfirm", "$TOPUP_PATH/lankaqr/confirm") {
            jsonBody(request)
        }.decode()

    override suspend fun adminListVoucherDiscountTiers(): VoucherDiscountTierUsageList =
        transport.apiGet(SERVICE, "adminListVoucherDiscountTiers", ADMIN_TIERS_PATH).decode()

    override suspend fun adminUpdateVoucherDiscountTiers(request: VoucherDiscountTierList): VoucherDiscountTierList =
        transport.apiPut(SERVICE, "adminUpdateVoucherDiscountTiers", ADMIN_TIERS_PATH) {
            jsonBody(request)
        }.decode()

    private fun transactionsPath(userId: Ulid): String = "$WALLET_PATH/$userId/transactions"

    private companion object {
        val SERVICE = ApiService.WALLET
        const val WALLET_PATH = "/v1/wallet"
        const val TOPUP_PATH = "/v1/wallet/topup"
        const val ADMIN_TIERS_PATH = "/v1/wallet/admin/voucher-discount-tiers"
    }
}

private fun HttpRequestBuilder.dateRange(from: BusinessDate?, to: BusinessDate?) {
    parameter("from", from?.toString())
    parameter("to", to?.toString())
}

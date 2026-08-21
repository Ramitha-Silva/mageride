package lk.mageride.shared.data.api.subscription

import io.ktor.client.call.body
import io.ktor.client.request.parameter
import kotlin.coroutines.cancellation.CancellationException
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiDelete
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPostExempt
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.filePart
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.multipartBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.CallbackAck
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.AccessRequestAccepted
import lk.mageride.shared.data.models.subscription.ChargeDailyFeeRequest
import lk.mageride.shared.data.models.subscription.CreditTransfer
import lk.mageride.shared.data.models.subscription.DailyFeeCharge
import lk.mageride.shared.data.models.subscription.DailyFeeRateList
import lk.mageride.shared.data.models.subscription.FeeRefundRequest
import lk.mageride.shared.data.models.subscription.FeeRefundRequestList
import lk.mageride.shared.data.models.subscription.MarkSubscriberCashPaidRequest
import lk.mageride.shared.data.models.subscription.ModeBFileKind
import lk.mageride.shared.data.models.subscription.PayModeBSubscriptionRequest
import lk.mageride.shared.data.models.subscription.PurchaseVoucherRequest
import lk.mageride.shared.data.models.subscription.RejectAccessRequest
import lk.mageride.shared.data.models.subscription.RequestCreditTransferRequest
import lk.mageride.shared.data.models.subscription.RequestDailyFeeRefundRequest
import lk.mageride.shared.data.models.subscription.RequestModeBAccessRequest
import lk.mageride.shared.data.models.subscription.SendCreditToDriverRequest
import lk.mageride.shared.data.models.subscription.SetSubscriberFareRequest
import lk.mageride.shared.data.models.subscription.SubscriberRow
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionProviderCallback
import lk.mageride.shared.data.models.subscription.TodaysDailyFee
import lk.mageride.shared.data.models.subscription.VoucherDiscountTierList
import lk.mageride.shared.data.models.subscription.VoucherPurchase

/**
 * subscription-svc — the driver daily fee, credit transfers, vouchers and Mode B subscriptions
 * (`backend/contracts/subscription.yaml`).
 *
 * Two unrelated money flows share this contract because they share a ledger:
 * - **Driver side** — the daily fee (US-9.1/9.7), reseller credit transfers and voucher
 *   purchases.
 * - **Mode B side** — a passenger subscribing to a shared private vehicle, and the monthly
 *   payment that keeps the subscription alive (AL-24).
 *
 * Business dates are `Asia/Colombo` (D-13) and every one of them arrives with the instant it was
 * derived at (D-38) — `feeDate` with `feeDateTzAt`, `periodMonth` with `periodMonthTzAt`. Do not
 * recompute either from the device clock.
 */
@Suppress("TooManyFunctions")
public interface SubscriptionApi {

    /** `GET /v1/fees/rates` — the daily fee per vehicle type and mode. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listDailyFeeRates(): DailyFeeRateList

    /**
     * `GET /v1/fees/{driverId}/today` — today's fee, what has been deducted and the trip count.
     *
     * `firstTripFree` is the D-08 degraded-mode allowance surfacing in the read model.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getTodaysDailyFee(driverId: Ulid): TodaysDailyFee

    /**
     * `POST /v1/fees/{driverId}/refund-requests` — dispute a daily-fee charge (Δ C047, US-9.23).
     *
     * Raises one `support.tickets` row, so its status is the ticket's and support-svc owns it.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun requestDailyFeeRefund(
        driverId: Ulid,
        request: RequestDailyFeeRefundRequest,
        idempotencyKey: String? = null,
    ): FeeRefundRequest

    /** `GET /v1/fees/{driverId}/refund-requests` — the driver's disputes, newest first. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listDailyFeeRefundRequests(driverId: Ulid, limit: Int? = null): FeeRefundRequestList

    /**
     * `GET /v1/mode-b/files/{kind}/{id}` — a Mode-B document behind a signed link.
     *
     * **Unauthenticated, because the signature is the credential** — the same reasoning as
     * support-svc's screenshot read. The URL goes to an image loader, which carries no bearer,
     * and an access token in a query string is an access token in every proxy log on the way.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getModeBFile(kind: ModeBFileKind, id: Ulid, expires: Long, signature: String): ByteArray

    /** `GET /v1/fees/{driverId}/history` — past daily-fee charges, optionally date-bounded. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listDailyFeeHistory(
        driverId: Ulid,
        from: BusinessDate? = null,
        to: BusinessDate? = null,
        page: PageRequest = PageRequest.FIRST,
    ): Page<DailyFeeCharge>

    /**
     * `POST /v1/internal/fees/{driverId}/charge-before-trip` — take the fee before dispatch.
     *
     * **Service-to-service (mTLS).** `402 insufficient-wallet` is the gate D-08 describes.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun chargeDailyFeeBeforeTrip(
        driverId: Ulid,
        request: ChargeDailyFeeRequest,
        idempotencyKey: String? = null,
    ): DailyFeeCharge

    /** `POST /v1/subscriptions/credit-transfer/request` — a driver asks a reseller for credit. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun requestCreditTransfer(
        request: RequestCreditTransferRequest,
        idempotencyKey: String? = null,
    ): CreditTransfer

    /** `GET /v1/subscriptions/credit-transfer/pending` — requests waiting on this reseller. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listPendingCreditTransfers(page: PageRequest = PageRequest.FIRST): Page<CreditTransfer>

    /** `POST /v1/subscriptions/credit-transfer/{transferId}/approve` — pay it. Attested (D-30). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun approveCreditTransfer(transferId: Ulid, idempotencyKey: String? = null): CreditTransfer

    /** `POST /v1/subscriptions/credit-transfer/{transferId}/reject` — decline it. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun rejectCreditTransfer(transferId: Ulid, idempotencyKey: String? = null): CreditTransfer

    /** `POST /v1/transfers/driver` — push credit to another driver outright. Attested (D-30). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun sendCreditToDriver(
        request: SendCreditToDriverRequest,
        idempotencyKey: String? = null,
    ): CreditTransfer

    /** `POST /v1/vouchers/purchase` — buy discounted credit at a denomination tier. Attested. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun purchaseVoucher(
        request: PurchaseVoucherRequest,
        idempotencyKey: String? = null,
    ): VoucherPurchase

    /** `GET /v1/mode-b/{vehicleId}/access-requests` — passengers asking to join this vehicle. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listModeBAccessRequests(
        vehicleId: Ulid,
        page: PageRequest = PageRequest.FIRST,
    ): Page<AccessRequest>

    /** `POST /v1/mode-b/{vehicleId}/access-requests` — a passenger asks to join. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun requestModeBAccess(
        vehicleId: Ulid,
        request: RequestModeBAccessRequest,
        idempotencyKey: String? = null,
    ): AccessRequest

    /** `POST /v1/mode-b/access-requests/{requestId}/accept` — the owner admits them. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun acceptModeBAccessRequest(requestId: Ulid, idempotencyKey: String? = null): AccessRequestAccepted

    /** `POST /v1/mode-b/access-requests/{requestId}/reject` — the owner declines. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun rejectModeBAccessRequest(
        requestId: Ulid,
        request: RejectAccessRequest,
        idempotencyKey: String? = null,
    ): AccessRequest

    /** `GET /v1/mode-b/subscriptions/{passengerId}` — this passenger's Mode B subscriptions. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listPassengerSubscriptions(
        passengerId: Ulid,
        page: PageRequest = PageRequest.FIRST,
    ): Page<Subscription>

    /** `POST /v1/mode-b/subscriptions/{subscriptionId}/unsubscribe` — leave the vehicle. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun unsubscribeModeB(subscriptionId: Ulid, idempotencyKey: String? = null): Subscription

    /** `POST /v1/mode-b/subscriptions/{subscriptionId}/pay` — pay a month (AL-24). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun payModeBSubscription(
        subscriptionId: Ulid,
        request: PayModeBSubscriptionRequest,
        idempotencyKey: String? = null,
    ): SubscriptionPayment

    /** `GET /v1/mode-b/subscriptions/{subscriptionId}/payments` — this subscription's payments. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listSubscriptionPayments(
        subscriptionId: Ulid,
        page: PageRequest = PageRequest.FIRST,
    ): Page<SubscriptionPayment>

    /** `POST /v1/mode-b/payments/{paymentId}/transfer-slip` — upload the bank slip. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun uploadTransferSlip(
        paymentId: Ulid,
        file: FileUpload,
        idempotencyKey: String? = null,
    ): SubscriptionPayment

    /** `POST /v1/mode-b/payments/{paymentId}/confirm` — the owner accepts the slip. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun confirmTransferSlip(paymentId: Ulid, idempotencyKey: String? = null): SubscriptionPayment

    /**
     * `POST /v1/mode-b/pay/lankaqr/confirm` — the bank IPG reports a subscription payment.
     *
     * **Inbound, HMAC-signed and `x-idempotency-exempt`** (R-19). Not an app call; present for
     * contract coverage and never retried by the transport.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun modeBLankaqrConfirm(request: SubscriptionProviderCallback): CallbackAck

    /** `GET /v1/mode-b/{vehicleId}/subscribers` — the owner's subscriber roster. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listModeBSubscribers(
        vehicleId: Ulid,
        page: PageRequest = PageRequest.FIRST,
    ): Page<SubscriberRow>

    /** `DELETE /v1/mode-b/{vehicleId}/subscribers/{subscriberId}` — remove a subscriber. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun deleteModeBSubscriber(vehicleId: Ulid, subscriberId: Ulid)

    /** `PUT /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/fare` — per-subscriber monthly fare. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun setSubscriberFare(
        vehicleId: Ulid,
        subscriberId: Ulid,
        request: SetSubscriberFareRequest,
    ): SubscriberRow

    /** `POST /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/mark-cash` — record a cash month. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun markSubscriberCashPaid(
        vehicleId: Ulid,
        subscriberId: Ulid,
        request: MarkSubscriberCashPaidRequest,
        idempotencyKey: String? = null,
    ): SubscriptionPayment

    /** `GET /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/payments` — one subscriber's history. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listSubscriberPayments(
        vehicleId: Ulid,
        subscriberId: Ulid,
        page: PageRequest = PageRequest.FIRST,
    ): Page<SubscriptionPayment>

    /** `PUT /v1/admin/fees/rates` — Admin Portal sets the daily fee table. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun updateDailyFeeRates(request: DailyFeeRateList): DailyFeeRateList

    /** `PUT /v1/admin/voucher-discount-tiers` — Admin Portal sets the voucher discount tiers. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun updateVoucherDiscountTiers(request: VoucherDiscountTierList): VoucherDiscountTierList
}

@Suppress("TooManyFunctions", "LargeClass")
internal class KtorSubscriptionApi(private val transport: ApiTransport) : SubscriptionApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listDailyFeeRates(): DailyFeeRateList =
        transport.apiGet(SERVICE, "listDailyFeeRates", "$FEES_PATH/rates").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getTodaysDailyFee(driverId: Ulid): TodaysDailyFee =
        transport.apiGet(SERVICE, "getTodaysDailyFee", "$FEES_PATH/$driverId/today").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun requestDailyFeeRefund(
        driverId: Ulid,
        request: RequestDailyFeeRefundRequest,
        idempotencyKey: String?,
    ): FeeRefundRequest = transport.apiPost(
        service = SERVICE,
        operationId = "requestDailyFeeRefund",
        path = "$FEES_PATH/$driverId/refund-requests",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listDailyFeeRefundRequests(driverId: Ulid, limit: Int?): FeeRefundRequestList =
        transport.apiGet(SERVICE, "listDailyFeeRefundRequests", "$FEES_PATH/$driverId/refund-requests") {
            limit?.let { parameter("limit", it) }
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getModeBFile(kind: ModeBFileKind, id: Ulid, expires: Long, signature: String): ByteArray =
        transport.apiGet(SERVICE, "getModeBFile", "$MODE_B_PATH/files/${kind.wire}/$id") {
            parameter("expires", expires)
            parameter("signature", signature)
        }.body()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listDailyFeeHistory(
        driverId: Ulid,
        from: BusinessDate?,
        to: BusinessDate?,
        page: PageRequest,
    ): Page<DailyFeeCharge> = transport.apiGet(SERVICE, "listDailyFeeHistory", "$FEES_PATH/$driverId/history") {
        parameter("from", from?.toString())
        parameter("to", to?.toString())
        pageParameters(page)
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun chargeDailyFeeBeforeTrip(
        driverId: Ulid,
        request: ChargeDailyFeeRequest,
        idempotencyKey: String?,
    ): DailyFeeCharge = transport.apiPost(
        service = SERVICE,
        operationId = "chargeDailyFeeBeforeTrip",
        path = "/v1/internal/fees/$driverId/charge-before-trip",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun requestCreditTransfer(
        request: RequestCreditTransferRequest,
        idempotencyKey: String?,
    ): CreditTransfer = transport.apiPost(
        service = SERVICE,
        operationId = "requestCreditTransfer",
        path = "$CREDIT_TRANSFER_PATH/request",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listPendingCreditTransfers(page: PageRequest): Page<CreditTransfer> =
        transport.apiGet(SERVICE, "listPendingCreditTransfers", "$CREDIT_TRANSFER_PATH/pending") {
            pageParameters(page)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun approveCreditTransfer(transferId: Ulid, idempotencyKey: String?): CreditTransfer =
        transport.apiPost(
            service = SERVICE,
            operationId = "approveCreditTransfer",
            path = "$CREDIT_TRANSFER_PATH/$transferId/approve",
            idempotencyKey = idempotencyKey,
            attested = true,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun rejectCreditTransfer(transferId: Ulid, idempotencyKey: String?): CreditTransfer =
        transport.apiPost(
            service = SERVICE,
            operationId = "rejectCreditTransfer",
            path = "$CREDIT_TRANSFER_PATH/$transferId/reject",
            idempotencyKey = idempotencyKey,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun sendCreditToDriver(
        request: SendCreditToDriverRequest,
        idempotencyKey: String?,
    ): CreditTransfer = transport.apiPost(
        service = SERVICE,
        operationId = "sendCreditToDriver",
        path = "/v1/transfers/driver",
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun purchaseVoucher(request: PurchaseVoucherRequest, idempotencyKey: String?): VoucherPurchase =
        transport.apiPost(
            service = SERVICE,
            operationId = "purchaseVoucher",
            path = "/v1/vouchers/purchase",
            idempotencyKey = idempotencyKey,
            attested = true,
            requestTimeout = transport.config.timeouts.paymentRequestTimeout,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listModeBAccessRequests(vehicleId: Ulid, page: PageRequest): Page<AccessRequest> =
        transport.apiGet(SERVICE, "listModeBAccessRequests", accessRequestsPath(vehicleId)) {
            pageParameters(page)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun requestModeBAccess(
        vehicleId: Ulid,
        request: RequestModeBAccessRequest,
        idempotencyKey: String?,
    ): AccessRequest = transport.apiPost(
        service = SERVICE,
        operationId = "requestModeBAccess",
        path = accessRequestsPath(vehicleId),
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun acceptModeBAccessRequest(requestId: Ulid, idempotencyKey: String?): AccessRequestAccepted =
        transport.apiPost(
            service = SERVICE,
            operationId = "acceptModeBAccessRequest",
            path = "$MODE_B_PATH/access-requests/$requestId/accept",
            idempotencyKey = idempotencyKey,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun rejectModeBAccessRequest(
        requestId: Ulid,
        request: RejectAccessRequest,
        idempotencyKey: String?,
    ): AccessRequest = transport.apiPost(
        service = SERVICE,
        operationId = "rejectModeBAccessRequest",
        path = "$MODE_B_PATH/access-requests/$requestId/reject",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listPassengerSubscriptions(passengerId: Ulid, page: PageRequest): Page<Subscription> =
        transport.apiGet(SERVICE, "listPassengerSubscriptions", "$SUBSCRIPTIONS_PATH/$passengerId") {
            pageParameters(page)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun unsubscribeModeB(subscriptionId: Ulid, idempotencyKey: String?): Subscription =
        transport.apiPost(
            service = SERVICE,
            operationId = "unsubscribeModeB",
            path = "$SUBSCRIPTIONS_PATH/$subscriptionId/unsubscribe",
            idempotencyKey = idempotencyKey,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun payModeBSubscription(
        subscriptionId: Ulid,
        request: PayModeBSubscriptionRequest,
        idempotencyKey: String?,
    ): SubscriptionPayment = transport.apiPost(
        service = SERVICE,
        operationId = "payModeBSubscription",
        path = "$SUBSCRIPTIONS_PATH/$subscriptionId/pay",
        idempotencyKey = idempotencyKey,
        requestTimeout = transport.config.timeouts.paymentRequestTimeout,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listSubscriptionPayments(subscriptionId: Ulid, page: PageRequest): Page<SubscriptionPayment> =
        transport.apiGet(SERVICE, "listSubscriptionPayments", "$SUBSCRIPTIONS_PATH/$subscriptionId/payments") {
            pageParameters(page)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun uploadTransferSlip(
        paymentId: Ulid,
        file: FileUpload,
        idempotencyKey: String?,
    ): SubscriptionPayment = transport.apiPost(
        service = SERVICE,
        operationId = "uploadTransferSlip",
        path = "$MODE_B_PATH/payments/$paymentId/transfer-slip",
        idempotencyKey = idempotencyKey,
    ) { multipartBody { filePart("file", file) } }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun confirmTransferSlip(paymentId: Ulid, idempotencyKey: String?): SubscriptionPayment =
        transport.apiPost(
            service = SERVICE,
            operationId = "confirmTransferSlip",
            path = "$MODE_B_PATH/payments/$paymentId/confirm",
            idempotencyKey = idempotencyKey,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun modeBLankaqrConfirm(request: SubscriptionProviderCallback): CallbackAck =
        transport.apiPostExempt(SERVICE, "modeBLankaqrConfirm", "$MODE_B_PATH/pay/lankaqr/confirm") {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listModeBSubscribers(vehicleId: Ulid, page: PageRequest): Page<SubscriberRow> =
        transport.apiGet(SERVICE, "listModeBSubscribers", subscribersPath(vehicleId)) {
            pageParameters(page)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun deleteModeBSubscriber(vehicleId: Ulid, subscriberId: Ulid) {
        transport.apiDelete(SERVICE, "deleteModeBSubscriber", "${subscribersPath(vehicleId)}/$subscriberId")
    }

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun setSubscriberFare(
        vehicleId: Ulid,
        subscriberId: Ulid,
        request: SetSubscriberFareRequest,
    ): SubscriberRow =
        transport.apiPut(SERVICE, "setSubscriberFare", "${subscribersPath(vehicleId)}/$subscriberId/fare") {
            jsonBody(request)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun markSubscriberCashPaid(
        vehicleId: Ulid,
        subscriberId: Ulid,
        request: MarkSubscriberCashPaidRequest,
        idempotencyKey: String?,
    ): SubscriptionPayment = transport.apiPost(
        service = SERVICE,
        operationId = "markSubscriberCashPaid",
        path = "${subscribersPath(vehicleId)}/$subscriberId/mark-cash",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listSubscriberPayments(
        vehicleId: Ulid,
        subscriberId: Ulid,
        page: PageRequest,
    ): Page<SubscriptionPayment> = transport.apiGet(
        service = SERVICE,
        operationId = "listSubscriberPayments",
        path = "${subscribersPath(vehicleId)}/$subscriberId/payments",
    ) { pageParameters(page) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun updateDailyFeeRates(request: DailyFeeRateList): DailyFeeRateList =
        transport.apiPut(SERVICE, "updateDailyFeeRates", "/v1/admin/fees/rates") { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun updateVoucherDiscountTiers(request: VoucherDiscountTierList): VoucherDiscountTierList =
        transport.apiPut(SERVICE, "updateVoucherDiscountTiers", "/v1/admin/voucher-discount-tiers") {
            jsonBody(request)
        }.decode()

    private fun accessRequestsPath(vehicleId: Ulid): String = "$MODE_B_PATH/$vehicleId/access-requests"

    private fun subscribersPath(vehicleId: Ulid): String = "$MODE_B_PATH/$vehicleId/subscribers"

    private companion object {
        val SERVICE = ApiService.SUBSCRIPTION
        const val FEES_PATH = "/v1/fees"
        const val CREDIT_TRANSFER_PATH = "/v1/subscriptions/credit-transfer"
        const val MODE_B_PATH = "/v1/mode-b"
        const val SUBSCRIPTIONS_PATH = "/v1/mode-b/subscriptions"
    }
}

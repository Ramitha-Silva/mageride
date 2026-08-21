package lk.mageride.shared.data.api.fare

import io.ktor.client.request.parameter
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.fare.CalculateFinalFareRequest
import lk.mageride.shared.data.models.fare.ClaimDriverQrRequest
import lk.mageride.shared.data.models.fare.ConfirmDriverQrRequest
import lk.mageride.shared.data.models.fare.DisputeDriverQrRequest
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.fare.FinalFareResponse
import lk.mageride.shared.data.models.fare.InitiatePaymentRequest
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.fare.RefundFareRequest
import lk.mageride.shared.data.models.fare.RefundResponse
import lk.mageride.shared.data.models.fare.ScanDriverQrRequest
import lk.mageride.shared.data.models.support.TicketRef
import kotlin.coroutines.cancellation.CancellationException

/**
 * fare-svc — estimates, the final fare and the payment state machine
 * (`backend/contracts/fare.yaml`, D-10).
 *
 * **Money is integer minor units everywhere** (CLAUDE.md); nothing in this client takes or
 * returns a `Double` amount.
 *
 * [initiatePayment] and the top-ups in [lk.mageride.shared.data.api.wallet.WalletApi] are the
 * only app-facing calls that wait on an external gateway, so they run on the longer
 * [lk.mageride.shared.data.api.ApiTimeouts.paymentRequestTimeout] budget rather than the 15-second
 * one (D6' §8.3).
 */
@Suppress("TooManyFunctions")
public interface FareApi {

    /**
     * `GET /v1/fare/estimate` — a quote plus the `fareEstimateToken` a booking must carry.
     *
     * The token is what stops a client inventing its own price: `POST /v1/rides/request` rejects
     * a stale or forged one with `400 invalid-fare-token`.
     *
     * `422 route-unavailable` when the router cannot connect the two points;
     * `400 unserviceable-area` outside an operating city.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun estimateFare(
        fromLat: Double,
        fromLng: Double,
        toLat: Double,
        toLng: Double,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind? = null,
    ): FareEstimateResponse

    /**
     * `POST /v1/fare/calculate` — settle the final fare from the actual distance and duration.
     *
     * **Service-to-service (mTLS).** ride-svc calls this on completion; present for contract
     * coverage.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun calculateFinalFare(
        request: CalculateFinalFareRequest,
        idempotencyKey: String? = null,
    ): FinalFareResponse

    /**
     * `POST /v1/fare/pay` — start payment for a completed ride. Attested (D-30).
     *
     * The response carries whichever provider hand-off the chosen method needs: a OnePay
     * redirect or a LankaQR payload. `409 payment-already-settled` if the ride is already paid.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun initiatePayment(
        request: InitiatePaymentRequest,
        idempotencyKey: String? = null,
    ): PaymentInitiation

    /** `GET /v1/fare/pay/{paymentId}/status` — poll the payment state machine. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getPaymentStatus(paymentId: Ulid): PaymentStatus

    /** `POST /v1/fare/pay/{paymentId}/fallback-cash` — abandon the gateway and settle in cash. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun fallbackToCash(paymentId: Ulid, idempotencyKey: String? = null): PaymentStatus

    /** `POST /v1/fare/pay/scan-driver-qr` — the passenger pays by scanning the driver's QR (AL-22). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun payByScanningDriverQr(
        request: ScanDriverQrRequest,
        idempotencyKey: String? = null,
    ): PaymentStatus

    /**
     * `POST /v1/fare/pay/driver-qr/claim` — the passenger asserts they have paid (AL-47).
     *
     * `202`: the payment moves to `QrClaimedByPassenger` and waits for [confirmDriverQrPayment].
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun claimDriverQrPayment(
        request: ClaimDriverQrRequest,
        idempotencyKey: String? = null,
    ): PaymentStatus

    /** `POST /v1/fare/pay/driver-qr/confirm` — the driver attests the money arrived (AL-47). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun confirmDriverQrPayment(
        request: ConfirmDriverQrRequest,
        idempotencyKey: String? = null,
    ): PaymentStatus

    /** `POST /v1/fare/pay/driver-qr/dispute` — the driver says it did not. Opens a ticket. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun disputeDriverQrPayment(
        request: DisputeDriverQrRequest,
        idempotencyKey: String? = null,
    ): TicketRef

    /** `POST /v1/admin/fare/refund` — Finance Officer issues a refund. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun refundFare(request: RefundFareRequest, idempotencyKey: String? = null): RefundResponse
}

@Suppress("TooManyFunctions")
internal class KtorFareApi(private val transport: ApiTransport) : FareApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun estimateFare(
        fromLat: Double,
        fromLng: Double,
        toLat: Double,
        toLng: Double,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind?,
    ): FareEstimateResponse = transport.apiGet(SERVICE, "estimateFare", "/v1/fare/estimate") {
        parameter("fromLat", fromLat)
        parameter("fromLng", fromLng)
        parameter("toLat", toLat)
        parameter("toLng", toLng)
        parameter("vehicleType", vehicleType.wire)
        parameter("kind", kind?.wire)
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun calculateFinalFare(
        request: CalculateFinalFareRequest,
        idempotencyKey: String?,
    ): FinalFareResponse = transport.apiPost(
        service = SERVICE,
        operationId = "calculateFinalFare",
        path = "/v1/fare/calculate",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun initiatePayment(request: InitiatePaymentRequest, idempotencyKey: String?): PaymentInitiation =
        transport.apiPost(
            service = SERVICE,
            operationId = "initiatePayment",
            path = PAY_PATH,
            idempotencyKey = idempotencyKey,
            attested = true,
            requestTimeout = transport.config.timeouts.paymentRequestTimeout,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getPaymentStatus(paymentId: Ulid): PaymentStatus =
        transport.apiGet(SERVICE, "getPaymentStatus", "$PAY_PATH/$paymentId/status").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun fallbackToCash(paymentId: Ulid, idempotencyKey: String?): PaymentStatus = transport.apiPost(
        service = SERVICE,
        operationId = "fallbackToCash",
        path = "$PAY_PATH/$paymentId/fallback-cash",
        idempotencyKey = idempotencyKey,
    ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun payByScanningDriverQr(request: ScanDriverQrRequest, idempotencyKey: String?): PaymentStatus =
        transport.apiPost(
            service = SERVICE,
            operationId = "payByScanningDriverQr",
            path = "$PAY_PATH/scan-driver-qr",
            idempotencyKey = idempotencyKey,
            attested = true,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun claimDriverQrPayment(request: ClaimDriverQrRequest, idempotencyKey: String?): PaymentStatus =
        transport.apiPost(
            service = SERVICE,
            operationId = "claimDriverQrPayment",
            path = "$DRIVER_QR_PATH/claim",
            idempotencyKey = idempotencyKey,
            attested = true,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun confirmDriverQrPayment(
        request: ConfirmDriverQrRequest,
        idempotencyKey: String?,
    ): PaymentStatus = transport.apiPost(
        service = SERVICE,
        operationId = "confirmDriverQrPayment",
        path = "$DRIVER_QR_PATH/confirm",
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun disputeDriverQrPayment(request: DisputeDriverQrRequest, idempotencyKey: String?): TicketRef =
        transport.apiPost(
            service = SERVICE,
            operationId = "disputeDriverQrPayment",
            path = "$DRIVER_QR_PATH/dispute",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun refundFare(request: RefundFareRequest, idempotencyKey: String?): RefundResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "refundFare",
            path = "/v1/admin/fare/refund",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }.decode()

    private companion object {
        val SERVICE = ApiService.FARE
        const val PAY_PATH = "/v1/fare/pay"
        const val DRIVER_QR_PATH = "/v1/fare/pay/driver-qr"
    }
}

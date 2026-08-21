package lk.mageride.shared.data.api.ride

import kotlin.coroutines.cancellation.CancellationException
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.decodeOrNull
import lk.mageride.shared.data.api.filePart
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.multipartBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.api.textPart
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.data.models.ride.AcceptRideOfferResponse
import lk.mageride.shared.data.models.ride.CancelRideRequest
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.CompleteRideResponse
import lk.mageride.shared.data.models.ride.ConfirmCashOnDeliveryRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.DeclineRideOfferRequest
import lk.mageride.shared.data.models.ride.DisputeRideRequest
import lk.mageride.shared.data.models.ride.ExpireRideOfferRequest
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.MarkRideMatchingRequest
import lk.mageride.shared.data.models.ride.NotifyPaymentSettledRequest
import lk.mageride.shared.data.models.ride.OfferPlaced
import lk.mageride.shared.data.models.ride.OtpAttempt
import lk.mageride.shared.data.models.ride.PlaceRideOfferRequest
import lk.mageride.shared.data.models.ride.ProofArtifactResponse
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.ride.RideSagaState
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.data.models.ride.StartRideRequest
import lk.mageride.shared.data.models.ride.SystemCancelRideRequest
import lk.mageride.shared.data.models.ride.VersionedCommand
import lk.mageride.shared.data.models.support.TicketRef

/**
 * ride-svc — the Mode C ride aggregate and its sole write surface
 * (`backend/contracts/ride.yaml`, R-01).
 *
 * **Every state-changing call is versioned.** The body carries the `version` the caller believes
 * the ride is at, and a stale one is `409 version-conflict` rather than a silent overwrite —
 * that is the optimistic-concurrency contract D3' §0 states for all of ride-svc. Re-read the
 * ride, re-decide, re-send: do not bump the number and retry.
 *
 * **The offer race has two distinct outcomes** and this client keeps them apart:
 * [acceptRideOffer] answers `409 offer-already-accepted` when another driver won, and
 * `410 offer-expired` when the fifteen-second window simply elapsed. They arrive as
 * [lk.mageride.shared.data.api.MageRideError.Conflict] and
 * [lk.mageride.shared.data.api.MageRideError.Gone].
 *
 * C015 owns the state machine that decides which of these calls is legal when; this client only
 * knows how to make them.
 */
@Suppress("TooManyFunctions")
public interface RideApi {

    /**
     * `POST /v1/rides/request` — book a Mode C ride. Attested (D-30).
     *
     * `202`: the ride exists and dispatch has started. `409 active-ride-exists` when the
     * passenger already has one in flight; `400 invalid-fare-token` when the estimate has aged out.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun requestRide(request: RideRequest, idempotencyKey: String? = null): RequestRideResponse

    /** `GET /v1/rides/history` — completed rides, newest first. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listRideHistory(page: PageRequest = PageRequest.FIRST): Page<RideHistoryRow>

    /** `GET /v1/rides/passenger/{passengerId}/active` — the live ride, or `null`. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getActivePassengerRide(passengerId: Ulid): RideDetail?

    /** `GET /v1/rides/driver/{driverId}/active` — the live ride, or `null`. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getActiveDriverRide(driverId: Ulid): RideDetail?

    /** `GET /v1/rides/{rideId}` — the full ride. `403 not-ride-participant` for anyone else. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getRide(rideId: Ulid): RideDetail

    /**
     * `GET /v1/rides/{rideId}/state` — state, version and the offer deadline, without the payload.
     *
     * The cheap poll for a screen that is waiting on a transition; the live path is the SignalR
     * hub (D3' §3.1), and this is its fallback when the backplane is down (D6' §8.3).
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getRideState(rideId: Ulid): RideStateSnapshot

    /**
     * `POST /v1/rides/{rideId}/offer/{driverId}/accept` — take the offer. Attested (D-30).
     *
     * The atomic accept: exactly one driver wins. See the interface KDoc for the two ways of
     * losing. `402 insufficient-wallet` is the D-08 daily-fee gate, not a dispatch failure.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun acceptRideOffer(
        rideId: Ulid,
        driverId: Ulid,
        request: AcceptRideOfferRequest,
        idempotencyKey: String? = null,
    ): AcceptRideOfferResponse

    /** `POST /v1/rides/{rideId}/offer/{driverId}/decline` — pass on the offer. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun declineRideOffer(
        rideId: Ulid,
        driverId: Ulid,
        request: DeclineRideOfferRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /** `POST /v1/rides/{rideId}/arrive` — the driver is at the pickup. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun markDriverArrived(
        rideId: Ulid,
        request: VersionedCommand,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `POST /v1/rides/{rideId}/start` — begin the ride, quoting the pickup OTP.
     *
     * `423 otp-locked` once the attempt budget is spent.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun startRide(
        rideId: Ulid,
        request: StartRideRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /** `POST /v1/rides/{rideId}/complete` — end the ride; the response carries the final fare. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun completeRide(
        rideId: Ulid,
        request: VersionedCommand,
        idempotencyKey: String? = null,
    ): CompleteRideResponse

    /**
     * `POST /v1/rides/{rideId}/cancel` — cancel, with a reason.
     *
     * The response's `penalty` is the D-05 cross-trip settlement: a cancellation fee can be
     * carried to the passenger's next trip rather than charged now.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun cancelRide(
        rideId: Ulid,
        request: CancelRideRequest,
        idempotencyKey: String? = null,
    ): CancelRideResponse

    /** `POST /v1/rides/{rideId}/dispute` — raise a support ticket against a completed ride. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun disputeRide(
        rideId: Ulid,
        request: DisputeRideRequest,
        idempotencyKey: String? = null,
    ): TicketRef

    /** `POST /v1/rides/{rideId}/package/pickup-otp` — the sender's OTP releases the package. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun verifyPackagePickupOtp(
        rideId: Ulid,
        request: OtpAttempt,
        idempotencyKey: String? = null,
    ): RideStateChange

    /** `POST /v1/rides/{rideId}/package/delivery-otp` — the recipient's OTP accepts it (AL-21). */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun verifyPackageDeliveryOtp(
        rideId: Ulid,
        request: OtpAttempt,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `POST /v1/rides/{rideId}/package/proof-photo` — proof of delivery, as `multipart/form-data`.
     *
     * **It completes the delivery** (Δ C037): the photograph is the delivery OTP's alternative, not
     * a filing beside it, so this is legal only from `InProgress` and the response carries the
     * ride's new state.
     *
     * @param lat Where the handset says the photo was taken, for
     *   `rides.proof_artifacts.captured_geo` (D5' §11). Absent means no fix — a lift well, a
     *   basement — which is a photo without a position rather than a refused delivery.
     * @param lng The other half of that coordinate.
     */
    @Suppress("LongParameterList") // Five multipart parts, and each is one the contract declares.
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun uploadPackageProofPhoto(
        rideId: Ulid,
        file: FileUpload,
        note: String? = null,
        lat: Double? = null,
        lng: Double? = null,
        idempotencyKey: String? = null,
    ): ProofArtifactResponse

    /**
     * `POST /v1/rides/{rideId}/cod-collected` — the driver confirms cash on delivery. Attested.
     *
     * `409 payment-already-settled` if this ride has already been paid another way.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun confirmCashOnDelivery(
        rideId: Ulid,
        request: ConfirmCashOnDeliveryRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `POST /v1/location-requests` — ask a proxy rider to share their pickup point (P-02, P-13).
     *
     * Attested, and rate limited: `429 loc-request-rate-limited`.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun createLocationRequest(
        request: CreateLocationRequestRequest,
        idempotencyKey: String? = null,
    ): CreateLocationRequestResponse

    /** `GET /v1/location-requests/{requestId}` — poll the request's state. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getLocationRequest(requestId: Ulid): LocationRequest

    /** `POST /v1/location-requests/{requestId}/confirm` — the rider shares their position. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun confirmLocationRequest(
        requestId: Ulid,
        request: GeoPointWithAccuracy,
        idempotencyKey: String? = null,
    ): LocationRequest

    /** `POST /v1/location-requests/{requestId}/decline` — the rider refuses. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun declineLocationRequest(requestId: Ulid, idempotencyKey: String? = null): LocationRequest

    /**
     * `POST /v1/internal/rides/{rideId}/matching` — dispatch-svc has begun building candidates.
     *
     * **Service-to-service (`internalKey`).** Present for contract coverage; not reachable from
     * an app. `Requested → Matching`, driven by dispatch because ride-svc is the sole writer of
     * `rides.state` (ADD §11.12).
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun markRideMatching(
        rideId: Ulid,
        request: MarkRideMatchingRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `POST /v1/internal/rides/{rideId}/offer` — arm the 15 s offer window (`Matching → Offered`).
     *
     * **Service-to-service (`internalKey`).** Present for contract coverage; not reachable from
     * an app. The deadline in [OfferPlaced] is ride-svc's, not the caller's.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun placeRideOffer(
        rideId: Ulid,
        request: PlaceRideOfferRequest,
        idempotencyKey: String? = null,
    ): OfferPlaced

    /**
     * `POST /v1/internal/rides/{rideId}/offer/expire` — the window closed unanswered (R-04).
     *
     * **Service-to-service (`internalKey`).** Present for contract coverage; not reachable from
     * an app. `Offered → Matching`, bound to `offer_expires_at <= now()` evaluated by Postgres so
     * a sweeper whose clock ran ahead cannot take a driver's window away.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun expireRideOffer(
        rideId: Ulid,
        request: ExpireRideOfferRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `POST /v1/internal/rides/{rideId}/system-cancel` — dispatch or a timeout kills the ride.
     *
     * **Service-to-service (mTLS).** Present for contract coverage; not reachable from an app.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun systemCancelRide(
        rideId: Ulid,
        request: SystemCancelRideRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `POST /v1/internal/rides/{rideId}/payment-settled` — fare-svc reports settlement.
     *
     * **Service-to-service (mTLS).** Present for contract coverage; not reachable from an app.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun notifyPaymentSettled(
        rideId: Ulid,
        request: NotifyPaymentSettledRequest,
        idempotencyKey: String? = null,
    ): RideStateChange

    /**
     * `GET /v1/internal/rides/{rideId}/saga-state` — transitions and pending outbox depth.
     *
     * **Service-to-service (mTLS).** Present for contract coverage; not reachable from an app.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getRideSagaState(rideId: Ulid): RideSagaState
}

@Suppress("TooManyFunctions")
internal class KtorRideApi(private val transport: ApiTransport) : RideApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun requestRide(request: RideRequest, idempotencyKey: String?): RequestRideResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "requestRide",
            path = "$RIDES_PATH/request",
            idempotencyKey = idempotencyKey,
            attested = true,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listRideHistory(page: PageRequest): Page<RideHistoryRow> =
        transport.apiGet(SERVICE, "listRideHistory", "$RIDES_PATH/history") { pageParameters(page) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getActivePassengerRide(passengerId: Ulid): RideDetail? =
        transport.apiGet(SERVICE, "getActivePassengerRide", "$RIDES_PATH/passenger/$passengerId/active")
            .decodeOrNull(transport.json)

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getActiveDriverRide(driverId: Ulid): RideDetail? =
        transport.apiGet(SERVICE, "getActiveDriverRide", "$RIDES_PATH/driver/$driverId/active")
            .decodeOrNull(transport.json)

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getRide(rideId: Ulid): RideDetail =
        transport.apiGet(SERVICE, "getRide", "$RIDES_PATH/$rideId").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getRideState(rideId: Ulid): RideStateSnapshot =
        transport.apiGet(SERVICE, "getRideState", "$RIDES_PATH/$rideId/state").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun acceptRideOffer(
        rideId: Ulid,
        driverId: Ulid,
        request: AcceptRideOfferRequest,
        idempotencyKey: String?,
    ): AcceptRideOfferResponse = transport.apiPost(
        service = SERVICE,
        operationId = "acceptRideOffer",
        path = "$RIDES_PATH/$rideId/offer/$driverId/accept",
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun declineRideOffer(
        rideId: Ulid,
        driverId: Ulid,
        request: DeclineRideOfferRequest,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "declineRideOffer",
        path = "$RIDES_PATH/$rideId/offer/$driverId/decline",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun markDriverArrived(
        rideId: Ulid,
        request: VersionedCommand,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "markDriverArrived",
        path = "$RIDES_PATH/$rideId/arrive",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun startRide(rideId: Ulid, request: StartRideRequest, idempotencyKey: String?): RideStateChange =
        transport.apiPost(
            service = SERVICE,
            operationId = "startRide",
            path = "$RIDES_PATH/$rideId/start",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun completeRide(
        rideId: Ulid,
        request: VersionedCommand,
        idempotencyKey: String?,
    ): CompleteRideResponse = transport.apiPost(
        service = SERVICE,
        operationId = "completeRide",
        path = "$RIDES_PATH/$rideId/complete",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun cancelRide(
        rideId: Ulid,
        request: CancelRideRequest,
        idempotencyKey: String?,
    ): CancelRideResponse = transport.apiPost(
        service = SERVICE,
        operationId = "cancelRide",
        path = "$RIDES_PATH/$rideId/cancel",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun disputeRide(rideId: Ulid, request: DisputeRideRequest, idempotencyKey: String?): TicketRef =
        transport.apiPost(
            service = SERVICE,
            operationId = "disputeRide",
            path = "$RIDES_PATH/$rideId/dispute",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun verifyPackagePickupOtp(
        rideId: Ulid,
        request: OtpAttempt,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "verifyPackagePickupOtp",
        path = "$RIDES_PATH/$rideId/package/pickup-otp",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun verifyPackageDeliveryOtp(
        rideId: Ulid,
        request: OtpAttempt,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "verifyPackageDeliveryOtp",
        path = "$RIDES_PATH/$rideId/package/delivery-otp",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Suppress("LongParameterList") // The interface's; see its KDoc.
    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun uploadPackageProofPhoto(
        rideId: Ulid,
        file: FileUpload,
        note: String?,
        lat: Double?,
        lng: Double?,
        idempotencyKey: String?,
    ): ProofArtifactResponse = transport.apiPost(
        service = SERVICE,
        operationId = "uploadPackageProofPhoto",
        path = "$RIDES_PATH/$rideId/package/proof-photo",
        idempotencyKey = idempotencyKey,
    ) {
        multipartBody {
            filePart("file", file)
            textPart("note", note)
            textPart("lat", lat?.toString())
            textPart("lng", lng?.toString())
        }
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun confirmCashOnDelivery(
        rideId: Ulid,
        request: ConfirmCashOnDeliveryRequest,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "confirmCashOnDelivery",
        path = "$RIDES_PATH/$rideId/cod-collected",
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun createLocationRequest(
        request: CreateLocationRequestRequest,
        idempotencyKey: String?,
    ): CreateLocationRequestResponse = transport.apiPost(
        service = SERVICE,
        operationId = "createLocationRequest",
        path = LOCATION_REQUESTS_PATH,
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getLocationRequest(requestId: Ulid): LocationRequest =
        transport.apiGet(SERVICE, "getLocationRequest", "$LOCATION_REQUESTS_PATH/$requestId").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun confirmLocationRequest(
        requestId: Ulid,
        request: GeoPointWithAccuracy,
        idempotencyKey: String?,
    ): LocationRequest = transport.apiPost(
        service = SERVICE,
        operationId = "confirmLocationRequest",
        path = "$LOCATION_REQUESTS_PATH/$requestId/confirm",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun declineLocationRequest(requestId: Ulid, idempotencyKey: String?): LocationRequest =
        transport.apiPost(
            service = SERVICE,
            operationId = "declineLocationRequest",
            path = "$LOCATION_REQUESTS_PATH/$requestId/decline",
            idempotencyKey = idempotencyKey,
        ).decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun markRideMatching(
        rideId: Ulid,
        request: MarkRideMatchingRequest,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "markRideMatching",
        path = "$INTERNAL_RIDES_PATH/$rideId/matching",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun placeRideOffer(
        rideId: Ulid,
        request: PlaceRideOfferRequest,
        idempotencyKey: String?,
    ): OfferPlaced = transport.apiPost(
        service = SERVICE,
        operationId = "placeRideOffer",
        path = "$INTERNAL_RIDES_PATH/$rideId/offer",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun expireRideOffer(
        rideId: Ulid,
        request: ExpireRideOfferRequest,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "expireRideOffer",
        path = "$INTERNAL_RIDES_PATH/$rideId/offer/expire",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun systemCancelRide(
        rideId: Ulid,
        request: SystemCancelRideRequest,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "systemCancelRide",
        path = "$INTERNAL_RIDES_PATH/$rideId/system-cancel",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun notifyPaymentSettled(
        rideId: Ulid,
        request: NotifyPaymentSettledRequest,
        idempotencyKey: String?,
    ): RideStateChange = transport.apiPost(
        service = SERVICE,
        operationId = "notifyPaymentSettled",
        path = "$INTERNAL_RIDES_PATH/$rideId/payment-settled",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getRideSagaState(rideId: Ulid): RideSagaState =
        transport.apiGet(SERVICE, "getRideSagaState", "$INTERNAL_RIDES_PATH/$rideId/saga-state").decode()

    private companion object {
        val SERVICE = ApiService.RIDE
        const val RIDES_PATH = "/v1/rides"
        const val LOCATION_REQUESTS_PATH = "/v1/location-requests"
        const val INTERNAL_RIDES_PATH = "/v1/internal/rides"
    }
}

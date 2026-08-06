package lk.mageride.passenger.ride

import lk.mageride.shared.data.api.comms.VoipApi
import lk.mageride.shared.data.api.fare.FareApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.api.safety.SafetyApi
import lk.mageride.shared.data.api.wallet.WalletApi
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.comms.CallOutcome
import lk.mageride.shared.data.models.comms.CalleeRole
import lk.mageride.shared.data.models.comms.RecordCallOutcomeRequest
import lk.mageride.shared.data.models.comms.StartCallRequest
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.fare.ClaimDriverQrRequest
import lk.mageride.shared.data.models.fare.InitiatePaymentRequest
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.fare.ScanDriverQrRequest
import lk.mageride.shared.data.models.ride.CancelRideRequest
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.RideCancelReason
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosRole
import lk.mageride.shared.data.models.safety.TriggerSosRequest
import lk.mageride.shared.data.models.safety.TripShareLink
import lk.mageride.shared.data.models.wallet.Wallet

/**
 * Cluster 4's seam — the ride, its money, and the two things a passenger reaches for mid-trip.
 *
 * Five services: **ride-svc** for the aggregate and the cancel, **fare-svc** for the whole D-10
 * payment machine including AL-47's attestation pair, **wallet-svc** for the balance SCR-PA-016
 * shows before offering the wallet rail, **safety-svc** for SOS and the share link, and
 * **comms-svc** for the call that AL-48 turned into a real number.
 *
 * **No rating operation is here, and that is not an omission.** See [RateDriverViewModel] — there
 * is no contract to call.
 */
@Suppress("TooManyFunctions") // One method per contract operation, across five services.
internal interface RideRepository {

    /** `GET /v1/rides/{rideId}` — the aggregate, including the driver once one is assigned. */
    suspend fun ride(rideId: Ulid): RideDetail

    /** `GET /v1/rides/{rideId}/state` — the cheap poll behind the SignalR event. */
    suspend fun rideState(rideId: Ulid): RideStateSnapshot

    /** `POST /v1/rides/{rideId}/cancel` — the response carries D-05's Rs 50, when one applies. */
    suspend fun cancel(rideId: Ulid, version: RideVersion, reason: RideCancelReason): CancelRideResponse

    /** `GET /v1/wallet/{userId}` — SCR-PA-016 needs the balance before it offers the wallet. */
    suspend fun wallet(userId: Ulid): Wallet

    /** `POST /v1/fare/pay` — starts D-10 on the chosen rail. */
    suspend fun pay(rideId: Ulid, method: PaymentMethod, tipMinor: Long? = null): PaymentInitiation

    /** `GET /v1/fare/pay/{paymentId}/status` — the poll SCR-PA-017 waits on. */
    suspend fun paymentStatus(paymentId: Ulid): PaymentStatus

    /** `POST /v1/fare/pay/{paymentId}/fallback-cash` — US-8.15, without losing the history. */
    suspend fun fallbackToCash(paymentId: Ulid): PaymentStatus

    /** `POST /v1/fare/pay/scan-driver-qr` — the decoded payload of the driver's own QR (AL-22). */
    suspend fun payByScanningDriverQr(rideId: Ulid, qrPayload: String): PaymentStatus

    /** `POST /v1/fare/pay/driver-qr/claim` — AL-47's *"I've paid"*. */
    suspend fun claimDriverQrPaid(rideId: Ulid, receiptArtifactId: Ulid? = null): PaymentStatus

    /** `POST /v1/sos` — D-33's parallel SMS fan-out. */
    suspend fun triggerSos(rideId: Ulid, lat: Double, lng: Double): SosDispatched

    /** `POST /v1/trips/{tripId}/share` — the wireframe's share-trip affordance. */
    suspend fun shareTrip(rideId: Ulid): TripShareLink

    /** `POST /v1/calls/start` — logs the tap; a VoIP call also gets a session back (AL-48). */
    suspend fun startCall(rideId: Ulid, type: CallType): StartCallResponse

    /**
     * `POST /v1/calls/{callId}/outcome` — how the call ended (Δ C055).
     *
     * The only way voip-svc learns about a call that never connected, and therefore the signal
     * AL-48's *"Call normally instead?"* hangs on: SCR-PA-028 reports [CallOutcome.VOIP_FAILED]
     * when it puts the prompt up, and a `direct_dial` row after one on the same ride is the
     * fallback being taken rather than a passenger who simply preferred to dial.
     */
    suspend fun reportCallOutcome(callId: Ulid, outcome: CallOutcome)
}

/** [RideRepository] over the generated clients. */
@Suppress("TooManyFunctions") // Mirrors the interface above, one for one.
internal class ApiRideRepository(
    private val rides: RideApi,
    private val fares: FareApi,
    private val wallets: WalletApi,
    private val safety: SafetyApi,
    private val comms: VoipApi,
) : RideRepository {

    override suspend fun ride(rideId: Ulid): RideDetail = rides.getRide(rideId)

    override suspend fun rideState(rideId: Ulid): RideStateSnapshot = rides.getRideState(rideId)

    override suspend fun cancel(rideId: Ulid, version: RideVersion, reason: RideCancelReason): CancelRideResponse =
        rides.cancelRide(rideId, CancelRideRequest(version = version, reason = reason))

    override suspend fun wallet(userId: Ulid): Wallet = wallets.getWallet(userId)

    override suspend fun pay(rideId: Ulid, method: PaymentMethod, tipMinor: Long?): PaymentInitiation =
        fares.initiatePayment(InitiatePaymentRequest(rideId = rideId, method = method, tipMinor = tipMinor))

    override suspend fun paymentStatus(paymentId: Ulid): PaymentStatus = fares.getPaymentStatus(paymentId)

    override suspend fun fallbackToCash(paymentId: Ulid): PaymentStatus = fares.fallbackToCash(paymentId)

    override suspend fun payByScanningDriverQr(rideId: Ulid, qrPayload: String): PaymentStatus =
        fares.payByScanningDriverQr(ScanDriverQrRequest(rideId = rideId, qrPayload = qrPayload))

    override suspend fun claimDriverQrPaid(rideId: Ulid, receiptArtifactId: Ulid?): PaymentStatus =
        fares.claimDriverQrPayment(ClaimDriverQrRequest(rideId = rideId, receiptArtifactId = receiptArtifactId))

    override suspend fun triggerSos(rideId: Ulid, lat: Double, lng: Double): SosDispatched =
        safety.triggerSos(TriggerSosRequest(rideId = rideId, lat = lat, lng = lng, role = SosRole.PASSENGER))

    override suspend fun shareTrip(rideId: Ulid): TripShareLink = safety.createTripShare(rideId)

    /**
     * Logs the call.
     *
     * **Best effort, and never a gate.** `comms.call_log` is a client-logged tap (AL-48) — the
     * dial itself is an `Intent`, and a passenger trying to reach their driver must not be stopped
     * by a logging call that timed out. The caller places the dial regardless of what this does.
     */
    override suspend fun startCall(rideId: Ulid, type: CallType): StartCallResponse = comms.startCall(
        StartCallRequest(rideId = rideId, calleeRole = CalleeRole.DRIVER, callType = type),
    )

    override suspend fun reportCallOutcome(callId: Ulid, outcome: CallOutcome) {
        comms.recordCallOutcome(callId, RecordCallOutcomeRequest(outcome))
    }
}

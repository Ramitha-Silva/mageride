package lk.mageride.driver.ride

import lk.mageride.shared.data.api.fare.FareApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.fare.ConfirmDriverQrRequest
import lk.mageride.shared.data.models.fare.DisputeDriverQrRequest
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.ride.CompleteRideResponse
import lk.mageride.shared.data.models.ride.OtpAttempt
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.data.models.ride.StartRideRequest
import lk.mageride.shared.data.models.ride.VersionedCommand
import lk.mageride.shared.data.models.support.TicketRef

/**
 * ride-svc's write surface as SCR-DA-015 uses it, plus AL-47's settlement pair.
 *
 * **Every mutation carries the version the client last saw** (R-14). A stale one is
 * `409 version-conflict` rather than a silent overwrite, and the answer to one is to re-read and
 * re-decide — never to bump the number and retry. That is why each call here takes a version
 * instead of reading one for itself: the version that must be sent is the one the *screen* was
 * showing when the driver tapped, not one fetched a moment later.
 *
 * **ride-svc is the sole writer (R-01).** Nothing in this file may reach trip-state-svc, and
 * nothing in [lk.mageride.driver.home.JourneyRepository] may reach here.
 */
internal class ActiveRideRepository(private val ride: RideApi, private val fare: FareApi) {

    /**
     * `GET /v1/rides/driver/{driverId}/active` — the ride already in hand, or `null`.
     *
     * The cold-start restore SCR-DA-001 promises (*"approved+perms → Dashboard (resumes active
     * ride/session)"*). A driver whose app was killed mid-ride has to come back to the ride, not to
     * a standby map that says they are idle.
     */
    suspend fun active(driverId: Ulid): RideDetail? = ride.getActiveDriverRide(driverId)

    /** `GET /v1/rides/{rideId}` — the whole aggregate the sheet is drawn from. */
    suspend fun detail(rideId: Ulid): RideDetail = ride.getRide(rideId)

    /**
     * `GET /v1/rides/{rideId}/state` — state and version, without the payload.
     *
     * D3' §3.1's live path is the SignalR hub and this is its documented fallback (D6' §8.3).
     * **`:shared` carries the hub's contract but no client**, so on this surface the fallback is
     * the only path there is — see the C070 handoff.
     */
    suspend fun snapshot(rideId: Ulid): RideStateSnapshot = ride.getRideState(rideId)

    /** `POST /v1/rides/{rideId}/arrive` — `Accepted → DriverArrived`. */
    suspend fun arrive(rideId: Ulid, version: RideVersion): RideStateChange =
        ride.markDriverArrived(rideId, VersionedCommand(version))

    /**
     * Begins the ride — **two different operations, chosen by kind**.
     *
     * A passenger or proxy ride quotes the rider's start OTP to `POST /v1/rides/{rideId}/start`; a
     * package ride has no rider to hold one, so the sender's pickup OTP goes to
     * `POST …/package/pickup-otp` instead (P-07). Both are `RideCommand`'s `START` /
     * `VERIFY_PICKUP_OTP` and both land on `InProgress`; picking between them here is what keeps
     * the screen from having to know.
     *
     * `423 otp-locked` once the five attempts are spent, on either route.
     */
    suspend fun start(rideId: Ulid, kind: RideKind, version: RideVersion, otp: String): RideStateChange =
        if (kind == RideKind.PACKAGE) {
            ride.verifyPackagePickupOtp(rideId, OtpAttempt(otp))
        } else {
            ride.startRide(rideId, StartRideRequest(version = version, otp = otp))
        }

    /**
     * `POST /v1/rides/{rideId}/complete` — `InProgress → Completed → PaymentPending`.
     *
     * The response carries the final fare, which fare-svc computes from the **Kalman-filtered**
     * distance (E-04). The driver's earning does not post here: R-05 holds it until payment reaches
     * a terminal state, which for a driver-QR ride is the confirm below.
     */
    suspend fun complete(rideId: Ulid, version: RideVersion): CompleteRideResponse =
        ride.completeRide(rideId, VersionedCommand(version))

    /**
     * `POST /v1/fare/pay/driver-qr/confirm` — **AL-47's attestation** (US-26.1).
     *
     * The money moved bank-to-bank into the driver's own LankaQR account and no gateway can confirm
     * it, so the driver's word is the settlement: this moves the payment to
     * [PaymentState.DriverConfirmedQR], which is **terminal**, settles like cash, and is what posts
     * the earning (R-05). Valid with or without a prior passenger claim — the driver's bank app is
     * the only party that actually saw the money.
     */
    suspend fun confirmQrPayment(rideId: Ulid): PaymentStatus =
        fare.confirmDriverQrPayment(ConfirmDriverQrRequest(rideId = rideId))

    /**
     * `POST /v1/fare/pay/driver-qr/dispute` — the driver says it did not arrive.
     *
     * Opens a ticket routed Support → Finance and moves **no money**: the platform took no
     * commission on this path and holds none of it, so there is nothing to reverse.
     */
    suspend fun disputeQrPayment(rideId: Ulid, note: String?): TicketRef =
        fare.disputeDriverQrPayment(DisputeDriverQrRequest(rideId = rideId, note = note))
}

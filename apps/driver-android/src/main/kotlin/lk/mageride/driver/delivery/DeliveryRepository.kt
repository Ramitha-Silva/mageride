package lk.mageride.driver.delivery

import lk.mageride.shared.data.api.FileUpload
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.ride.CancelRideRequest
import lk.mageride.shared.data.models.ride.CancelRideResponse
import lk.mageride.shared.data.models.ride.OtpAttempt
import lk.mageride.shared.data.models.ride.ProofArtifactResponse
import lk.mageride.shared.data.models.ride.RideCancelReason
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.data.models.ride.RideStateSnapshot

/**
 * ride-svc's **package** surface, as SCR-DA-016a/b/c uses it (P-06/P-07/P-10, AL-33).
 *
 * A delivery is the same aggregate as a ride (R-01 keeps one), so this sits beside
 * [lk.mageride.driver.ride.ActiveRideRepository] rather than inside it: the two screens drive
 * *different commands over the same rows*, and one class owning both would be one class owning two
 * state machines — the passenger arrive/start/complete triple, and the two OTP handoffs that
 * replace it. The read pair is repeated because a delivery has to be able to read a ride without
 * depending on the passenger screen's repository.
 *
 * **The COD confirmation is deliberately absent.** AL-33 replaced *"Cash received (COD)"* with
 * *"Delivery completed"* and decoupled the cash from the handover, so nothing on these three sheets
 * calls `POST /v1/rides/{rideId}/cod-collected`; uncollected cash is reconciled separately and
 * becomes `Disputed` after 24 hours (P-14). See the C071 handoff — that endpoint now has no caller
 * on this surface.
 */
internal class DeliveryRepository(private val ride: RideApi) {

    /** `GET /v1/rides/{rideId}` — the whole aggregate the three sheets are drawn from. */
    suspend fun detail(rideId: Ulid): RideDetail = ride.getRide(rideId)

    /**
     * `GET /v1/rides/{rideId}/state` — state and version, without the payload.
     *
     * The same documented fallback SCR-DA-015 runs on: D3' §3.1's live path is the SignalR hub and
     * `:shared` carries its contract and no client, so a screen waiting on a transition polls.
     */
    suspend fun snapshot(rideId: Ulid): RideStateSnapshot = ride.getRideState(rideId)

    /**
     * `POST /v1/rides/{rideId}/package/pickup-otp` — the **sender's** code releases the parcel.
     *
     * `Accepted | DriverArrived → InProgress`, emitting `package.picked_up` (P-07). The same event
     * is what sends the recipient *their* code — AL-21 branches on whether they have an account:
     * FCM deep link if they do, an SMS carrying a `safety.trip_share_tokens` link if they do not.
     * Five attempts, then `423 otp-locked` and the handoff is with support.
     */
    suspend fun verifyPickup(rideId: Ulid, otp: String): RideStateChange =
        ride.verifyPackagePickupOtp(rideId, OtpAttempt(otp))

    /**
     * `POST /v1/rides/{rideId}/package/delivery-otp` — the **recipient's** code accepts it.
     *
     * `InProgress → Completed → PaymentPending`, emitting `package.delivered`. Same five-attempt
     * budget, against the code the recipient was sent at pickup.
     */
    suspend fun verifyDelivery(rideId: Ulid, otp: String): RideStateChange =
        ride.verifyPackageDeliveryOtp(rideId, OtpAttempt(otp))

    /**
     * `POST /v1/rides/{rideId}/package/proof-photo` — P-10's fallback when nobody is there.
     *
     * **This completes the delivery too** (Δ C037): the photograph is the delivery OTP's
     * alternative, not a filing beside it, so it is legal only from `InProgress` and the response
     * says where the ride landed. The captured position rides along as `captured_geo`, which is what
     * makes the picture evidence of a delivery *at a place*.
     */
    suspend fun deliverWithProof(rideId: Ulid, proof: ProofUpload): ProofArtifactResponse =
        ride.uploadPackageProofPhoto(
            rideId = rideId,
            file = FileUpload(
                fileName = proof.image.fileName,
                bytes = proof.image.bytes,
                contentType = proof.image.mimeType,
            ),
            lat = proof.at?.lat,
            lng = proof.at?.lng,
        )

    /**
     * `POST /v1/rides/{rideId}/cancel` — the driver puts the delivery down (SCR-DA-016a's Cancel).
     *
     * **What the client can do and what AL-33 asks for are not the same thing**, and this is where
     * the difference lives. AL-33 says Cancel *"releases the offer back to dispatch → next eligible
     * driver"*; the only operation that exists is the ordinary driver cancel, and §11.12's matrix
     * makes `(Accepted, DriverCancel)` terminal `CancelledByDriver` — dispatch-svc retires the ride
     * on `ride.cancelled` and returns the driver to the pool rather than re-offering it. So this
     * releases the delivery from *this* driver, which is the half a client owns; the re-dispatch is
     * ride-svc's and does not happen today. Recorded as a spec gap in the C071 handoff.
     *
     * [RideCancelReason.OTHER] because none of the four names it: the enum is the *rider's* reason
     * list, and "released back to dispatch" is not among them.
     */
    suspend fun release(rideId: Ulid, version: RideVersion): CancelRideResponse =
        ride.cancelRide(rideId, CancelRideRequest(version = version, reason = RideCancelReason.OTHER))
}

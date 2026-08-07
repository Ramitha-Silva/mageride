import Foundation
import MageRideShared

/// ride-svc's **package** surface, as SCR-DI-016a/b/c uses it (P-06/P-07/P-10, AL-33).
///
/// A delivery is the same aggregate as a ride (R-01 keeps one), so this sits beside
/// ``ActiveRideRepository`` rather than inside it: the two screens drive *different commands over the
/// same rows*, and one protocol owning both would be one protocol owning two state machines — the
/// passenger arrive/start/complete triple, and the two OTP handoffs that replace it. The read pair is
/// repeated because a delivery has to be able to read a ride without depending on the passenger
/// screen's repository.
///
/// **The COD confirmation is deliberately absent.** AL-33 replaced *"Cash received (COD)"* with
/// *"Delivery completed"* and decoupled the cash from the handover, so nothing on these three sheets
/// calls `POST /v1/rides/{rideId}/cod-collected`; uncollected cash is reconciled separately and becomes
/// `Disputed` after 24 hours (P-14). See the C071 handoff — that endpoint has no caller on this surface
/// on either platform.
///
/// **Every mutation carries the version the screen was showing** (R-14), which is why ``release`` takes
/// one instead of reading it for itself.
protocol DeliveryRepository: AnyObject {

    /// `GET /v1/rides/{rideId}` — the whole aggregate the three sheets are drawn from.
    func detail(rideId: String) async throws -> RideDetail

    /// `GET /v1/rides/{rideId}/state` — state and version, without the payload.
    ///
    /// The same documented fallback SCR-DI-015 runs on: D3' §3.1's live path is the SignalR hub and
    /// `:shared` carries its contract and no client, so a screen waiting on a transition polls.
    func snapshot(rideId: String) async throws -> RideStateSnapshot

    /// `POST /v1/rides/{rideId}/package/pickup-otp` — the **sender's** code releases the parcel.
    func verifyPickup(rideId: String, otp: String) async throws -> RideStateSnapshot

    /// `POST /v1/rides/{rideId}/package/delivery-otp` — the **recipient's** code accepts it.
    func verifyDelivery(rideId: String, otp: String) async throws -> RideStateSnapshot

    /// `POST /v1/rides/{rideId}/package/proof-photo` — P-10's fallback when nobody is there.
    ///
    /// The artifact response rather than a snapshot, because Δ C037 makes `state` and `version`
    /// **optional** on it: a server answering the older shape leaves both absent, and only the caller
    /// knows the version the screen was holding to fall back to.
    func deliverWithProof(rideId: String, proof: ProofUpload) async throws -> ProofArtifactResponse

    /// `POST /v1/rides/{rideId}/cancel` — the driver puts the delivery down (SCR-DI-016a's Cancel).
    func release(rideId: String, version: Int32) async throws
}

/// ``DeliveryRepository`` over ride-svc.
final class ApiDeliveryRepository: DeliveryRepository {

    private let ride: RideApi

    init(ride: RideApi) {
        self.ride = ride
    }

    func detail(rideId: String) async throws -> RideDetail {
        try await ride.getRide(rideId: rideId)
    }

    func snapshot(rideId: String) async throws -> RideStateSnapshot {
        try await ride.getRideState(rideId: rideId)
    }

    /// `Accepted | DriverArrived → InProgress`, emitting `package.picked_up` (P-07).
    ///
    /// The same event is what sends the recipient *their* code — AL-21 branches on whether they have an
    /// account: an FCM deep link if they do, an SMS carrying a `safety.trip_share_tokens` link if they
    /// do not. Five attempts, then `423 otp-locked` and the handoff is with support.
    func verifyPickup(rideId: String, otp: String) async throws -> RideStateSnapshot {
        let moved = try await ride.verifyPackagePickupOtp(
            rideId: rideId,
            request: OtpAttempt(otp: otp),
            idempotencyKey: nil
        )
        return RideStateSnapshot(state: moved.state, version: moved.version, offerExpiresAt: nil)
    }

    /// `InProgress → Completed → PaymentPending`, emitting `package.delivered`. Same five-attempt
    /// budget, against the code the recipient was sent at pickup.
    func verifyDelivery(rideId: String, otp: String) async throws -> RideStateSnapshot {
        let moved = try await ride.verifyPackageDeliveryOtp(
            rideId: rideId,
            request: OtpAttempt(otp: otp),
            idempotencyKey: nil
        )
        return RideStateSnapshot(state: moved.state, version: moved.version, offerExpiresAt: nil)
    }

    /// **This completes the delivery too** (Δ C037): the photograph is the delivery OTP's alternative,
    /// not a filing beside it, so it is legal only from `InProgress` and the response says where the
    /// ride landed. The captured position rides along as `captured_geo`, which is what makes the picture
    /// evidence of a delivery *at a place*.
    ///
    /// `asDocument()` is reached for its `file` alone: a proof photo goes to `rides.proof_artifacts` and
    /// the contract declares no `…CapturedVia` part beside it, so AL-43's provenance is dropped at the
    /// upload rather than filed against a Verification-Officer queue it has nothing to do with.
    func deliverWithProof(rideId: String, proof: ProofUpload) async throws -> ProofArtifactResponse {
        try await ride.uploadPackageProofPhoto(
            rideId: rideId,
            file: proof.image.asDocument().file,
            note: nil,
            lat: proof.at.map { KotlinDouble(value: $0.lat) },
            lng: proof.at.map { KotlinDouble(value: $0.lng) },
            idempotencyKey: nil
        )
    }

    /// **What the client can do and what AL-33 asks for are not the same thing**, and this is where the
    /// difference lives. AL-33 says Cancel *"releases the offer back to dispatch → next eligible
    /// driver"*; the only operation that exists is the ordinary driver cancel, and §11.12's matrix makes
    /// `(Accepted, DriverCancel)` terminal `CancelledByDriver` — dispatch-svc retires the ride on
    /// `ride.cancelled` and returns the driver to the pool rather than re-offering it. So this releases
    /// the delivery from *this* driver, which is the half a client owns; the re-dispatch is ride-svc's
    /// and does not happen today. Recorded as a spec gap in the C071 handoff and carried forward here.
    ///
    /// `RideCancelReason.other` because none of the four names it: the enum is the *rider's* reason list,
    /// and "released back to dispatch" is not among them.
    func release(rideId: String, version: Int32) async throws {
        _ = try await ride.cancelRide(
            rideId: rideId,
            request: CancelRideRequest(version: version, reason: RideCancelReason.other),
            idempotencyKey: nil
        )
    }
}

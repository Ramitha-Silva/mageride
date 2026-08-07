import Foundation
import MageRideShared

/// ride-svc's write surface as SCR-DI-015 uses it, plus AL-47's settlement pair.
///
/// **Every mutation carries the version the client last saw** (R-14). A stale one is
/// `409 version-conflict` rather than a silent overwrite, and the answer to one is to re-read and
/// re-decide — never to bump the number and retry. That is why each call here takes a version instead
/// of reading one for itself: the version that must be sent is the one the *screen* was showing when
/// the driver tapped, not one fetched a moment later.
///
/// **ride-svc is the sole writer (R-01).** Nothing behind this protocol may reach trip-state-svc, and
/// nothing behind ``JourneyRepository`` may reach here.
protocol ActiveRideRepository: AnyObject {

    /// `GET /v1/rides/driver/{driverId}/active` — the ride already in hand, or `nil`.
    ///
    /// The cold-start restore SCR-DI-001 promises (*"approved+perms → Dashboard (resumes active
    /// ride/session)"*). A driver whose app was killed mid-ride has to come back to the ride, not to
    /// a standby map that says they are idle.
    func active(driverId: String) async throws -> RideDetail?

    /// `GET /v1/rides/{rideId}` — the whole aggregate the sheet is drawn from.
    func detail(rideId: String) async throws -> RideDetail

    /// `GET /v1/rides/{rideId}/state` — state and version, without the payload.
    func snapshot(rideId: String) async throws -> RideStateSnapshot

    /// `POST /v1/rides/{rideId}/arrive` — `Accepted → DriverArrived`.
    func arrive(rideId: String, version: Int32) async throws -> RideStateSnapshot

    /// Begins the ride — **two different operations, chosen by kind**.
    func start(rideId: String, kind: RideKind, version: Int32, otp: String) async throws -> RideStateSnapshot

    /// `POST /v1/rides/{rideId}/complete` — `InProgress → Completed → PaymentPending`.
    func complete(rideId: String, version: Int32) async throws -> RideStateSnapshot

    /// `POST /v1/fare/pay/driver-qr/confirm` — **AL-47's attestation** (US-26.1).
    func confirmQrPayment(rideId: String) async throws -> PaymentState

    /// `POST /v1/fare/pay/driver-qr/dispute` — the driver says it did not arrive.
    func disputeQrPayment(rideId: String, note: String?) async throws
}

/// ``ActiveRideRepository`` over ride-svc and fare-svc.
final class ApiActiveRideRepository: ActiveRideRepository {

    private let ride: RideApi
    private let fare: FareApi

    init(ride: RideApi, fare: FareApi) {
        self.ride = ride
        self.fare = fare
    }

    func active(driverId: String) async throws -> RideDetail? {
        try await ride.getActiveDriverRide(driverId: driverId)
    }

    func detail(rideId: String) async throws -> RideDetail {
        try await ride.getRide(rideId: rideId)
    }

    /// D3' §3.1's live path is the SignalR hub and this is its documented fallback (D6' §8.3).
    /// **`:shared` carries the hub's contract but no client**, so on this surface the fallback is the
    /// only path there is — see the C070 handoff, which says the same about the Android twin.
    func snapshot(rideId: String) async throws -> RideStateSnapshot {
        try await ride.getRideState(rideId: rideId)
    }

    func arrive(rideId: String, version: Int32) async throws -> RideStateSnapshot {
        let moved = try await ride.markDriverArrived(
            rideId: rideId,
            request: VersionedCommand(version: version),
            idempotencyKey: nil
        )
        return RideStateSnapshot(state: moved.state, version: moved.version, offerExpiresAt: nil)
    }

    /// A passenger or proxy ride quotes the rider's start OTP to `POST /v1/rides/{rideId}/start`; a
    /// package ride has no rider to hold one, so the sender's pickup OTP goes to
    /// `POST …/package/pickup-otp` instead (P-07). Both land on `InProgress`; picking between them
    /// here is what keeps the screen from having to know.
    ///
    /// `423 otp-locked` once the five attempts are spent, on either route.
    func start(rideId: String, kind: RideKind, version: Int32, otp: String) async throws -> RideStateSnapshot {
        let moved: RideStateChange
        if kind == RideKind.package {
            moved = try await ride.verifyPackagePickupOtp(
                rideId: rideId,
                request: OtpAttempt(otp: otp),
                idempotencyKey: nil
            )
        } else {
            moved = try await ride.startRide(
                rideId: rideId,
                request: StartRideRequest(version: version, otp: otp),
                idempotencyKey: nil
            )
        }
        return RideStateSnapshot(state: moved.state, version: moved.version, offerExpiresAt: nil)
    }

    /// The response carries the final fare, which fare-svc computes from the **Kalman-filtered**
    /// distance (E-04). The driver's earning does not post here: R-05 holds it until payment reaches a
    /// terminal state, which for a driver-QR ride is the confirm below.
    func complete(rideId: String, version: Int32) async throws -> RideStateSnapshot {
        let moved = try await ride.completeRide(
            rideId: rideId,
            request: VersionedCommand(version: version),
            idempotencyKey: nil
        )
        return RideStateSnapshot(state: moved.state, version: moved.version, offerExpiresAt: nil)
    }

    /// The money moved bank-to-bank into the driver's own LankaQR account and no gateway can confirm
    /// it, so the driver's word is the settlement: this moves the payment to
    /// `PaymentState.DriverConfirmedQR`, which is **terminal**, settles like cash, and is what posts
    /// the earning (R-05). Valid with or without a prior passenger claim — the driver's bank app is
    /// the only party that actually saw the money.
    func confirmQrPayment(rideId: String) async throws -> PaymentState {
        try await fare.confirmDriverQrPayment(
            request: ConfirmDriverQrRequest(rideId: rideId),
            idempotencyKey: nil
        ).state
    }

    /// Opens a ticket routed Support → Finance and moves **no money**: the platform took no commission
    /// on this path and holds none of it, so there is nothing to reverse.
    func disputeQrPayment(rideId: String, note: String?) async throws {
        _ = try await fare.disputeDriverQrPayment(
            request: DisputeDriverQrRequest(rideId: rideId, note: note),
            idempotencyKey: nil
        )
    }
}

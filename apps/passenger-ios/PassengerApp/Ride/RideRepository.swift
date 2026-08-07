import Foundation
import MageRideShared

/// Cluster 4's seam — the ride, its money, and the one thing a passenger reaches for mid-trip.
///
/// Four services behind one door: **ride-svc** for the aggregate and the cancel, **fare-svc** for the
/// whole D-10 payment machine including AL-47's attestation pair, **wallet-svc** for the balance
/// SCR-PI-016 shows before it offers the wallet rail, and **comms-svc** for the call AL-48 turned back
/// into a real number. Four Kotlin clients resolved into five models would be twenty initialiser
/// parameters and no single place to see what a ride touches.
///
/// **safety-svc is deliberately absent, and that is C102's boundary rather than an omission.**
/// `POST /v1/sos` and `POST /v1/trips/{id}/share` belong to SCR-PI-029, whose whole screen is the
/// confirm, the countdown, the contact list and D-34's link — and *"`POST /v1/sos` having one
/// caller"* is what stops one emergency arriving on the operator's live feed as two events. C084 made
/// exactly that move on the Android side after C080 had put the call here; this side starts where
/// that finished. SCR-PI-015's `⛨ SOS` **navigates**.
///
/// **No rating operation is here either, and that is not an omission.** See ``RideRatings`` — there
/// is no contract to call.
///
/// A Swift protocol rather than the Kotlin clients themselves, for the rule
/// `apps/passenger-ios/CLAUDE.md` states: all four are Kotlin interfaces with `suspend` methods and
/// Swift can stand in for none of them. **No caching and no state** — the screens' state is theirs and
/// this is a translation layer, which is what makes it substitutable in a test.
protocol RideRepository: AnyObject {

    /// `GET /v1/rides/{rideId}` — the aggregate, including the driver once one is assigned.
    func ride(rideId: String) async throws -> RideDetail

    /// `GET /v1/rides/{rideId}/state` — the cheap poll behind the SignalR event.
    func rideState(rideId: String) async throws -> RideStateSnapshot

    /// `POST /v1/rides/{rideId}/cancel` — the response carries D-05's Rs 50, when one applies.
    func cancel(rideId: String, version: Int32, reason: RideCancelReason) async throws -> CancelRideResponse

    /// `GET /v1/wallet/{userId}` — SCR-PI-016 needs the balance before it offers the wallet rail.
    func wallet(userId: String) async throws -> Wallet

    /// `POST /v1/fare/pay` — starts D-10 on the chosen rail.
    func pay(rideId: String, method: PaymentMethod) async throws -> PaymentInitiation

    /// `GET /v1/fare/pay/{paymentId}/status` — the poll SCR-PI-017 waits on.
    func paymentStatus(paymentId: String) async throws -> PaymentStatus

    /// `POST /v1/fare/pay/{paymentId}/fallback-cash` — US-8.15, without losing the history.
    func fallbackToCash(paymentId: String) async throws -> PaymentStatus

    /// `POST /v1/fare/pay/scan-driver-qr` — the decoded payload of the driver's own QR (AL-22).
    func payByScanningDriverQr(rideId: String, qrPayload: String) async throws -> PaymentStatus

    /// `POST /v1/fare/pay/driver-qr/claim` — AL-47's *"I've paid"*.
    func claimDriverQrPaid(rideId: String, receiptArtifactId: String?) async throws -> PaymentStatus

    /// `POST /v1/calls/start` — logs the tap; a VoIP call also gets a session back (AL-48).
    func startCall(rideId: String, type: CallType) async throws -> StartCallResponse
}

/// ``RideRepository`` over the generated clients.
///
/// The whole of what it adds is what a Swift call site cannot leave out: **every parameter is
/// passed**, because a Kotlin default argument does not survive the Objective-C export. Every
/// `idempotencyKey` here is `nil` on purpose — R-18's dedupe key belongs to an operation a *retry*
/// must not repeat, and none of these is one: a cancel carries the version (R-03's optimistic
/// concurrency does the same job), and every fare operation is keyed server-side on the ride.
///
/// **Nothing here constructs a boxed Kotlin primitive.** `InitiatePaymentRequest.tipMinor` is a
/// `Long?` and is passed `nil` — SCR-PI-018 draws no tip row, on the wireframe's own instruction —
/// so `KotlinLong(…)`, which this repository has two spellings for and only one right answer to
/// (the C096 finding), is never written.
final class ApiRideRepository: RideRepository {

    private let rides: RideApi
    private let fares: FareApi
    private let wallets: WalletApi
    private let comms: VoipApi

    init(rides: RideApi, fares: FareApi, wallets: WalletApi, comms: VoipApi) {
        self.rides = rides
        self.fares = fares
        self.wallets = wallets
        self.comms = comms
    }

    func ride(rideId: String) async throws -> RideDetail {
        try await rides.getRide(rideId: rideId)
    }

    func rideState(rideId: String) async throws -> RideStateSnapshot {
        try await rides.getRideState(rideId: rideId)
    }

    func cancel(rideId: String, version: Int32, reason: RideCancelReason) async throws -> CancelRideResponse {
        try await rides.cancelRide(
            rideId: rideId,
            request: CancelRideRequest(version: version, reason: reason),
            idempotencyKey: nil
        )
    }

    func wallet(userId: String) async throws -> Wallet {
        try await wallets.getWallet(userId: userId)
    }

    func pay(rideId: String, method: PaymentMethod) async throws -> PaymentInitiation {
        try await fares.initiatePayment(
            request: InitiatePaymentRequest(rideId: rideId, method: method, tipMinor: nil),
            idempotencyKey: nil
        )
    }

    func paymentStatus(paymentId: String) async throws -> PaymentStatus {
        try await fares.getPaymentStatus(paymentId: paymentId)
    }

    func fallbackToCash(paymentId: String) async throws -> PaymentStatus {
        try await fares.fallbackToCash(paymentId: paymentId, idempotencyKey: nil)
    }

    func payByScanningDriverQr(rideId: String, qrPayload: String) async throws -> PaymentStatus {
        try await fares.payByScanningDriverQr(
            request: ScanDriverQrRequest(rideId: rideId, qrPayload: qrPayload),
            idempotencyKey: nil
        )
    }

    func claimDriverQrPaid(rideId: String, receiptArtifactId: String?) async throws -> PaymentStatus {
        try await fares.claimDriverQrPayment(
            request: ClaimDriverQrRequest(rideId: rideId, receiptArtifactId: receiptArtifactId),
            idempotencyKey: nil
        )
    }

    /// Logs the call.
    ///
    /// **Best effort, and never a gate.** `comms.call_log` is a client-logged tap (AL-48) — the dial
    /// itself is a `tel:` URL — and a passenger trying to reach their driver must not be stopped by a
    /// logging call that timed out. The caller places the dial regardless of what this does.
    func startCall(rideId: String, type: CallType) async throws -> StartCallResponse {
        try await comms.startCall(
            request: StartCallRequest(rideId: rideId, calleeRole: CalleeRole.driver, callType: type),
            idempotencyKey: nil
        )
    }
}

extension RideDetail {

    /// The fare, when one has been computed. The two money screens want only the number.
    var amountMinorOrNil: Int64? { fare?.amountMinor }
}

extension RideState {

    /// Whether the passenger's active-ride screen has nothing left to watch.
    ///
    /// **Payment states are deliberately not terminal here**: `Completed` and `PaymentPending` are
    /// exactly when SCR-PI-016 and SCR-PI-017 take over, and the screen has to *see* the transition
    /// to hand off. What ends the watch is a ride whose money and whose journey are both done.
    var isTerminalForPassenger: Bool {
        switch self {
        case RideState.paid,
             RideState.cashSettled,
             RideState.cashOnDeliveryCollected,
             RideState.cancelledByRiderBeforeAccept,
             RideState.cancelledByRiderAfterAccept,
             RideState.cancelledByDriver,
             RideState.expiredNoDriver,
             RideState.noShowRider,
             RideState.noShowDriver:
            return true
        default:
            return false
        }
    }

    /// Whether the fare has been dealt with — SCR-PI-018's receipt versus its *"Pay now"* CTA.
    ///
    /// `CashSettled` counts: the driver took the money in the vehicle, which is a settled ride even
    /// though nothing moved through the platform. `PaymentPending` does not, which is what puts the
    /// CTA on the receipt.
    var isSettled: Bool {
        self == RideState.paid || self == RideState.cashSettled || self == RideState.cashOnDeliveryCollected
    }
}

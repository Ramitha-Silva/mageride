import Foundation
import MageRideShared

@testable import DriverApp

/// The fixtures and fakes SCR-DI-016a/b/c's model is driven by.
///
/// Same rule as ``DashboardTestKit``: every seam is a **Swift protocol**, because the Kotlin types
/// behind them are classes Swift cannot stand in for. The DTOs underneath are the real shared ones, so a
/// contract change fails these tests rather than a driver's phone.

let testSenderPhone = "+94771234567"
let testRecipientPhone = "+94777120345"

/// A package ride as `GET /v1/rides/{rideId}` returns one.
///
/// Δ C037's three fields are all populated, because AL-33's sheets put a call button beside **both**
/// parties and a name under one of them.
func packageRide(
    state: RideState = RideState.accepted,
    version: Int32 = 3,
    paymentMethod: RidePaymentMethod = RidePaymentMethod.cod,
    senderPhone: String? = testSenderPhone,
    recipientPhone: String? = testRecipientPhone,
    counterpartyPhone: String? = testRecipientPhone,
    recipientName: String? = "Sunethra",
    packageSize: PackageSize = PackageSize.m
) -> RideDetail {
    RideDetail(
        rideId: testRideId,
        kind: RideKind.package,
        state: state,
        version: version,
        bookerId: nil,
        riderId: nil,
        riderName: nil,
        pickup: Place(lat: testHere.lat, lng: testHere.lng, address: "Galle Face"),
        dropoff: Place(lat: testThere.lat, lng: testThere.lng, address: "Nugegoda"),
        vehicleType: RideVehicleType.miniTruck,
        paymentMethod: paymentMethod,
        scheduledAt: nil,
        offerExpiresAt: nil,
        driver: nil,
        counterpartyPhone: counterpartyPhone,
        fare: FareEstimate(amountMinor: 48_000, currency: Currency.lkr, surchargeMinor: nil),
        packageSize: packageSize,
        packageDescription: nil,
        packageStatus: nil,
        recipientName: recipientName,
        senderPhone: senderPhone,
        recipientPhone: recipientPhone,
        createdAt: IosInstantKt.timestampFromEpochMillis(millis: 0)
    )
}

/// A photograph SCR-DI-005 would hand back. Three bytes, because none of them is ever decoded.
func testProofImage(_ fileName: String = "delivery-proof.jpg") -> CapturedImage {
    CapturedImage(fileName: fileName, data: Data([0x01, 0x02, 0x03]))
}

/// `423 otp-locked` — the one failure ``apiFailure(code:status:)`` cannot build, because that helper
/// always wraps a `MageRideError.Conflict` and P-07's lockout has to arrive as `MageRideError.Locked`
/// for the handoff to treat the *server's* count as authoritative.
func lockedFailure() -> NSError {
    let problem = ProblemDetails(
        type: "https://mageride.lk/errors/otp-locked",
        title: "otp-locked",
        status: 423,
        detail: nil,
        instance: nil,
        traceId: nil,
        errors: nil,
        updateUrl: nil,
        latestVersion: nil,
        isMandatory: nil
    )
    return NSError(domain: "KotlinException", code: 0, userInfo: ["KotlinException": MageRideErrorLocked(problem: problem)])
}

/// `400 invalid-otp` — a rejected code, which spends one of the five.
func invalidOtpFailure() -> NSError {
    let problem = ProblemDetails(
        type: "https://mageride.lk/errors/invalid-otp",
        title: "invalid-otp",
        status: 400,
        detail: nil,
        instance: nil,
        traceId: nil,
        errors: nil,
        updateUrl: nil,
        latestVersion: nil,
        isMandatory: nil
    )
    return NSError(
        domain: "KotlinException",
        code: 0,
        userInfo: ["KotlinException": MageRideErrorBadRequest(problem: problem)]
    )
}

/// ``DeliveryRepository`` with no gateway.
///
/// Every call is recorded rather than only counted: half of what AL-33 says is *which* request went out
/// — the pickup gate and the delivery gate are different endpoints, the photograph is a third, and
/// `cod-collected` must be none of them — and a counter cannot tell those apart.
final class FakeDeliveryRepository: DeliveryRepository {

    var detailToReturn = packageRide()
    var snapshotToReturn: RideStateSnapshot?
    var nextMove: RideStateSnapshot?
    var proofResponse = ProofArtifactResponse(
        artifactId: "01JARTIFACT0000000000000001",
        state: RideState.completed,
        version: KotlinInt(value: 5)
    )
    var nextFailure: Error?

    private(set) var pickupOtps: [String] = []
    private(set) var deliveryOtps: [String] = []
    private(set) var proofsUploaded: [ProofUpload] = []
    private(set) var released: [Int32] = []

    func detail(rideId: String) async throws -> RideDetail {
        try throwIfProgrammed()
        return detailToReturn
    }

    func snapshot(rideId: String) async throws -> RideStateSnapshot {
        guard let snapshotToReturn else { throw CancellationError() }
        return snapshotToReturn
    }

    func verifyPickup(rideId: String, otp: String) async throws -> RideStateSnapshot {
        pickupOtps.append(otp)
        try throwIfProgrammed()
        return nextMove ?? moved(RideState.inProgress, 4)
    }

    func verifyDelivery(rideId: String, otp: String) async throws -> RideStateSnapshot {
        deliveryOtps.append(otp)
        try throwIfProgrammed()
        return nextMove ?? moved(RideState.completed, 5)
    }

    func deliverWithProof(rideId: String, proof: ProofUpload) async throws -> ProofArtifactResponse {
        proofsUploaded.append(proof)
        try throwIfProgrammed()
        return proofResponse
    }

    func release(rideId: String, version: Int32) async throws {
        released.append(version)
        try throwIfProgrammed()
    }

    private func moved(_ state: RideState, _ version: Int32) -> RideStateSnapshot {
        RideStateSnapshot(state: state, version: version, offerExpiresAt: nil)
    }

    /// Programmed failures fire **once**, like every other fake in this target: a test that wants five
    /// refusals in a row re-arms ``nextFailure`` between them, which is also what makes "and the sixth
    /// was never sent" an assertion rather than a coincidence.
    private func throwIfProgrammed() throws {
        guard let failure = nextFailure else { return }
        nextFailure = nil
        throw failure
    }
}

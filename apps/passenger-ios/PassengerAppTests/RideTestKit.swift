import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 4's seams, faked, plus the fixtures its suites are written against.
///
/// Same rule as ``OnboardingTestKit``, ``HomeTestKit`` and ``BookingTestKit``: **every one of these
/// stands in for a Swift protocol, never for a Kotlin class.** ``RideRepository`` exists precisely
/// because it would otherwise be four Kotlin interfaces with `suspend` methods; ``RideContact``,
/// ``BankAppHandoff`` and ``CameraAuthoriser`` exist because a model test cannot hold
/// `UIApplication`, and ``RideRatings`` because it cannot open a protected SQLite file.
///
/// **Nothing here constructs a boxed Kotlin primitive.** `RideDriver.etaSeconds` and `.rating` are a
/// `KotlinInt?` and a `KotlinDouble?`, and the fixtures below take Swift values and box them at the
/// one point where the compiler's answer is unambiguous — see ``RideFixtures/driver(name:)``.

// MARK: - The repository

final class FakeRideRepository: RideRepository, @unchecked Sendable {

    /// Programmable answers. A `nil` failure succeeds; a value is thrown.
    var rideFailure: Error?
    var cancelFailure: Error?
    var walletFailure: Error?
    var payFailure: Error?
    var claimFailure: Error?
    var scanFailure: Error?
    var fallbackFailure: Error?

    /// `POST /v1/calls/start` refusing — SCR-PI-028's ``VoipFailure/signalling`` (Δ C102).
    var callFailure: Error?

    /// `POST /v1/calls/{callId}/outcome` refusing. **Never visible to a passenger**, which is what
    /// `VoipCallModelTests` asserts by arming this and expecting the screen not to move.
    var outcomeFailure: Error?

    var ride = RideFixtures.ride()
    var stateSnapshot = RideStateSnapshot(state: RideState.matching, version: 1, offerExpiresAt: nil)
    var cancelResponse = RideFixtures.cancelled(penaltyMinor: nil)
    var wallet = RideFixtures.wallet(availableMinor: 120_000)
    var initiation = RideFixtures.initiation(method: PaymentMethod.scanDriverQr)

    /// What the next status read answers. Wound by a test to make the driver confirm.
    var status = RideFixtures.status(state: PaymentState.qrClaimedByPassenger)

    var claimResponse = RideFixtures.status(state: PaymentState.qrClaimedByPassenger)
    var scanResponse = RideFixtures.status(state: PaymentState.qrClaimedByPassenger)
    var fallbackResponse = RideFixtures.status(state: PaymentState.fellBackToCash, method: PaymentMethod.cash)
    var callResponse = StartCallResponse(
        callId: RideFixtures.callId,
        callType: CallType.freeVoip,
        session: nil
    )

    /// Every call, in order — several rules here are about *what was sent* rather than about state.
    private(set) var rideReads = 0
    private(set) var cancelled: [(rideId: String, version: Int32, reason: RideCancelReason)] = []
    private(set) var walletReads: [String] = []
    private(set) var paid: [(rideId: String, method: PaymentMethod)] = []
    private(set) var scanned: [(rideId: String, payload: String)] = []
    private(set) var claims: [(rideId: String, receiptArtifactId: String?)] = []
    private(set) var fallbacks: [String] = []
    private(set) var statusReads: [String] = []
    private(set) var calls: [(rideId: String, type: CallType)] = []
    private(set) var outcomes: [ReportedOutcome] = []

    func ride(rideId: String) async throws -> RideDetail {
        rideReads += 1
        if let rideFailure { throw rideFailure }
        return ride
    }

    func rideState(rideId: String) async throws -> RideStateSnapshot {
        if let rideFailure { throw rideFailure }
        return stateSnapshot
    }

    func cancel(rideId: String, version: Int32, reason: RideCancelReason) async throws -> CancelRideResponse {
        cancelled.append((rideId, version, reason))
        if let cancelFailure { throw cancelFailure }
        return cancelResponse
    }

    func wallet(userId: String) async throws -> Wallet {
        walletReads.append(userId)
        if let walletFailure { throw walletFailure }
        return wallet
    }

    func pay(rideId: String, method: PaymentMethod) async throws -> PaymentInitiation {
        paid.append((rideId, method))
        if let payFailure { throw payFailure }
        return initiation
    }

    func paymentStatus(paymentId: String) async throws -> PaymentStatus {
        statusReads.append(paymentId)
        return status
    }

    func fallbackToCash(paymentId: String) async throws -> PaymentStatus {
        fallbacks.append(paymentId)
        if let fallbackFailure { throw fallbackFailure }
        return fallbackResponse
    }

    func payByScanningDriverQr(rideId: String, qrPayload: String) async throws -> PaymentStatus {
        scanned.append((rideId, qrPayload))
        if let scanFailure { throw scanFailure }
        return scanResponse
    }

    func claimDriverQrPaid(rideId: String, receiptArtifactId: String?) async throws -> PaymentStatus {
        claims.append((rideId, receiptArtifactId))
        if let claimFailure { throw claimFailure }
        return claimResponse
    }

    func startCall(rideId: String, type: CallType) async throws -> StartCallResponse {
        calls.append((rideId, type))
        if let callFailure { throw callFailure }
        return callResponse
    }

    func reportCallOutcome(callId: String, outcome: CallOutcome) async throws {
        outcomes.append(ReportedOutcome(callId: callId, outcome: outcome))
        if let outcomeFailure { throw outcomeFailure }
    }
}

/// One `POST /v1/calls/{callId}/outcome`, recorded (Δ C102).
///
/// A named type rather than a tuple, for the reason ``ReplacedAddress`` and ``SavedContact`` are:
/// `outcomes.map(\.outcome)` is a key path, and **Swift has none into a tuple** (the C087 finding).
/// The tuples above it predate that rule and are read positionally.
struct ReportedOutcome {
    let callId: String
    let outcome: CallOutcome
}

// MARK: - The system seams

@MainActor
final class FakeRideContact: RideContact {

    /// What `open` answers — `false` is a handset with no cellular radio.
    var opens = true

    private(set) var dialled: [String] = []

    @discardableResult
    func dial(_ phone: String) async -> Bool {
        dialled.append(phone)
        return opens
    }
}

@MainActor
final class FakeBankAppHandoff: BankAppHandoff {

    /// Whether a bank app claimed the link. `false` is AL-15's fallback condition.
    var opens = true

    private(set) var attempts = 0

    /// The links C100's SCR-PI-025a handed over, in order. SCR-PI-017 has none — see
    /// ``SystemBankAppHandoff/lankaQrScheme`` and AL-59's reason for it — so this stays empty for
    /// every cluster-4 suite.
    private(set) var openedUrls: [String] = []

    func openBankApp() async -> Bool {
        attempts += 1
        return opens
    }

    func openBankApp(url: String) async -> Bool {
        attempts += 1
        openedUrls.append(url)
        return opens
    }
}

@MainActor
final class FakeCameraAuthoriser: CameraAuthoriser {

    var access: CameraAccess = .granted
    var isScannerSupported = true

    /// What the system sheet answers, and what `access` becomes afterwards.
    var grantsOnRequest = true

    private(set) var requests = 0
    private(set) var settingsOpened = 0

    func request() async -> Bool {
        requests += 1
        access = grantsOnRequest ? .granted : .blocked
        return grantsOnRequest
    }

    func openSettings() {
        settingsOpened += 1
    }
}

/// §1.11's queue, in memory.
///
/// The interesting assertion is *what was handed over* — a ride id and a driver id — because the
/// stars and the comment have no columns to be handed over in. See ``RideRatings``.
final class FakeRideRatings: RideRatings, @unchecked Sendable {

    private(set) var queued: [(rideId: String, driverId: String?)] = []
    var rated: Set<String> = []

    func queue(rideId: String, driverId: String?) async {
        queued.append((rideId, driverId))
        rated.insert(rideId)
    }

    func isRated(rideId: String) async -> Bool {
        rated.contains(rideId)
    }
}

/// A clock a test winds by hand.
///
/// Both models that count — SCR-PI-014's two minutes and SCR-PI-017's five — take `now` as a
/// closure, so the assertion is about the rule rather than about a sleep.
final class TestClock: @unchecked Sendable {

    private let lock = NSLock()
    private var current: Date

    init(_ start: Date = Date(timeIntervalSince1970: 1_770_000_000)) {
        current = start
    }

    var now: Date {
        lock.lock()
        defer { lock.unlock() }
        return current
    }

    func advance(by seconds: TimeInterval) {
        lock.lock()
        current = current.addingTimeInterval(seconds)
        lock.unlock()
    }
}

// MARK: -

enum RideFixtures {

    static let rideId = "01JRIDE000000000000000001"
    static let driverId = "01JDRIVER00000000000000001"
    static let passengerId = "01JPAX00000000000000000001"
    static let paymentId = "01JPAY0000000000000000001"
    static let callId = "01JCALL000000000000000001"
    static let driverPhone = "+94771234567"

    static let pickup = Place(lat: 6.9344, lng: 79.8428, address: "Galle Face")
    static let dropoff = Place(lat: 6.8649, lng: 79.8997, address: "Nugegoda")

    /// The `KotlinInt` / `KotlinDouble` boxes live here and nowhere else.
    ///
    /// `RideDriver.etaSeconds` is a Kotlin `Int?` and `.rating` a `Double?`, so both cross boxed.
    /// `KotlinInt(int:)` and `KotlinInt(value:)` are both spelled somewhere in this repository and
    /// only one of them is right (the C096 finding) — confining the construction to one factory is
    /// what makes that a single line to fix rather than a sweep.
    static func driver(
        name: String = "K. Fernando",
        etaSeconds: Int32? = 180,
        rating: Double? = 4.8
    ) -> RideDriver {
        RideDriver(
            driverId: driverId,
            name: name,
            photoUrl: nil,
            vehicleType: VehicleType.sedan,
            registrationNumber: "ABC-1234",
            rating: rating.map { KotlinDouble(double: $0) },
            etaSeconds: etaSeconds.map { KotlinInt(int: $0) }
        )
    }

    static func ride(
        state: RideState = RideState.matching,
        kind: RideKind = RideKind.passenger,
        driver: RideDriver? = nil,
        fareMinor: Int64? = 85_000,
        counterpartyPhone: String? = nil,
        version: Int32 = 3
    ) -> RideDetail {
        RideDetail(
            rideId: rideId,
            kind: kind,
            state: state,
            version: version,
            bookerId: passengerId,
            riderId: passengerId,
            riderName: nil,
            pickup: pickup,
            dropoff: dropoff,
            vehicleType: RideVehicleType.sedan,
            paymentMethod: RidePaymentMethod.cash,
            scheduledAt: nil,
            offerExpiresAt: nil,
            driver: driver,
            counterpartyPhone: counterpartyPhone,
            fare: fareMinor.map { FareEstimate(amountMinor: $0, currency: Currency.lkr, surchargeMinor: nil) },
            packageSize: nil,
            packageDescription: nil,
            packageStatus: nil,
            recipientName: nil,
            senderPhone: nil,
            recipientPhone: nil,
            createdAt: IosInstantKt.timestampFromEpochMillis(millis: 1_770_000_000_000)
        )
    }

    /// A ride with a driver on it — SCR-PI-015's state, and the one where a cancel costs Rs 50.
    static func accepted(state: RideState = RideState.accepted) -> RideDetail {
        ride(state: state, driver: driver(), counterpartyPhone: driverPhone)
    }

    static func cancelled(penaltyMinor: Int64?) -> CancelRideResponse {
        CancelRideResponse(
            rideId: rideId,
            state: RideState.cancelledByRiderAfterAccept,
            version: 4,
            penalty: penaltyMinor.map {
                CancellationPenalty(
                    amountMinor: $0,
                    currency: Currency.lkr,
                    settledOn: PenaltySettlement.nextTrip
                )
            }
        )
    }

    static func wallet(availableMinor: Int64) -> Wallet {
        Wallet(
            userId: passengerId,
            balanceMinor: availableMinor,
            availableMinor: availableMinor,
            outstandingDebtMinor: nil,
            currency: Currency.lkr,
            updatedAt: nil
        )
    }

    static func initiation(
        method: PaymentMethod,
        state: PaymentState = PaymentState.initiated,
        amountMinor: Int64 = 85_000,
        qrImageUrl: String? = "https://cdn.example/driver-qr.png"
    ) -> PaymentInitiation {
        PaymentInitiation(
            paymentId: paymentId,
            state: state,
            method: method,
            amountMinor: amountMinor,
            surchargeMinor: nil,
            currency: Currency.lkr,
            wallet: method == PaymentMethod.wallet ? WalletInitiation(balanceAfterMinor: nil) : nil,
            driverQr: method == PaymentMethod.scanDriverQr ? DriverQrInitiation(qrImageUrl: qrImageUrl) : nil
        )
    }

    static func status(
        state: PaymentState,
        method: PaymentMethod = PaymentMethod.scanDriverQr,
        amountMinor: Int64 = 85_000
    ) -> PaymentStatus {
        PaymentStatus(
            paymentId: paymentId,
            rideId: rideId,
            state: state,
            method: method,
            amountMinor: amountMinor,
            surchargeMinor: nil,
            tipMinor: nil,
            currency: Currency.lkr,
            settledAt: nil
        )
    }

}

/// A failure with nothing behind it — what an unreachable service looks like to a model.
enum RideFakeError: Error {
    case unreachable
}

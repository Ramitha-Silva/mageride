import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 5's seam, faked, plus the fixtures its suites are written against.
///
/// Same rule as ``OnboardingTestKit``, ``HomeTestKit``, ``BookingTestKit`` and ``RideTestKit``:
/// **this stands in for a Swift protocol, never for a Kotlin class.** ``HistoryRepository`` exists
/// precisely because it would otherwise be three Kotlin interfaces with `suspend` methods.
///
/// **The boxed Kotlin primitives live here and nowhere else in this cluster.** `TripDetail` carries a
/// `Long?`, a `Double?` and two `Int?`, and all four cross the bridge boxed. The spellings used are
/// the ones the Objective-C importer derives from the generated initialisers —
/// `initWithInt:` → `init(int:)`, `initWithDouble:` → `init(double:)`, `initWithLongLong:` →
/// `init(longLong:)` — which is the same rule ``RideFixtures`` already follows for the first two.
/// They are therefore one answer rather than three: if the passenger app's existing `KotlinInt(int:)`
/// is right then so is `KotlinLong(longLong:)`, and if it is wrong both are one edit away (the C096
/// finding, which records that `apps/driver-ios` spells the same constructors `init(value:)`).

// MARK: - The repository

final class FakeHistoryRepository: HistoryRepository, @unchecked Sendable {

    /// Programmable answers. A `nil` failure succeeds; a value is thrown.
    var ridesFailure: Error?
    var tripFailure: Error?
    var rideFailure: Error?

    var rows: [RideHistoryRow] = []
    var tripDetail = HistoryFixtures.trip()
    var rideDetail = HistoryFixtures.packageRide()
    var scheduledRows: [ScheduledRideRow] = []

    /// Every call, in order — several rules here are about *what was asked* rather than about state.
    private(set) var historyReads = 0
    private(set) var rideReads: [String] = []
    private(set) var tripReads: [(userId: String, tripId: String)] = []
    private(set) var scheduledReads: [String] = []

    func rides() async throws -> [RideHistoryRow] {
        historyReads += 1
        if let ridesFailure { throw ridesFailure }
        return rows
    }

    func trip(userId: String, tripId: String) async throws -> TripDetail {
        tripReads.append((userId, tripId))
        if let tripFailure { throw tripFailure }
        return tripDetail
    }

    func ride(rideId: String) async throws -> RideDetail {
        rideReads.append(rideId)
        if let rideFailure { throw rideFailure }
        return rideDetail
    }

    func scheduled(userId: String) async throws -> [ScheduledRideRow] {
        scheduledReads.append(userId)
        return scheduledRows
    }
}

// MARK: -

enum HistoryFixtures {

    static let rideId = "01JPKG0000000000000000001"
    static let otherRideId = "01JPKG0000000000000000002"
    static let tripId = "01JTRIP000000000000000001"
    static let senderId = "01JPAX00000000000000000001"
    static let recipientId = "01JPAX00000000000000000002"
    static let driverId = "01JDRIVER00000000000000001"
    static let driverPhone = "+94771234567"
    static let maskedPhone = "+9477*****67"

    static let pickup = Place(lat: 6.8649, lng: 79.8997, address: "Nugegoda")
    static let dropoff = Place(lat: 6.9344, lng: 79.8428, address: "Galle Face")

    /// 2026-06-17 **08:32 in Colombo**, which is 2026-06-17 03:02Z — the wireframe's own
    /// `17 Jun, 08:32`.
    ///
    /// The assertion it feeds is about the **conversion**: a formatter left on the handset's zone
    /// answers a different hour here, and ``afterColomboMidnightMillis`` answers a different *day*.
    static let completedAtMillis: Int64 = 1_781_665_320_000

    /// 2026-06-18 **00:30 in Colombo**, which is 19:00Z on the **17th** — the instant that catches a
    /// formatter running in UTC out on the *date* rather than only on the hour. `apps/driver-ios`'s
    /// two Colombo suites are asserted at the same 19:00Z for the same reason.
    static let afterColomboMidnightMillis: Int64 = 1_781_722_800_000

    static func timestamp(_ millis: Int64 = completedAtMillis) -> Timestamp {
        IosInstantKt.timestampFromEpochMillis(millis: millis)
    }

    static func historyDriver(name: String = "K. Fernando") -> RideHistoryDriver {
        RideHistoryDriver(
            driverId: driverId,
            name: name,
            mobileMasked: maskedPhone,
            callTypesAvailable: [CallType.freeVoip, CallType.directDial]
        )
    }

    /// One row of `GET /v1/rides/history`.
    static func row(
        rideId: String = rideId,
        state: RideState = RideState.paid,
        fareMinor: Int64? = 85_000,
        driver: RideHistoryDriver? = historyDriver()
    ) -> RideHistoryRow {
        RideHistoryRow(
            rideId: rideId,
            state: state,
            pickup: pickup,
            dropoff: dropoff,
            fare: fareMinor.map { FareEstimate(amountMinor: $0, currency: Currency.lkr, surchargeMinor: nil) },
            completedAt: timestamp(),
            driver: driver
        )
    }

    static func rideDriver(etaSeconds: Int32? = 720) -> RideDriver {
        RideDriver(
            driverId: driverId,
            name: "K. Fernando",
            photoUrl: nil,
            vehicleType: VehicleType.threeWheeler,
            registrationNumber: "ABC-1234",
            rating: KotlinDouble(double: 4.7),
            etaSeconds: etaSeconds.map { KotlinInt(int: $0) }
        )
    }

    /// A parcel, as `GET /v1/rides/{rideId}` answers it.
    ///
    /// `bookerId` is what decides which of SCR-PI-020 and SCR-PI-021 is drawn — see
    /// ``PackageTrackModel/party(of:signedInUserId:)``.
    static func packageRide(
        state: RideState = RideState.inprogress,
        packageStatus: PackageStatus? = PackageStatus.pickuppending,
        bookerId: String? = senderId,
        counterpartyPhone: String? = driverPhone,
        driver: RideDriver? = rideDriver()
    ) -> RideDetail {
        RideDetail(
            rideId: rideId,
            kind: RideKind.package,
            state: state,
            version: 4,
            bookerId: bookerId,
            riderId: nil,
            riderName: nil,
            pickup: pickup,
            dropoff: dropoff,
            vehicleType: RideVehicleType.threeWheeler,
            paymentMethod: RidePaymentMethod.cash,
            scheduledAt: nil,
            offerExpiresAt: nil,
            driver: driver,
            counterpartyPhone: counterpartyPhone,
            fare: FareEstimate(amountMinor: 52_000, currency: Currency.lkr, surchargeMinor: nil),
            packageSize: PackageSize.s,
            packageDescription: "Documents",
            packageStatus: packageStatus,
            recipientName: "S. Perera",
            senderPhone: nil,
            recipientPhone: nil,
            createdAt: timestamp()
        )
    }

    /// query-svc's trip detail — the only read that carries a polyline and a filtered distance.
    static func trip(
        distanceKm: Double? = 8.2,
        fareMinor: Int64? = 85_000,
        polyline: String? = HistoryFixtures.polyline,
        geometrySource: GeometrySource = GeometrySource.telemetry,
        driver: TripDriver? = TripDriver(driverId: driverId, name: "K. Fernando", registrationNumber: "ABC-1234")
    ) -> TripDetail {
        TripDetail(
            tripId: tripId,
            plane: TripPlane.ride,
            mode: ServiceMode.c,
            pickup: pickup,
            dropoff: dropoff,
            fareMinor: fareMinor.map { KotlinLong(longLong: $0) },
            currency: Currency.lkr,
            startedAt: timestamp(),
            endedAt: timestamp(completedAtMillis + 900_000),
            polyline: polyline,
            distanceKm: distanceKm.map { KotlinDouble(double: $0) },
            durationSec: KotlinInt(int: 900),
            driver: driver,
            rating: nil,
            geometrySource: geometrySource
        )
    }

    /// Two points near Colombo, Google-encoded. Enough for MAP-08 to draw a line and for a decode to
    /// be worth asserting; the algorithm itself is `EncodedPolylineTests`'.
    static let polyline = "_p~iF~ps|U_ulLnnqC"

    /// `PackageStatusChanged` as the hub sends it — the Kotlin entry names, which is what
    /// `PackageStatus` serialises to (it declares no `@SerialName`).
    static func packageStatusPayload(rideId: String = rideId, status: String = "InTransit") -> String {
        #"{"rideId":"\#(rideId)","status":"\#(status)"}"#
    }

    /// `DriverPosition` as the hub sends it.
    static func driverPositionPayload(rideId: String = rideId) -> String {
        #"{"rideId":"\#(rideId)","lat":6.9271,"lng":79.8612,"heading":90}"#
    }
}

/// A failure with nothing behind it — what an unreachable service looks like to a model.
enum HistoryFakeError: Error {
    case unreachable
}

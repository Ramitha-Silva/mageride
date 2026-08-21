import Foundation
import MageRideShared

@testable import PassengerApp

/// Cluster 3's seams, faked, plus the fixtures its four suites are written against.
///
/// Same rule as ``OnboardingTestKit`` and ``HomeTestKit``: **every one of these stands in for a Swift
/// protocol, never for a Kotlin class.** ``BookingRepository`` exists precisely because it would
/// otherwise be six Kotlin interfaces with `suspend` methods, and ``IdempotencyKeys`` because
/// `IdempotencyKeyGenerator` is one too — this app implements two Kotlin interfaces and no more.
///
/// **Nothing here constructs a boxed Kotlin primitive.** The request shapes come from
/// `IosBookingRequestsKt`, which is the same door production uses, so a test asserts the object the
/// app will actually send.

// MARK: - The repository

final class FakeBookingRepository: BookingRepository, @unchecked Sendable {

    /// Programmable answers. A `nil` failure succeeds; a value is thrown.
    var transitFailure: Error?
    var estimateFailure: Error?
    var requestFailure: Error?
    var scheduleFailure: Error?
    var parseFailure: Error?
    var locationFailure: Error?

    var options = TransitOptionsResponse(options: [], feedVersion: nil, coverage: TransitCoverage.active)
    var route = BookingFixtures.route()
    var estimateMinor: Int64 = 74_000
    var estimateToken = "FARE-TOKEN"
    var rideId = BookingFixtures.rideId
    var pickupOtp: String? = "4821"
    var scheduledRideId = BookingFixtures.scheduledRideId
    var parsed = GeoPoint(lat: 6.9271, lng: 79.8612)
    var reverseName = "No. 27, Maple Ave, Nugegoda"
    var registered = true
    var created = BookingFixtures.createdRequest()
    var locationRequest = BookingFixtures.locationRequest(state: LocationRequestState.pending)

    /// Every call, in order — several rules here are about *what was sent* rather than about state.
    private(set) var estimatedTypes: [RideVehicleType] = []
    private(set) var requested: [RideRequest] = []
    private(set) var scheduled: [ScheduleRideRequest] = []
    private(set) var parsedUrls: [String] = []
    private(set) var lookedUp: [String] = []
    private(set) var locationRequestsFor: [String] = []
    private(set) var confirmed: [(requestId: String, at: GeoPointWithAccuracy)] = []
    private(set) var declined: [String] = []
    private(set) var routeReads = 0

    /// Whether `getBusesOnRoute` was ever reachable from here. It is not, and AL-17 is why — see
    /// ``BookingRepository``, which declares no such method.
    let hasRouteNumberLookup = false

    func transitOptions(from: GeoPoint, to: GeoPoint) async throws -> TransitOptionsResponse {
        if let transitFailure { throw transitFailure }
        return options
    }

    func transitRoute(routeId: String, around: GeoPoint?) async throws -> TransitRoute {
        routeReads += 1
        if let transitFailure { throw transitFailure }
        return route
    }

    func estimate(
        from: GeoPoint,
        to: GeoPoint,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind
    ) async throws -> FareEstimateResponse {
        estimatedTypes.append(vehicleType)
        if let estimateFailure { throw estimateFailure }
        return FareEstimateResponse(
            fareEstimateToken: estimateToken,
            amountMinor: estimateMinor,
            currency: Currency.lkr,
            breakdown: BookingFixtures.breakdown()
        )
    }

    func requestRide(_ request: RideRequest) async throws -> RequestRideResponse {
        requested.append(request)
        if let requestFailure { throw requestFailure }
        return RequestRideResponse(
            rideId: rideId,
            state: RideState.requested,
            version: 1,
            pickupOtp: pickupOtp,
            estimatedFare: FareEstimate(amountMinor: estimateMinor, currency: Currency.lkr, surchargeMinor: nil)
        )
    }

    func scheduleRide(_ request: ScheduleRideRequest) async throws -> ScheduledRide {
        scheduled.append(request)
        if let scheduleFailure { throw scheduleFailure }
        return BookingFixtures.scheduledRide(id: scheduledRideId, request: request)
    }

    func parseMapsLink(_ url: String) async throws -> GeoPoint {
        parsedUrls.append(url)
        if let parseFailure { throw parseFailure }
        return parsed
    }

    func reverseGeocode(_ point: GeoPoint) async throws -> GeocodedPlace {
        GeocodedPlace(lat: point.lat, lng: point.lng, displayName: reverseName, line1: nil, city: nil, source: nil)
    }

    func isRegistered(phone: String) async throws -> Bool {
        lookedUp.append(phone)
        if let locationFailure { throw locationFailure }
        return registered
    }

    func requestRiderLocation(phone: String) async throws -> CreateLocationRequestResponse {
        locationRequestsFor.append(phone)
        if let locationFailure { throw locationFailure }
        return created
    }

    func locationRequest(requestId: String) async throws -> LocationRequest {
        if let locationFailure { throw locationFailure }
        return locationRequest
    }

    func confirmLocationRequest(requestId: String, at: GeoPointWithAccuracy) async throws -> LocationRequest {
        confirmed.append((requestId, at))
        if let locationFailure { throw locationFailure }
        return BookingFixtures.locationRequest(state: LocationRequestState.confirmed)
    }

    func declineLocationRequest(requestId: String) async throws -> LocationRequest {
        declined.append(requestId)
        if let locationFailure { throw locationFailure }
        return BookingFixtures.locationRequest(state: LocationRequestState.declined)
    }
}

// MARK: - Keys

/// A generator that answers the same value until a test winds it.
///
/// R-18's rule is about **reuse**, so a fake that counted up would make the interesting assertion
/// impossible to write: a retry has to carry the id the first attempt did.
final class FakePinnedIdempotencyKeys: IdempotencyKeys, @unchecked Sendable {

    var value = "01JKEY0000000000000000001"
    private(set) var issued = 0

    func next() -> String {
        issued += 1
        return value
    }
}

// MARK: -

enum BookingFixtures {

    static let rideId = "01JRIDE000000000000000001"
    static let scheduledRideId = "01JSCHED00000000000000001"
    static let requestId = "01JLOCREQ0000000000000001"
    static let riderPhone = "771234567"

    static let colombo = Place(lat: 6.9344, lng: 79.8428, address: "Galle Face")
    static let nugegoda = Place(lat: 6.8649, lng: 79.8997, address: "Nugegoda")

    /// A halt ~1.5 km from Colombo Fort — comfortably past BR-23.2's 400 m radius, so the walk hint
    /// is drawn.
    static let farHalt = TransitStop(
        stopId: "STOP-1",
        name: "Pamankada",
        lat: 6.8770,
        lng: 79.8720,
        // Both nullable `Int`s are `nil`: they cross as `KotlinInt?` and this kit builds no boxes.
        sequence: nil,
        distanceM: nil
    )

    /// A halt the passenger is effectively standing at.
    static let nearHalt = TransitStop(
        stopId: "STOP-2",
        name: "Galle Face",
        lat: 6.9345,
        lng: 79.8429,
        sequence: nil,
        distanceM: nil
    )

    static func leg(routeId: String = "R138", shortName: String = "138") -> TransitLeg {
        TransitLeg(
            routeId: routeId,
            routeShortName: shortName,
            headsign: "Pettah → Maharagama",
            description: "via High Level Rd",
            boardStopId: nil,
            alightStopId: nil,
            shape: nil
        )
    }

    static func option(kind: TransitOptionKind = TransitOptionKind.direct, legs: [TransitLeg]) -> TransitOption {
        TransitOption(kind: kind, totalDurationSec: nil, walkingDistanceM: nil, legs: legs)
    }

    static func route(stops: [TransitStop] = [farHalt]) -> TransitRoute {
        TransitRoute(
            routeId: "R138",
            routeShortName: "138",
            routeLongName: nil,
            agencyName: nil,
            // Two points from the algorithm's own published fixture, so the decoder is exercised
            // rather than handed an empty string.
            shape: "_p~iF~ps|U_ulLnnqC",
            stops: stops,
            nearestStops: nil
        )
    }

    static func breakdown() -> FareBreakdown {
        FareBreakdown(
            firstKmMinor: 5_000,
            perKmMinor: 6_000,
            distanceKm: 11.5,
            peakSurchargePct: nil,
            nightSurchargePct: nil
        )
    }

    static func createdRequest(
        state: LocationRequestState = LocationRequestState.pending,
        ttl: Int32 = 300
    ) -> CreateLocationRequestResponse {
        CreateLocationRequestResponse(requestId: requestId, state: state, ttl: ttl)
    }

    static func locationRequest(
        state: LocationRequestState,
        geo: GeoPointWithAccuracy? = nil,
        expiresInSeconds: Double = 300
    ) -> LocationRequest {
        LocationRequest(
            requestId: requestId,
            state: state,
            geo: geo,
            expiresAt: IosInstantKt.timestampFromEpochMillis(
                millis: Int64((Date().timeIntervalSince1970 + expiresInSeconds) * 1000)
            )
        )
    }

    static func scheduledRide(id: String, request: ScheduleRideRequest) -> ScheduledRide {
        ScheduledRide(
            scheduledRideId: id,
            rideId: nil,
            pickup: Place(lat: request.destLat, lng: request.destLng, address: nil),
            dropoff: Place(lat: request.destLat, lng: request.destLng, address: nil),
            vehicleType: request.vehicleType,
            pickupTime: request.pickupTime,
            status: ScheduledRideStatus.scheduled,
            distanceM: nil,
            intentCount: nil
        )
    }
}

/// A failure with nothing behind it — what an unreachable service looks like to a model.
enum BookingFakeError: Error {
    case unreachable
}

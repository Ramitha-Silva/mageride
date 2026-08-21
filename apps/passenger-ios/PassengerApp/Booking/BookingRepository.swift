import Foundation
import MageRideShared

/// Cluster 3's seam onto the platform — six services behind one door.
///
/// A booking is the widest read this app makes: **transit-svc** for the GTFS routes (AL-18),
/// **fare-svc** for the tier prices (AL-19), **ride-svc** for the request and for the whole proxy
/// location-request round trip (P-02), **dispatch-svc** for a scheduled ride, **query-svc** to turn
/// a dropped pin into an address, and **iam** to find out whether a proxy rider is even a user
/// (P-03). Six clients resolved into five models would be thirty initialiser parameters and no
/// single place to see what a booking touches.
///
/// **No caching, no state.** The draft is ``BookingDraft``'s and the screens' state is theirs; this
/// is a translation layer and nothing else, which is what makes it substitutable in a test — and a
/// Swift protocol rather than the Kotlin clients themselves, because all six are Kotlin interfaces
/// with `suspend` methods and Swift can stand in for none of them.
protocol BookingRepository: AnyObject {

    /// `GET /v1/transit/options` — every direct route, then the transfer ones (BR-23.2).
    func transitOptions(from: GeoPoint, to: GeoPoint) async throws -> TransitOptionsResponse

    /// `GET /v1/transit/routes/{routeId}` — the shape and halts for a selected route.
    func transitRoute(routeId: String, around: GeoPoint?) async throws -> TransitRoute

    /// `GET /v1/fare/estimate` — the upfront price, and the token a booking must carry.
    func estimate(
        from: GeoPoint,
        to: GeoPoint,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind
    ) async throws -> FareEstimateResponse

    /// `POST /v1/rides/request` — 202.
    func requestRide(_ request: RideRequest) async throws -> RequestRideResponse

    /// `POST /v1/rides/schedule` — a ride in the future (US-6A.4).
    func scheduleRide(_ request: ScheduleRideRequest) async throws -> ScheduledRide

    /// `GET /v1/geo/parse-maps-link` — AL-20's short-link resolve, server-side.
    func parseMapsLink(_ url: String) async throws -> GeoPoint

    /// `GET /v1/geo/reverse` — what a dropped pin is called.
    func reverseGeocode(_ point: GeoPoint) async throws -> GeocodedPlace

    /// `GET /v1/users/lookup` — P-03, whether a proxy rider can be sent an FCM request at all.
    func isRegistered(phone: String) async throws -> Bool

    /// `POST /v1/location-requests` — ask a rider to share their pickup (P-02).
    func requestRiderLocation(phone: String) async throws -> CreateLocationRequestResponse

    /// `GET /v1/location-requests/{id}` — the poll behind the SignalR event.
    func locationRequest(requestId: String) async throws -> LocationRequest

    /// `POST /v1/location-requests/{id}/confirm` — the rider shares. SCR-PI-011's Share.
    func confirmLocationRequest(requestId: String, at: GeoPointWithAccuracy) async throws -> LocationRequest

    /// `POST /v1/location-requests/{id}/decline` — SCR-PI-011's Decline. **Carries no point.**
    func declineLocationRequest(requestId: String) async throws -> LocationRequest
}

/// ``BookingRepository`` over the generated clients.
///
/// The whole of what it adds is what a Swift call site cannot leave out: **every parameter is
/// passed**, because a Kotlin default argument does not survive the export. Two calls go through
/// `IosBookingRequestsKt` instead of straight at the client — see that file for why an optional
/// primitive does not belong on this bridge.
final class ApiBookingRepository: BookingRepository {

    private let transit: TransitApi
    private let fare: FareApi
    private let rides: RideApi
    private let dispatch: DispatchApi
    private let query: QueryApi
    private let iam: IamApi

    init(transit: TransitApi, fare: FareApi, rides: RideApi, dispatch: DispatchApi, query: QueryApi, iam: IamApi) {
        self.transit = transit
        self.fare = fare
        self.rides = rides
        self.dispatch = dispatch
        self.query = query
        self.iam = iam
    }

    func transitOptions(from: GeoPoint, to: GeoPoint) async throws -> TransitOptionsResponse {
        try await transit.getTransitOptions(fromLat: from.lat, fromLng: from.lng, toLat: to.lat, toLng: to.lng)
    }

    func transitRoute(routeId: String, around: GeoPoint?) async throws -> TransitRoute {
        try await IosBookingRequestsKt.transitRouteNear(transit: transit, routeId: routeId, around: around)
    }

    func estimate(
        from: GeoPoint,
        to: GeoPoint,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind
    ) async throws -> FareEstimateResponse {
        try await fare.estimateFare(
            fromLat: from.lat,
            fromLng: from.lng,
            toLat: to.lat,
            toLng: to.lng,
            vehicleType: vehicleType,
            kind: kind
        )
    }

    /// The booking.
    ///
    /// `RideRequest.clientRequestId` is also the idempotency key, and both are the caller's: R-18
    /// dedupes on `(passengerId, clientRequestId)`, so a retry of the *same* booking has to carry
    /// the *same* id or it books twice. Minting one here would make every retry a new ride.
    func requestRide(_ request: RideRequest) async throws -> RequestRideResponse {
        try await rides.requestRide(request: request, idempotencyKey: request.clientRequestId)
    }

    func scheduleRide(_ request: ScheduleRideRequest) async throws -> ScheduledRide {
        try await dispatch.scheduleRide(request: request, idempotencyKey: nil)
    }

    func parseMapsLink(_ url: String) async throws -> GeoPoint {
        try await transit.parseMapsLink(url: url).point
    }

    func reverseGeocode(_ point: GeoPoint) async throws -> GeocodedPlace {
        try await query.reverseGeocode(lat: point.lat, lng: point.lng, lang: nil)
    }

    func isRegistered(phone: String) async throws -> Bool {
        try await iam.lookupUserByPhone(phone: phone).registered
    }

    func requestRiderLocation(phone: String) async throws -> CreateLocationRequestResponse {
        try await rides.createLocationRequest(
            request: CreateLocationRequestRequest(riderPhone: phone, rideDraftId: nil),
            idempotencyKey: nil
        )
    }

    func locationRequest(requestId: String) async throws -> LocationRequest {
        try await rides.getLocationRequest(requestId: requestId)
    }

    func confirmLocationRequest(requestId: String, at: GeoPointWithAccuracy) async throws -> LocationRequest {
        try await rides.confirmLocationRequest(requestId: requestId, request: at, idempotencyKey: nil)
    }

    func declineLocationRequest(requestId: String) async throws -> LocationRequest {
        try await rides.declineLocationRequest(requestId: requestId, idempotencyKey: nil)
    }
}

/// Where the `clientRequestId` a booking must carry comes from.
///
/// A Swift protocol over `:shared`'s `IdempotencyKeyGenerator` rather than the Kotlin interface
/// itself, for the reason `apps/passenger-ios/CLAUDE.md` states as a rule: **this app implements two
/// Kotlin interfaces and no more.** A fake here is four lines and lets a test pin the id across a
/// retry, which is the whole of what R-18 asks a client to get right.
protocol IdempotencyKeys: AnyObject {
    func next() -> String
}

/// ``IdempotencyKeys`` over the generator the HTTP pipeline itself uses — see
/// ``IosAppGraph.idempotencyKeys``.
final class SharedIdempotencyKeys: IdempotencyKeys {

    private let generator: IdempotencyKeyGenerator

    init(generator: IdempotencyKeyGenerator) {
        self.generator = generator
    }

    func next() -> String { generator.next() }
}

extension GeocodedPlace {
    /// A resolved place, with whatever address the platform could give it.
    var asBookingPlace: Place { Place(lat: lat, lng: lng, address: displayName) }
}

extension GeoPoint {
    /// A place from a bare coordinate, for a pin the passenger dropped before it was named.
    func asPlace(address: String? = nil) -> Place { Place(lat: lat, lng: lng, address: address) }
}

package lk.mageride.passenger.booking

import lk.mageride.shared.data.api.dispatch.DispatchApi
import lk.mageride.shared.data.api.fare.FareApi
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.api.transit.TransitApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.ride.CreateLocationRequestRequest
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.transit.TransitRoute

/**
 * Cluster 3's seam onto the platform — six services behind one door.
 *
 * A booking is the widest read this app makes: **transit-svc** for the GTFS routes (AL-18),
 * **fare-svc** for the tier prices (AL-19), **ride-svc** for the request and for the whole proxy
 * location-request round trip (P-02), **dispatch-svc** for a scheduled ride, **query-svc** to turn
 * a dropped pin into an address, and **iam** to find out whether a proxy rider is even a user
 * (P-03). Six clients resolved into five view models would be thirty constructor parameters and no
 * single place to see what a booking touches.
 *
 * **No caching, no state.** The draft is [BookingDraft]'s and the screens' state is theirs; this
 * is a translation layer and nothing else, which is what makes it substitutable in a test.
 */
@Suppress("TooManyFunctions") // One method per contract operation, across six services.
internal interface BookingRepository {

    /** `GET /v1/transit/options` — every direct route, then the transfer ones (BR-23.2). */
    suspend fun transitOptions(from: GeoPoint, to: GeoPoint): TransitOptionsResponse

    /** `GET /v1/transit/routes/{routeId}` — the shape and halts for a selected route. */
    suspend fun transitRoute(routeId: String, around: GeoPoint?): TransitRoute

    /** `GET /v1/fare/estimate` — the upfront price, and the token a booking must carry. */
    suspend fun estimate(
        from: GeoPoint,
        to: GeoPoint,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind,
    ): FareEstimateResponse

    /** `POST /v1/rides/request` — 202. */
    suspend fun requestRide(request: RideRequest): RequestRideResponse

    /** `POST /v1/rides/schedule` — a ride in the future (US-6A.4). */
    suspend fun scheduleRide(request: ScheduleRideRequest): ScheduledRide

    /** `GET /v1/geo/parse-maps-link` — AL-20's short-link resolve, server-side. */
    suspend fun parseMapsLink(url: String): GeoPoint

    /** `GET /v1/geo/reverse` — what a dropped pin is called. */
    suspend fun reverseGeocode(point: GeoPoint): GeocodedPlace

    /** `GET /v1/users/lookup` — P-03, whether a proxy rider can be sent an FCM request at all. */
    suspend fun isRegistered(phone: PhoneE164): Boolean

    /** `POST /v1/location-requests` — ask a rider to share their pickup (P-02). */
    suspend fun requestRiderLocation(phone: PhoneE164): CreateLocationRequestResponse

    /** `GET /v1/location-requests/{id}` — the poll behind the SignalR event. */
    suspend fun locationRequest(requestId: Ulid): LocationRequest

    /** `POST /v1/location-requests/{id}/confirm` — the rider shares. SCR-PA-011's Share. */
    suspend fun confirmLocationRequest(requestId: Ulid, at: GeoPointWithAccuracy): LocationRequest

    /** `POST /v1/location-requests/{id}/decline` — SCR-PA-011's Decline. **Carries no point.** */
    suspend fun declineLocationRequest(requestId: Ulid): LocationRequest
}

/** [BookingRepository] over the generated clients. */
@Suppress("TooManyFunctions") // Mirrors the interface above, one for one.
internal class ApiBookingRepository(
    private val transit: TransitApi,
    private val fare: FareApi,
    private val rides: RideApi,
    private val dispatch: DispatchApi,
    private val query: QueryApi,
    private val iam: IamApi,
) : BookingRepository {

    override suspend fun transitOptions(from: GeoPoint, to: GeoPoint): TransitOptionsResponse =
        transit.getTransitOptions(fromLat = from.lat, fromLng = from.lng, toLat = to.lat, toLng = to.lng)

    override suspend fun transitRoute(routeId: String, around: GeoPoint?): TransitRoute =
        transit.getTransitRoute(routeId = routeId, lat = around?.lat, lng = around?.lng)

    override suspend fun estimate(
        from: GeoPoint,
        to: GeoPoint,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind,
    ): FareEstimateResponse = fare.estimateFare(
        fromLat = from.lat,
        fromLng = from.lng,
        toLat = to.lat,
        toLng = to.lng,
        vehicleType = vehicleType,
        kind = kind,
    )

    /**
     * The booking.
     *
     * `RideRequest.clientRequestId` is also the idempotency key, and both are the caller's: R-18
     * dedupes on `(passengerId, clientRequestId)`, so a retry of the *same* booking has to carry
     * the *same* id or it books twice. Minting one here would make every retry a new ride.
     */
    override suspend fun requestRide(request: RideRequest): RequestRideResponse =
        rides.requestRide(request, idempotencyKey = request.clientRequestId)

    override suspend fun scheduleRide(request: ScheduleRideRequest): ScheduledRide = dispatch.scheduleRide(request)

    override suspend fun parseMapsLink(url: String): GeoPoint = transit.parseMapsLink(url).point

    override suspend fun reverseGeocode(point: GeoPoint): GeocodedPlace =
        query.reverseGeocode(lat = point.lat, lng = point.lng)

    override suspend fun isRegistered(phone: PhoneE164): Boolean = iam.lookupUserByPhone(phone).registered

    override suspend fun requestRiderLocation(phone: PhoneE164): CreateLocationRequestResponse =
        rides.createLocationRequest(CreateLocationRequestRequest(riderPhone = phone))

    override suspend fun locationRequest(requestId: Ulid): LocationRequest = rides.getLocationRequest(requestId)

    override suspend fun confirmLocationRequest(requestId: Ulid, at: GeoPointWithAccuracy): LocationRequest =
        rides.confirmLocationRequest(requestId, at)

    override suspend fun declineLocationRequest(requestId: Ulid): LocationRequest =
        rides.declineLocationRequest(requestId)
}

/** A resolved place, with whatever address the platform could give it. */
internal fun GeocodedPlace.asPlace(): Place = Place(lat = lat, lng = lng, address = displayName)

/** A place from a bare coordinate, for a pin the passenger dropped before it was named. */
internal fun GeoPoint.asPlace(address: String? = null): Place = Place(lat = lat, lng = lng, address = address)

/** When a scheduled ride is in the future, as the contract wants it. */
internal fun Timestamp.isFuture(now: Timestamp): Boolean = this > now

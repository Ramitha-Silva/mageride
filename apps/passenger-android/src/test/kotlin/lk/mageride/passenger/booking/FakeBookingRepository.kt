package lk.mageride.passenger.booking

import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.PhoneE164
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.data.models.fare.FareBreakdown
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.fare.FareEstimateResponse
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.ride.CreateLocationRequestResponse
import lk.mageride.shared.data.models.ride.FareEstimate
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.transit.TransitCoverage
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.transit.TransitRoute
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.Duration.Companion.minutes

/**
 * The booking seam, in memory.
 *
 * A hand-written fake rather than `FakeApiBackend` for this cluster, and the reason is specific:
 * what these tests assert is **which calls a screen makes and what it puts in them** — that
 * `getBusesOnRoute` is never reached, that a decline carries no coordinates, that a tier quote is
 * one call per tier. A fake with recorded arguments answers those directly; a MockEngine round
 * trip would answer them through two layers of serialisation that C013 already tests.
 *
 * Every method records, so a test can assert absence as easily as presence.
 */
internal class FakeBookingRepository : BookingRepository {

    var transitAnswer: TransitOptionsResponse = TransitOptionsResponse(coverage = TransitCoverage.ACTIVE)
    var transitFails: Boolean = false
    var routeAnswer: TransitRoute = TransitRoute(routeId = "R1", routeShortName = "138")
    var estimateFails: Set<RideVehicleType> = emptySet()
    var requestFails: Throwable? = null
    var parsedPoint: GeoPoint? = null
    var parseFails: Throwable? = null
    var registered: Boolean = true
    var locationRequestAnswer: LocationRequest? = null

    /** Every fare estimate asked for, in order. */
    val estimated = mutableListOf<Pair<RideVehicleType, FareEstimateKind>>()

    /** Every ride actually requested. */
    val requested = mutableListOf<RideRequest>()

    /** Every scheduled ride. */
    val scheduled = mutableListOf<ScheduleRideRequest>()

    /** Confirms, with the point each carried. */
    val confirms = mutableListOf<Pair<Ulid, GeoPointWithAccuracy>>()

    /** Declines — id only, because there is nothing else to record. That IS P-02. */
    val declines = mutableListOf<Ulid>()

    val parsedLinks = mutableListOf<String>()
    val locationRequestsFor = mutableListOf<PhoneE164>()

    override suspend fun transitOptions(from: GeoPoint, to: GeoPoint): TransitOptionsResponse {
        if (transitFails) error("transit-svc is unreachable")
        return transitAnswer
    }

    override suspend fun transitRoute(routeId: String, around: GeoPoint?): TransitRoute = routeAnswer

    override suspend fun estimate(
        from: GeoPoint,
        to: GeoPoint,
        vehicleType: RideVehicleType,
        kind: FareEstimateKind,
    ): FareEstimateResponse {
        estimated += vehicleType to kind
        if (vehicleType in estimateFails) error("no price for $vehicleType")
        return FareEstimateResponse(
            fareEstimateToken = "token-${vehicleType.wire}",
            amountMinor = PRICES.getValue(vehicleType),
            breakdown = FareBreakdown(firstKmMinor = 10_000, perKmMinor = 5_000, distanceKm = 4.0),
        )
    }

    override suspend fun requestRide(request: RideRequest): RequestRideResponse {
        requested += request
        requestFails?.let { throw it }
        return RequestRideResponse(
            rideId = RIDE_ID,
            state = RideState.Requested,
            version = 1,
            pickupOtp = "4829".takeIf { request.packageSize != null },
            estimatedFare = FareEstimate(amountMinor = 74_000),
        )
    }

    override suspend fun scheduleRide(request: ScheduleRideRequest): ScheduledRide {
        scheduled += request
        return ScheduledRide(
            scheduledRideId = SCHEDULED_ID,
            pickup = lk.mageride.shared.data.models.Place(lat = 6.9344, lng = 79.8428),
            dropoff = lk.mageride.shared.data.models.Place(lat = request.destLat, lng = request.destLng),
            vehicleType = request.vehicleType,
            pickupTime = request.pickupTime,
            status = ScheduledRideStatus.SCHEDULED,
        )
    }

    override suspend fun parseMapsLink(url: String): GeoPoint {
        parsedLinks += url
        parseFails?.let { throw it }
        return parsedPoint ?: error("no point configured")
    }

    override suspend fun reverseGeocode(point: GeoPoint): GeocodedPlace =
        GeocodedPlace(lat = point.lat, lng = point.lng, displayName = "Colombo Fort")

    override suspend fun isRegistered(phone: PhoneE164): Boolean = registered

    override suspend fun requestRiderLocation(phone: PhoneE164): CreateLocationRequestResponse {
        locationRequestsFor += phone
        return CreateLocationRequestResponse(
            requestId = REQUEST_ID,
            state = if (registered) LocationRequestState.Pending else LocationRequestState.RiderNotRegistered,
            ttl = CreateLocationRequestResponse.TTL_SECONDS,
        )
    }

    override suspend fun locationRequest(requestId: Ulid): LocationRequest = locationRequestAnswer ?: LocationRequest(
        requestId = requestId,
        state = LocationRequestState.Pending,
        expiresAt = Fixtures.NOW + 5.minutes,
    )

    override suspend fun confirmLocationRequest(requestId: Ulid, at: GeoPointWithAccuracy): LocationRequest {
        confirms += requestId to at
        return LocationRequest(
            requestId = requestId,
            state = LocationRequestState.Confirmed,
            geo = at,
            expiresAt = Fixtures.NOW + 5.minutes,
        )
    }

    override suspend fun declineLocationRequest(requestId: Ulid): LocationRequest {
        declines += requestId
        return LocationRequest(
            requestId = requestId,
            state = LocationRequestState.Declined,
            expiresAt = Fixtures.NOW + 5.minutes,
        )
    }

    internal companion object {
        const val RIDE_ID = "01JRIDE0000000000000000001"
        const val SCHEDULED_ID = "01JSCHED000000000000000001"
        const val REQUEST_ID = "01JLOCREQ00000000000000001"

        /** Distinct per tier, so a test can tell which quote a screen selected. */
        val PRICES: Map<RideVehicleType, Long> = RideVehicleType.entries
            .withIndex()
            .associate { (index, type) -> type to (30_000L + index * 10_000L) }
    }
}

/** A fixed clock, for the schedule and TTL assertions. */
internal fun fixedNow(): Timestamp = Fixtures.NOW

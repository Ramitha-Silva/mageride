package lk.mageride.shared.data.api.query

import io.ktor.client.request.parameter
import io.ktor.http.encodeURLPathPart
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.data.models.query.PlaceSearchResponse
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.data.models.query.TransportOptionsResponse
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.query.TripSummary
import kotlin.coroutines.cancellation.CancellationException

/**
 * query-svc — the read models: nearby vehicles, trip history, earnings and geocoding
 * (`backend/contracts/query.yaml`).
 *
 * Read-only by construction. Nothing here writes, so nothing here needs an `Idempotency-Key`,
 * and every call is safe to retry — which is why the map screen can poll [getNearbyVehicles] as
 * the documented fallback when the SignalR backplane is down (D6' §8.3 "Degraded mode").
 *
 * Maps are MapLibre + PMTiles with a self-hosted Nominatim (D-14): [searchPlaces] and
 * [reverseGeocode] are the platform's own geocoder, not a Google proxy.
 */
public interface QueryApi {

    /**
     * `GET /v1/nearby` — vehicles around a point.
     *
     * @param radiusMetres Contract bounds are 100–20 000 m.
     * @param types Filter by vehicle type (US-7.7); sent comma-joined, as `explode: false` asks.
     * @param modes Filter by operating mode.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getNearbyVehicles(
        lat: Double,
        lng: Double,
        radiusMetres: Int? = null,
        types: List<VehicleType>? = null,
        modes: List<ServiceMode>? = null,
    ): NearbyVehiclesResponse

    /** `GET /v1/transport-options` — every way of making this journey, ranked. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getTransportOptions(
        toLat: Double,
        toLng: Double,
        fromLat: Double? = null,
        fromLng: Double? = null,
    ): TransportOptionsResponse

    /** `GET /v1/routes/{routeNumber}/buses` — live buses on a numbered route. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getBusesOnRoute(routeNumber: String): NearbyVehiclesResponse

    /** `GET /v1/trips/{userId}` — every trip this user took, across all three modes. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listTrips(userId: Ulid, page: PageRequest = PageRequest.FIRST): Page<TripSummary>

    /** `GET /v1/trips/{userId}/{tripId}` — one trip with its polyline and rating. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getTrip(userId: Ulid, tripId: Ulid): TripDetail

    /** `GET /v1/earnings/{driverId}` — gross, fees, penalties and net for a period. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun getDriverEarnings(driverId: Ulid, period: EarningsPeriod? = null): EarningsSummary

    /** `GET /v1/earnings/{driverId}/sessions` — per-trip earnings inside a date range. */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun listEarningSessions(
        driverId: Ulid,
        from: BusinessDate? = null,
        to: BusinessDate? = null,
        page: PageRequest = PageRequest.FIRST,
    ): Page<SessionEarning>

    /**
     * `GET /v1/geo/search` — forward geocoding, optionally biased toward a point.
     *
     * @param lang The language to read the answer in (D-26). Reaches the self-hosted Nominatim as
     *   `accept-language`, so what comes back is **partly** translated: OSM carries `name:si` and
     *   `name:ta` for Sri Lanka's towns, districts and provinces and for very little else, and a
     *   road or a shop returns in Latin whatever is asked. `null` asks for no language at all and
     *   is what a caller that has not been updated still gets — deliberately not the same as
     *   asking for English.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun searchPlaces(
        query: String,
        lat: Double? = null,
        lng: Double? = null,
        limit: Int? = null,
        lang: Language? = null,
    ): PlaceSearchResponse

    /**
     * `GET /v1/geo/reverse` — the address at a coordinate.
     *
     * @param lang As [searchPlaces], including the partial coverage: a dropped pin comes back with
     *   its town and district in the asked-for script and its road in Latin.
     */
    @Throws(MageRideError::class, CancellationException::class)
    public suspend fun reverseGeocode(lat: Double, lng: Double, lang: Language? = null): GeocodedPlace
}

internal class KtorQueryApi(private val transport: ApiTransport) : QueryApi {

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getNearbyVehicles(
        lat: Double,
        lng: Double,
        radiusMetres: Int?,
        types: List<VehicleType>?,
        modes: List<ServiceMode>?,
    ): NearbyVehiclesResponse = transport.apiGet(SERVICE, "getNearbyVehicles", "/v1/nearby") {
        parameter("lat", lat)
        parameter("lng", lng)
        parameter("radius", radiusMetres)
        parameter("types", types?.joinToString(",") { it.wire })
        parameter("modes", modes?.joinToString(",") { it.name })
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getTransportOptions(
        toLat: Double,
        toLng: Double,
        fromLat: Double?,
        fromLng: Double?,
    ): TransportOptionsResponse = transport.apiGet(SERVICE, "getTransportOptions", "/v1/transport-options") {
        parameter("toLat", toLat)
        parameter("toLng", toLng)
        parameter("fromLat", fromLat)
        parameter("fromLng", fromLng)
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getBusesOnRoute(routeNumber: String): NearbyVehiclesResponse =
        transport.apiGet(SERVICE, "getBusesOnRoute", "/v1/routes/${routeNumber.encodeURLPathPart()}/buses").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listTrips(userId: Ulid, page: PageRequest): Page<TripSummary> =
        transport.apiGet(SERVICE, "listTrips", "$TRIPS_PATH/$userId") { pageParameters(page) }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getTrip(userId: Ulid, tripId: Ulid): TripDetail =
        transport.apiGet(SERVICE, "getTrip", "$TRIPS_PATH/$userId/$tripId").decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun getDriverEarnings(driverId: Ulid, period: EarningsPeriod?): EarningsSummary =
        transport.apiGet(SERVICE, "getDriverEarnings", "$EARNINGS_PATH/$driverId") {
            parameter("period", period?.wire)
        }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun listEarningSessions(
        driverId: Ulid,
        from: BusinessDate?,
        to: BusinessDate?,
        page: PageRequest,
    ): Page<SessionEarning> = transport.apiGet(SERVICE, "listEarningSessions", "$EARNINGS_PATH/$driverId/sessions") {
        parameter("from", from?.toString())
        parameter("to", to?.toString())
        pageParameters(page)
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun searchPlaces(
        query: String,
        lat: Double?,
        lng: Double?,
        limit: Int?,
        lang: Language?,
    ): PlaceSearchResponse = transport.apiGet(SERVICE, "searchPlaces", "/v1/geo/search") {
        parameter("q", query)
        parameter("lat", lat)
        parameter("lng", lng)
        parameter("limit", limit)
        parameter("lang", lang?.wire)
    }.decode()

    @Throws(MageRideError::class, CancellationException::class)
    override suspend fun reverseGeocode(lat: Double, lng: Double, lang: Language?): GeocodedPlace =
        transport.apiGet(SERVICE, "reverseGeocode", "/v1/geo/reverse") {
            parameter("lat", lat)
            parameter("lng", lng)
            parameter("lang", lang?.wire)
        }.decode()

    private companion object {
        val SERVICE = ApiService.QUERY
        const val TRIPS_PATH = "/v1/trips"
        const val EARNINGS_PATH = "/v1/earnings"
    }
}

package lk.mageride.shared.data.models.query

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType

// query-svc — live map snapshot, trip history, earnings, geocoding.
// Source: backend/contracts/query.yaml (D3' "query-svc — nearby, trips, earnings",
// ADD Appendix C).
//
// MAP HARD RULE (D3' header): every map endpoint is MapLibre + PMTiles + Redis GEO + self-hosted
// Nominatim. THERE IS NO GOOGLE MAPS OR PLACES CALL ANYWHERE IN MAGERIDE.
//
// GET /v1/nearby is a SNAPSHOT AND RESYNC read, not the live feed: positions stream over the
// SignalR hub /hubs/live, and this is what a client calls on cold start or after a reconnect.
//
// Visibility rules are enforced here as well as in fan-out (D-22/D-23): Mode C vehicles on active
// hire are excluded from public results (US-7.16); stale and offline vehicles are dropped
// (US-7.17); Mode B vehicles appear only to entitled passengers; and a Mode C driver's NAME is
// exposed ONLY AFTER ACCEPTANCE (US-7.12).

/** Whether a transport option is a private hire or public transport. */
@Serializable
public enum class TransportOptionKind {
    @SerialName("private")
    PRIVATE,

    @SerialName("public")
    PUBLIC,
}

/**
 * Which plane a trip came from (`query.yaml#/components/schemas/TripSummary.plane`).
 *
 * [RIDE] is a Mode C ride owned by ride-svc; [SESSION] is a Mode A/B tracking session owned by
 * trip-state-svc. Trip history spans both, because that is what a passenger means by "my trips".
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class TripPlane(public val wire: String) {
    @SerialName("ride")
    RIDE("ride"),

    @SerialName("session")
    SESSION("session"),
}

/**
 * The `?period=` window on the earnings dashboard. Evaluated in **Asia/Colombo** (D-13).
 *
 * @property wire The value as it appears in the query and on the wire.
 */
@Serializable
public enum class EarningsPeriod(public val wire: String) {
    @SerialName("today")
    TODAY("today"),

    @SerialName("week")
    WEEK("week"),

    @SerialName("month")
    MONTH("month"),
}

/**
 * Where a geocoded place came from.
 *
 * [NOMINATIM] is the self-hosted geocoder; [SAVED] and [RECENT] are the caller's own places,
 * blended into destination search so the two do not need separate round-trips.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class GeocodedPlaceSource(public val wire: String) {
    @SerialName("nominatim")
    NOMINATIM("nominatim"),

    @SerialName("saved")
    SAVED("saved"),

    @SerialName("recent")
    RECENT("recent"),
}

/**
 * A vehicle on the live map (`query.yaml#/components/schemas/NearbyVehicle`).
 *
 * @property vehicleId The vehicle.
 * @property type Canonical type, so the map can pick its marker.
 * @property mode Operating mode.
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property heading Course over ground, 0…359.
 * @property speed Metres per second.
 * @property driverName **Mode C only, and only after the ride is accepted** (US-7.12).
 * @property etaSeconds Seconds to the querying passenger.
 * @property registrationNumber Plate.
 */
@Serializable
public data class NearbyVehicle(
    val vehicleId: Ulid,
    val type: VehicleType,
    val mode: ServiceMode,
    val lat: Double,
    val lng: Double,
    val heading: Int? = null,
    val speed: Double? = null,
    val driverName: String? = null,
    val etaSeconds: Int? = null,
    val registrationNumber: String? = null,
) {
    /** The vehicle's position as a plain coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/**
 * `GET /v1/nearby` and `GET /v1/routes/{routeNumber}/buses` — 200. One shape for both.
 *
 * @property vehicles The visible vehicles.
 * @property asOf When the snapshot was taken. A client uses it to decide whether a socket frame
 *   it already holds is newer.
 */
@Serializable
public data class NearbyVehiclesResponse(val vehicles: List<NearbyVehicle> = emptyList(), val asOf: Timestamp)

/**
 * One way to reach a destination (`query.yaml#/components/schemas/TransportOption`, US-7.15).
 *
 * Combines the private tiers with the public-transport options transit-svc computes from the
 * active GTFS feed. Trains are included, which is the whole point of the story.
 *
 * @property kind Private hire or public transport.
 * @property label Server-rendered display text for the option.
 * @property vehicleType The private tier this option is, when it is one.
 * @property routeNumber The public route this option runs, when it is one.
 * @property etaSeconds Seconds to arrival.
 * @property estimatedFareMinor Estimated cost, minor units.
 * @property currency Always LKR.
 * @property transfers `0` is a direct public-transport route (AL-18).
 */
@Serializable
public data class TransportOption(
    val kind: TransportOptionKind,
    val label: String,
    val vehicleType: VehicleType? = null,
    val routeNumber: String? = null,
    val etaSeconds: Int? = null,
    val estimatedFareMinor: Long? = null,
    val currency: Currency? = null,
    val transfers: Int? = null,
)

/**
 * `GET /v1/transport-options` — 200.
 *
 * @property options Every way to get there, public and private.
 */
@Serializable
public data class TransportOptionsResponse(val options: List<TransportOption> = emptyList())

/**
 * One row of trip history (`query.yaml#/components/schemas/TripSummary`, US-8.7).
 *
 * @property tripId The ride or session.
 * @property plane Which aggregate it came from.
 * @property mode Operating mode.
 * @property pickup Where it started.
 * @property dropoff Where it ended.
 * @property fareMinor What it cost, minor units.
 * @property currency Always LKR.
 * @property startedAt When it began.
 * @property endedAt When it finished.
 */
@Serializable
public data class TripSummary(
    val tripId: Ulid,
    val plane: TripPlane,
    val mode: ServiceMode? = null,
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val fareMinor: Long? = null,
    val currency: Currency? = null,
    val startedAt: Timestamp,
    val endedAt: Timestamp? = null,
)

/** The driver on a trip detail. */
@Serializable
public data class TripDriver(
    val driverId: Ulid? = null,
    val name: String? = null,
    val registrationNumber: String? = null,
)

/**
 * A trip with its track (`query.yaml#/components/schemas/TripDetail` —
 * `allOf(TripSummary, …)`, flattened).
 *
 * The polyline is the **Kalman-filtered** track (E-04) — the same one the fare was computed from,
 * so a passenger comparing the map with the receipt sees one number.
 *
 * @property polyline Encoded polyline of the filtered track.
 * @property distanceKm Distance travelled.
 * @property durationSec Trip duration in seconds.
 * @property driver Who drove.
 * @property rating The rating left for this trip, 1–5.
 */
@Serializable
public data class TripDetail(
    val tripId: Ulid,
    val plane: TripPlane,
    val mode: ServiceMode? = null,
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val fareMinor: Long? = null,
    val currency: Currency? = null,
    val startedAt: Timestamp,
    val endedAt: Timestamp? = null,
    val polyline: String? = null,
    val distanceKm: Double? = null,
    val durationSec: Int? = null,
    val driver: TripDriver? = null,
    val rating: Int? = null,
)

/**
 * `GET /v1/earnings/{driverId}` — 200 (US-9.22).
 *
 * Earnings post **only from terminal payment states** (R-05), so an in-flight payment never
 * inflates the dashboard.
 *
 * @property period The window.
 * @property rangeFrom First Asia/Colombo day in the window.
 * @property rangeTo Last Asia/Colombo day in the window.
 * @property grossMinor Fares earned, minor units.
 * @property dailyFeeMinor Platform fees deducted, minor units.
 * @property penaltyMinor Penalties deducted, minor units.
 * @property tipMinor Gratuities received, minor units (E-10).
 * @property netMinor What the driver actually keeps, minor units.
 * @property currency Always LKR.
 * @property trips Completed trips in the window.
 */
@Serializable
public data class EarningsSummary(
    val period: EarningsPeriod,
    val rangeFrom: BusinessDate? = null,
    val rangeTo: BusinessDate? = null,
    val grossMinor: Long,
    val dailyFeeMinor: Long? = null,
    val penaltyMinor: Long? = null,
    val tipMinor: Long? = null,
    val netMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val trips: Int,
) : MoneyHolder {
    /** What the driver keeps — the number the dashboard leads with. */
    override val money: Money get() = Money(amountMinor = netMinor, currency = currency)
}

/**
 * One completed ride or session's earnings
 * (`query.yaml#/components/schemas/SessionEarning`).
 *
 * @property tripId The ride or session.
 * @property grossMinor Fare earned, minor units.
 * @property dailyFeeMinor Platform fee netted out, minor units.
 * @property penaltyMinor Penalty netted out, minor units.
 * @property tipMinor Gratuity, minor units.
 * @property netMinor What the driver keeps, minor units.
 * @property currency Always LKR.
 * @property endedAt When the trip finished.
 */
@Serializable
public data class SessionEarning(
    val tripId: Ulid,
    val grossMinor: Long,
    val dailyFeeMinor: Long? = null,
    val penaltyMinor: Long? = null,
    val tipMinor: Long? = null,
    val netMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val endedAt: Timestamp,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = netMinor, currency = currency)
}

/**
 * A geocoded place (`query.yaml#/components/schemas/GeocodedPlace`).
 *
 * Destination search returns these plus the caller's saved and recent addresses; it never returns
 * route rows, because **a destination is a geo-location only** (AL-17).
 *
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property displayName Full label as Nominatim renders it.
 * @property line1 First address line.
 * @property city City or town.
 * @property source Where the row came from.
 */
@Serializable
public data class GeocodedPlace(
    val lat: Double,
    val lng: Double,
    val displayName: String,
    val line1: String? = null,
    val city: String? = null,
    val source: GeocodedPlaceSource? = null,
) {
    /** The place as a plain coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)

    /** The place as a booking-ready [Place]. */
    public fun toPlace(): Place = Place(lat = lat, lng = lng, address = displayName)
}

/**
 * `GET /v1/geo/search` — 200.
 *
 * @property places Matching places, best first.
 */
@Serializable
public data class PlaceSearchResponse(val places: List<GeocodedPlace> = emptyList())

package lk.mageride.shared.data.models

import kotlinx.serialization.Serializable

/**
 * A WGS-84 coordinate (`_shared.yaml#/components/schemas/GeoPoint`).
 *
 * Latitude and longitude are `double` on the wire and stay `Double` here — unlike money, a
 * coordinate genuinely is a real number and the sink (`telemetry.positions`, C006) range-checks
 * it with plain CHECK constraints rather than trusting the client.
 *
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 */
@Serializable
public data class GeoPoint(val lat: Double, val lng: Double)

/**
 * A coordinate plus the device's own horizontal-accuracy estimate
 * (`_shared.yaml#/components/schemas/GeoPointWithAccuracy`).
 *
 * This is the body of `POST /v1/location-requests/{requestId}/confirm` (P-02): a rider sharing
 * their position as the pickup point. `accuracy` is what lets the booker's map draw the
 * uncertainty circle instead of implying a metre-perfect pin.
 *
 * @property accuracy Horizontal accuracy in metres, as reported by the device.
 */
@Serializable
public data class GeoPointWithAccuracy(val lat: Double, val lng: Double, val accuracy: Double? = null) {
    /** The coordinate without its accuracy, for callers that only need the point. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/**
 * A coordinate plus its optional human address (`_shared.yaml#/components/schemas/Place`).
 *
 * The pickup and dropoff of every ride are places. `address` is server-supplied display text
 * (reverse-geocoded by nominatim-svc, or the label the passenger saved) — it is data, not a
 * localised string this module owns.
 *
 * @property address Free-form address line, at most 512 characters.
 */
@Serializable
public data class Place(val lat: Double, val lng: Double, val address: String? = null) {
    /** The coordinate without its address. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

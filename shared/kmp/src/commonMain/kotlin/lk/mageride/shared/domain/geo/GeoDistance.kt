package lk.mageride.shared.domain.geo

import lk.mageride.shared.data.models.GeoPoint
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.asin
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

// Spherical geometry, and the rule that a geocell is never a distance bound.
//
// ADD §7.4 step 5 and D5' §3.1 both say it in the same words: the H3 cell is a COARSE PRE-FILTER
// and "the H3 cell alone is never treated as a final distance bound". A res-7 hexagon is ~5.16 km²
// with a ~1.22 km edge, so two points in the same cell can be 2.4 km apart and two points 50 m
// apart can be in different cells. Everything that says "within N metres" — a nearby-vehicle list,
// a near-pickup geofence, a dispatch candidate — has to pass through [exactWithin] or
// [distanceMetres] after the cell lookup.
//
// The server's own post-filter is `ST_DWithin` on PostGIS geography (an ellipsoid) or Redis
// `GEOSEARCH BYRADIUS` (a sphere). Haversine agrees with both to well under a metre at the radii
// in play here, and the server is authoritative regardless — this exists so the client can filter
// what it draws and explain what it filtered.

/** IUGG mean Earth radius, metres. The same figure Redis GEO uses. */
private const val EARTH_RADIUS_M = 6_371_008.8

private const val FULL_TURN_DEG = 360.0
private const val HALF_TURN_DEG = 180.0

/** Not a `const`: [PI] is an `expect val` in commonMain, so this cannot fold at compile time. */
private val DEGREES_PER_RADIAN: Double = HALF_TURN_DEG / PI

private fun Double.toRadians(): Double = this / DEGREES_PER_RADIAN

private fun Double.toDegrees(): Double = this * DEGREES_PER_RADIAN

/**
 * Great-circle distance in metres (haversine).
 *
 * Symmetric, and zero for identical points — the coalesce rules in
 * [lk.mageride.shared.mqtt.AdaptiveRateEngine] depend on both.
 */
public fun distanceMetres(from: GeoPoint, to: GeoPoint): Double {
    val dLat = (to.lat - from.lat).toRadians()
    val dLng = (to.lng - from.lng).toRadians()
    val lat1 = from.lat.toRadians()
    val lat2 = to.lat.toRadians()
    val h = sin(dLat / 2).let { it * it } + cos(lat1) * cos(lat2) * sin(dLng / 2).let { it * it }
    return 2 * EARTH_RADIUS_M * asin(min(1.0, sqrt(h)))
}

/** Initial great-circle bearing from [from] to [to], degrees clockwise from north, `0..360`. */
public fun bearingDegrees(from: GeoPoint, to: GeoPoint): Double {
    val lat1 = from.lat.toRadians()
    val lat2 = to.lat.toRadians()
    val dLng = (to.lng - from.lng).toRadians()
    val y = sin(dLng) * cos(lat2)
    val x = cos(lat1) * sin(lat2) - sin(lat1) * cos(lat2) * cos(dLng)
    return (atan2(y, x).toDegrees() + FULL_TURN_DEG) % FULL_TURN_DEG
}

/** The smaller angle between two bearings, `0..180`. */
public fun angularDifferenceDegrees(a: Double, b: Double): Double {
    val raw = abs(a - b) % FULL_TURN_DEG
    return if (raw > HALF_TURN_DEG) FULL_TURN_DEG - raw else raw
}

/** Whether [point] is within [radiusMetres] of [centre], inclusive of the boundary. */
public fun isWithin(point: GeoPoint, centre: GeoPoint, radiusMetres: Double): Boolean =
    distanceMetres(centre, point) <= radiusMetres

/**
 * **The mandatory exact-distance post-filter** (R-06, ADD §7.4, D5' §3.1).
 *
 * Apply this to anything a geocell lookup produced before treating it as "within N metres". The
 * result keeps the input order and is annotated with the measured distance, so a caller can sort
 * by it without measuring twice.
 *
 * @param candidates What the coarse cell pre-filter returned.
 * @param centre The point the radius is measured from — the passenger, or the pickup.
 * @param radiusMetres The bound the caller actually means.
 * @param position How to read a candidate's coordinate.
 */
public fun <T> exactWithin(
    candidates: Iterable<T>,
    centre: GeoPoint,
    radiusMetres: Double,
    position: (T) -> GeoPoint,
): List<Measured<T>> = candidates
    .map { Measured(it, distanceMetres(centre, position(it))) }
    .filter { it.distanceMetres <= radiusMetres }

/**
 * A candidate that survived [exactWithin], with the distance that let it through.
 *
 * @property value The candidate.
 * @property distanceMetres Great-circle metres from the filter's centre.
 */
public data class Measured<T>(public val value: T, public val distanceMetres: Double)

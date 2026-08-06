package lk.mageride.passenger.map

import lk.mageride.shared.data.models.VehicleType

/**
 * One vehicle as the map draws it.
 *
 * The same type goes into [MarkerInterpolator] as a target and comes out of it tweened, which is
 * what keeps MAP-04 invisible to a caller: a screen hands over the frames it received and reads
 * back where the markers should be.
 *
 * @property vehicleId The vehicle. Also the feature property MAP-07's tap reads.
 * @property lat Latitude, degrees.
 * @property lng Longitude, degrees.
 * @property heading Bearing, 0…359, for MAP-06's arrow.
 * @property type Canonical vehicle type, for MAP-03's colour. `null` draws `vehPrivate` grey —
 *   a type this build does not know is still drawn, because a missing marker reads as an outage.
 */
internal data class MapVehicle(
    val vehicleId: String,
    val lat: Double,
    val lng: Double,
    val heading: Double = 0.0,
    val type: VehicleType? = null,
)

/**
 * MAP-04 — *"smooth marker animation between position updates (interpolation)"*.
 *
 * **Why this is not a MapLibre feature.** The hub delivers a per-cell batch every **2–8 s**
 * (US-7.3, `signalr-hub.md` §3) rather than a fix per second: a vehicle's `GeoJsonSource` feature
 * therefore jumps hundreds of metres at a time, and a map of teleporting pins is what MAP-04
 * exists to prevent. MapLibre does not tween a source's contents — a `SymbolLayer` draws whatever
 * the source holds on that frame — so the interpolation is ours, and it belongs in pure Kotlin
 * where it can be tested on a build host with no GL surface.
 *
 * **The glide lasts as long as the last gap did, per vehicle.** A bus reporting every two seconds
 * glides for two and is therefore in continuous motion; a tuk reporting every eight glides for
 * eight. A fixed duration would make one of the two either sprint-and-freeze or crawl. The
 * measured gap is clamped to [MIN_DURATION_MS]…[MAX_DURATION_MS], so a burst of two batches inside
 * one second or a vehicle that went quiet for a minute cannot produce either extreme.
 *
 * **The rendered marker is therefore one batch behind the truth, on purpose.** That is inherent to
 * smoothing; the alternative is drawing the newest fix instantly, which is the jump MAP-04
 * forbids. Nothing *decides* anything from the interpolated value — the exact-distance rules
 * (`GeoCells.exactWithin`, the 100 m geofence) run against the frame, never against what the map
 * happens to be drawing this instant.
 *
 * Not thread-safe: drive it from the frame loop that reads it, like the map it feeds.
 *
 * @param minDurationMs Overridable for tests; the default is [MIN_DURATION_MS].
 * @param maxDurationMs Overridable for tests; the default is [MAX_DURATION_MS].
 */
internal class MarkerInterpolator(
    private val minDurationMs: Long = MIN_DURATION_MS,
    private val maxDurationMs: Long = MAX_DURATION_MS,
) {

    private val tracks = LinkedHashMap<String, Track>()

    /**
     * Seats a new batch of targets, and drops every vehicle not in it.
     *
     * A vehicle seen for the first time appears **at** its position rather than gliding in from
     * nowhere. Every later batch starts the glide from where the marker is *currently drawn*, not
     * from its previous target, so a batch arriving mid-glide does not snap the marker backwards.
     *
     * Removal is immediate and never animated: a vehicle that went on hire (US-7.16), went stale
     * (US-7.17) or whose Mode B share was revoked (D-22) must leave the map now, and a marker
     * fading out over eight seconds is eight seconds of showing a vehicle the passenger is no
     * longer entitled to see.
     *
     * @param vehicles Everything that should be on the map, already filtered by the caller.
     * @param nowMs A monotonic clock in milliseconds.
     */
    fun onFrames(vehicles: List<MapVehicle>, nowMs: Long) {
        vehicles.forEach { target ->
            val existing = tracks[target.vehicleId]
            tracks[target.vehicleId] = if (existing == null) {
                Track(from = target, to = target, startedAt = nowMs, durationMs = 0, lastFrameAt = nowMs)
            } else {
                Track(
                    from = existing.at(nowMs),
                    to = target,
                    startedAt = nowMs,
                    durationMs = (nowMs - existing.lastFrameAt).coerceIn(minDurationMs, maxDurationMs),
                    lastFrameAt = nowMs,
                )
            }
        }
        tracks.keys.retainAll(vehicles.mapTo(HashSet()) { it.vehicleId })
    }

    /** Forgets every track. The map was closed, or the passenger signed out. */
    fun clear() {
        tracks.clear()
    }

    /** Whether every marker has reached its target — i.e. whether the frame loop may stop. */
    fun isSettled(nowMs: Long): Boolean = tracks.values.all { nowMs >= it.startedAt + it.durationMs }

    /** Where every marker should be drawn at [nowMs]. */
    fun markersAt(nowMs: Long): List<MapVehicle> = tracks.values.map { it.at(nowMs) }

    private data class Track(
        val from: MapVehicle,
        val to: MapVehicle,
        val startedAt: Long,
        val durationMs: Long,
        val lastFrameAt: Long,
    ) {
        fun at(nowMs: Long): MapVehicle {
            if (durationMs <= 0L) return to
            val t = ((nowMs - startedAt).toDouble() / durationMs).coerceIn(0.0, 1.0)
            return to.copy(
                lat = from.lat + (to.lat - from.lat) * t,
                lng = from.lng + (to.lng - from.lng) * t,
                heading = interpolateBearing(from.heading, to.heading, t),
            )
        }
    }

    internal companion object {

        /**
         * The shortest glide.
         *
         * Two batches arriving inside one second — a reconnect snapshot immediately followed by
         * the next live batch — would otherwise animate over a few milliseconds, which is the jump
         * this class exists to remove.
         */
        const val MIN_DURATION_MS: Long = 1_000

        /**
         * The longest.
         *
         * US-7.3's batch window tops out at 8 s and fanout-svc drops a vehicle unheard from inside
         * its 60 s freshness window, so a gap longer than eight seconds means the vehicle was
         * quiet rather than slow. Gliding for a minute would draw a smooth path nothing travelled.
         */
        const val MAX_DURATION_MS: Long = 8_000

        private const val FULL_TURN = 360.0
        private const val HALF_TURN = 180.0

        /**
         * Interpolates a bearing the short way round.
         *
         * A vehicle turning from 350° to 10° has turned twenty degrees right, not three hundred
         * and forty degrees left — and the naive linear form spins MAP-06's arrow through a whole
         * rotation every time a vehicle crosses due north.
         */
        fun interpolateBearing(from: Double, to: Double, t: Double): Double {
            val delta = ((to - from + FULL_TURN + HALF_TURN) % FULL_TURN) - HALF_TURN
            return (from + delta * t + FULL_TURN) % FULL_TURN
        }
    }
}

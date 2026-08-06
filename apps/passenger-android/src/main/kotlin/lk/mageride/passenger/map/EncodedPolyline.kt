package lk.mageride.passenger.map

import lk.mageride.shared.data.models.GeoPoint

/**
 * Google's encoded-polyline algorithm, decode half.
 *
 * `transit.yaml` types `TransitLeg.shape` and `TransitRoute.shape` as *"Encoded polyline of the
 * GTFS shape"*, which is the format `shapes.txt` is universally re-encoded into — a few thousand
 * coordinates as a couple of kilobytes of ASCII instead of a JSON array of pairs. SCR-PA-009 draws
 * that shape when a public route is selected, so something has to turn it back into points.
 *
 * **Nothing in `:shared` does this**, and it is not a MapLibre utility either (the Android SDK
 * offers `PolylineUtils` only in the `-ktx`/turf artifacts, and this module cannot depend on those
 * — see the module CLAUDE.md on `checkDuplicateClasses`). Twenty lines of pure Kotlin with no
 * platform in them is the smaller answer, and it is testable on this host, which a rendering path
 * is not. C094 will want the same on iOS; if a third caller appears it belongs in `:shared`.
 *
 * The algorithm: each coordinate is a **delta** from the previous one, scaled by 1e5, zig-zag
 * encoded so the sign lives in bit 0, then split into five-bit groups, each offset by 63 into
 * printable ASCII with bit 5 set on every group but the last.
 */
internal object EncodedPolyline {

    /**
     * Decodes [encoded] into points, in order.
     *
     * **Malformed input yields a short list rather than an exception.** A truncated shape is a
     * server or feed problem, and the screen's answer to it is to draw the part it understood and
     * carry on — a route line is decoration on a booking screen, not the booking.
     */
    fun decode(encoded: String?): List<GeoPoint> {
        if (encoded.isNullOrEmpty()) return emptyList()

        val points = mutableListOf<GeoPoint>()
        var index = 0
        var lat = 0
        var lng = 0
        var readable = true

        while (readable && index < encoded.length) {
            // Both halves or neither: a latitude applied without its longitude would move every
            // subsequent point, so a pair that cannot be completed ends the decode.
            val latDelta = readValue(encoded, index)
            val lngDelta = latDelta?.let { readValue(encoded, it.next) }
            if (latDelta == null || lngDelta == null) {
                readable = false
            } else {
                index = lngDelta.next
                lat += latDelta.value
                lng += lngDelta.value
                points += GeoPoint(lat = lat / SCALE, lng = lng / SCALE)
            }
        }

        return points
    }

    /**
     * One zig-zag varint starting at [from], or `null` if the string ran out mid-value.
     *
     * `null` rather than a partial read: half a delta applied to the running latitude would put a
     * point somewhere arbitrary, and one wrong vertex in a route line is more misleading than a
     * line that simply stops.
     */
    @Suppress("ReturnCount") // A malformed group, a complete group, and the end of the string.
    private fun readValue(encoded: String, from: Int): Chunk? {
        var index = from
        var shift = 0
        var result = 0

        while (index < encoded.length) {
            val byte = encoded[index++].code - OFFSET
            if (byte < 0) return null
            result = result or ((byte and GROUP_MASK) shl shift)
            shift += GROUP_BITS
            if (byte < CONTINUATION_MASK) {
                // Zig-zag: bit 0 is the sign, so a negative delta is `~(v >> 1)` and a positive
                // one is `v >> 1`. Encoding the sign in the low bit is what keeps small negative
                // deltas — most of a route heading south or west — one character wide.
                val value = if (result and 1 != 0) (result shr 1).inv() else result shr 1
                return Chunk(value = value, next = index)
            }
        }

        return null
    }

    private data class Chunk(val value: Int, val next: Int)

    /** 1e5 — five decimal places, about a metre, which is what the format fixes. */
    private const val SCALE = 100_000.0

    /** Printable-ASCII offset: every group is stored as `group + 63`. */
    private const val OFFSET = 63

    /** Bit 5 set means "another group follows". */
    private const val CONTINUATION_MASK = 0x20

    private const val GROUP_MASK = 0x1f
    private const val GROUP_BITS = 5
}

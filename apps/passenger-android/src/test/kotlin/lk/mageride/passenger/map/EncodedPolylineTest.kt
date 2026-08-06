package lk.mageride.passenger.map

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The GTFS shape, decoded.
 *
 * `transit.yaml` hands `TransitLeg.shape` over as an encoded polyline, so SCR-PA-009 cannot draw a
 * public route until this works. The fixtures are the algorithm's own published example plus
 * round-trips through a reference encoder written here, because a decoder tested only against
 * itself is a decoder tested against nothing.
 */
class EncodedPolylineTest {

    @Test
    fun the_published_reference_string_decodes_to_its_published_points() {
        // Google's own documented example for the algorithm: (38.5, -120.2), (40.7, -120.95),
        // (43.252, -126.453). If this passes, the delta chaining, the zig-zag sign and the 1e5
        // scale are all right at once.
        val points = EncodedPolyline.decode("_p~iF~ps|U_ulLnnqC_mqNvxq`@")

        assertEquals(3, points.size)
        assertClose(38.5, points[0].lat)
        assertClose(-120.2, points[0].lng)
        assertClose(40.7, points[1].lat)
        assertClose(-120.95, points[1].lng)
        assertClose(43.252, points[2].lat)
        assertClose(-126.453, points[2].lng)
    }

    @Test
    fun a_sri_lankan_route_round_trips() {
        // A bus route's worth of vertices, all north-east, all small deltas — the shape of every
        // real input this will ever see.
        val original = listOf(
            6.93440 to 79.84280,
            6.93510 to 79.84420,
            6.93600 to 79.84610,
            6.93120 to 79.85030,
            6.92710 to 79.86120,
        )

        val decoded = EncodedPolyline.decode(encode(original))

        assertEquals(original.size, decoded.size)
        original.forEachIndexed { index, (lat, lng) ->
            assertClose(lat, decoded[index].lat)
            assertClose(lng, decoded[index].lng)
        }
    }

    @Test
    fun a_shape_that_runs_south_and_west_keeps_its_signs() {
        // Zig-zag encoding puts the sign in bit 0, so a decoder that drops it produces a route
        // that heads the wrong way — and every Sri Lankan fixture would still pass.
        val original = listOf(0.0 to 0.0, -1.5 to -2.25, -3.0 to -0.75)

        val decoded = EncodedPolyline.decode(encode(original))

        assertEquals(3, decoded.size)
        assertClose(-1.5, decoded[1].lat)
        assertClose(-2.25, decoded[1].lng)
        assertClose(-3.0, decoded[2].lat)
        assertClose(-0.75, decoded[2].lng)
    }

    @Test
    fun nothing_to_draw_is_an_empty_list_rather_than_a_failure() {
        // `TransitLeg.shape` is nullable — a feed without `shapes.txt` is valid GTFS, and the
        // booking screen still has to list that route.
        assertTrue(EncodedPolyline.decode(null).isEmpty())
        assertTrue(EncodedPolyline.decode("").isEmpty())
    }

    @Test
    fun a_truncated_shape_draws_what_it_understood_and_stops() {
        // A route line is decoration on a booking screen, not the booking. Half a delta applied to
        // the running latitude would put a vertex somewhere arbitrary, which is more misleading
        // than a line that simply ends — so the partial value is dropped, not used.
        val full = encode(listOf(38.5 to -120.2, 40.7 to -120.95, 43.252 to -126.453))
        val complete = EncodedPolyline.decode(full)

        val cut = EncodedPolyline.decode(full.dropLast(1))

        assertEquals(3, complete.size)
        assertTrue(cut.size < complete.size, "the trailing point is dropped, not guessed")
        assertClose(38.5, cut.first().lat, "and everything before the cut still decodes")
    }

    @Test
    fun a_string_of_nonsense_decodes_to_nothing_rather_than_throwing() {
        // Not a crash on a malformed feed. Whatever this is, it is not a shape.
        assertTrue(EncodedPolyline.decode("!!!").isEmpty())
    }

    // ------------------------------------------------------------------------------------------

    /**
     * The reference encoder, so the decoder is checked against the algorithm rather than itself.
     *
     * Deliberately written the long way — scale, delta, zig-zag, five-bit groups, `+63` — because
     * a compact one would share whatever misunderstanding the decoder has.
     */
    private fun encode(points: List<Pair<Double, Double>>): String {
        val out = StringBuilder()
        var lastLat = 0
        var lastLng = 0

        points.forEach { (lat, lng) ->
            val scaledLat = kotlin.math.round(lat * SCALE).toInt()
            val scaledLng = kotlin.math.round(lng * SCALE).toInt()
            encodeValue(scaledLat - lastLat, out)
            encodeValue(scaledLng - lastLng, out)
            lastLat = scaledLat
            lastLng = scaledLng
        }

        return out.toString()
    }

    private fun encodeValue(value: Int, out: StringBuilder) {
        var zigzag = if (value < 0) (value shl 1).inv() else value shl 1
        while (zigzag >= 0x20) {
            out.append(((0x20 or (zigzag and 0x1f)) + 63).toChar())
            zigzag = zigzag shr 5
        }
        out.append((zigzag + 63).toChar())
    }

    private fun assertClose(expected: Double, actual: Double, message: String = "") {
        assertTrue(abs(expected - actual) < TOLERANCE, "$message expected $expected but was $actual")
    }

    private companion object {
        const val SCALE = 100_000.0

        /** Half the format's own resolution — it stores five decimal places and no more. */
        const val TOLERANCE = 0.000005
    }
}

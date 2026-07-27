package lk.mageride.shared.serialization

import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.encodeToString
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

/**
 * Proves the kotlinx.serialization compiler plugin is wired into `commonMain` and that
 * [MageRideJson]'s four settings behave the way C013's API client will rely on.
 */
class MageRideJsonTest {
    @Serializable
    private data class Fare(
        val rideId: String,
        val totalMinor: Long,
        val note: String? = null,
        val currency: String = "LKR",
    )

    @Test
    fun unknown_server_fields_do_not_break_an_older_build() {
        val decoded = MageRideJson.decodeFromString<Fare>(
            """{"rideId":"r-1","totalMinor":45000,"surgeReasonAddedLater":"peak"}""",
        )

        assertEquals("r-1", decoded.rideId)
        assertEquals(45_000L, decoded.totalMinor)
    }

    @Test
    fun nulls_and_defaults_are_left_out_of_the_wire_form() {
        val encoded = MageRideJson.encodeToString(Fare(rideId = "r-2", totalMinor = 0))

        assertEquals("""{"rideId":"r-2","totalMinor":0}""", encoded)
    }

    @Test
    fun an_absent_field_and_an_explicit_null_mean_the_same_thing() {
        assertNull(MageRideJson.decodeFromString<Fare>("""{"rideId":"r-3","totalMinor":1,"note":null}""").note)
        assertNull(MageRideJson.decodeFromString<Fare>("""{"rideId":"r-3","totalMinor":1}""").note)
    }

    @Test
    fun a_malformed_body_is_an_error_rather_than_a_coerced_default() {
        assertFailsWith<SerializationException> {
            MageRideJson.decodeFromString<Fare>("""{"rideId":"r-4","totalMinor":"not-a-number"}""")
        }
    }
}

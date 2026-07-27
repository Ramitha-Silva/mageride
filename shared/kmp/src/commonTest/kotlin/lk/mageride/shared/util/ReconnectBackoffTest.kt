package lk.mageride.shared.util

import kotlin.math.pow
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/** R-09's reconnect curve — one rule for the MQTT plane and the SignalR hub alike. */
class ReconnectBackoffTest {

    @Test
    fun the_base_delay_doubles_and_stops_at_sixty_seconds() {
        val backoff = ReconnectBackoff(random = Random(1))

        val delays = List(10) { backoff.next() }

        assertTrue(delays.first() in 0.75.seconds..1.25.seconds, "first delay ~1 s ±25 %")
        assertTrue(delays.last() in 45.seconds..75.seconds, "capped at 60 s ±25 %")
        assertEquals(10, backoff.attempt)
    }

    @Test
    fun every_delay_stays_inside_the_symmetric_band() {
        val backoff = ReconnectBackoff(random = Random(7))

        repeat(50) {
            val base = (1.seconds * 2.0.pow(backoff.attempt)).coerceAtMost(60.seconds)
            val delay = backoff.next()

            assertTrue(delay >= base * 0.75, "$delay below the band around $base")
            assertTrue(delay <= base * 1.25, "$delay above the band around $base")
        }
    }

    @Test
    fun two_clients_reconnecting_together_do_not_land_together() {
        // The whole point: a regional outage ends for every handset at the same instant, and an
        // unjittered curve turns that into a synchronised wave at EMQX's connection limit.
        val a = ReconnectBackoff(random = Random(1))
        val b = ReconnectBackoff(random = Random(2))

        val collisions = List(20) { a.next() == b.next() }.count { it }

        assertTrue(collisions <= 1, "jitter should separate two clients")
    }

    @Test
    fun a_successful_connect_resets_the_curve() {
        val backoff = ReconnectBackoff(random = Random(3))
        repeat(6) { backoff.next() }

        backoff.reset()

        assertEquals(0, backoff.attempt)
        assertTrue(backoff.next() <= 1.25.seconds)
    }

    @Test
    fun the_constants_are_the_ones_both_specs_print() {
        assertEquals(1.seconds, ReconnectBackoff.MIN_DELAY)
        assertEquals(60.seconds, ReconnectBackoff.MAX_DELAY)
        assertEquals(0.25, ReconnectBackoff.JITTER_FRACTION)
    }
}

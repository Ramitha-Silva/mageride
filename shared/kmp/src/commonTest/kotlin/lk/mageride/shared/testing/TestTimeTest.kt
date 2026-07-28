package lk.mageride.shared.testing

import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.domain.ride.RideCommand
import lk.mageride.shared.domain.ride.RideProjection
import lk.mageride.shared.domain.ride.RideSnapshot
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.testing.time.TestClock
import lk.mageride.shared.testing.time.testTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * The deterministic time harness — [TestClock] for a clock a statement moves, and
 * [lk.mageride.shared.testing.time.TestTime] for one the coroutine scheduler moves.
 *
 * The reason both exist is the failure they prevent. A test that drives `runTest`'s virtual time
 * *and* a hand-wound clock has two notions of "now", and the moment they disagree a renewal loop
 * whose `delay` has fired reads an `expiresAt` that has not — which looks exactly like a flaky
 * test and is not one.
 */
class TestTimeTest {

    @Test
    fun a_hand_wound_clock_drives_the_offer_ttl() {
        val clock = TestClock()
        val projection = RideProjection(
            initial = RideSnapshot(
                rideId = Fixtures.RIDE_ID,
                kind = RideKind.PASSENGER,
                state = RideState.Offered,
                version = 3,
                offerExpiresAt = Fixtures.NOW + OFFER_TTL,
            ),
            clock = clock::now,
        )

        assertTrue(projection.canSend(RideCommand.ACCEPT_OFFER), "the offer is live for fifteen seconds")
        clock.advanceBy(OFFER_TTL)
        assertFalse(projection.canSend(RideCommand.ACCEPT_OFFER), "R-02: at the deadline the offer is gone")
    }

    @Test
    fun a_clock_refuses_to_run_backwards_but_can_be_repositioned() {
        val clock = TestClock()

        assertFailsWith<IllegalArgumentException> { clock.advanceBy((-1).seconds) }
        assertEquals(Fixtures.NOW, clock.instant)

        clock.set(Fixtures.MIDNIGHT_EDGE)
        assertEquals(Fixtures.MIDNIGHT_EDGE, clock.now())
    }

    @Test
    fun millis_is_the_same_instant_the_other_two_readers_see() {
        val clock = TestClock()
        clock.advanceBy(1500.milliseconds)

        assertEquals(clock.instant.toEpochMilliseconds(), clock.millis())
        assertEquals(clock.now(), clock.instant)
    }

    @Test
    fun virtual_time_moves_the_clock_and_the_scheduler_together() = runTest {
        val time = testTime()
        val wakeUps = mutableListOf<Long>()

        backgroundScope.launch {
            repeat(3) {
                delay(RENEWAL_INTERVAL)
                wakeUps += (time.now() - Fixtures.NOW).inWholeMinutes
            }
        }

        time.advanceBy(RENEWAL_INTERVAL * 3)

        assertEquals(listOf(25L, 50L, 75L), wakeUps, "a delay that fired must be a clock that moved")
    }

    @Test
    fun advance_to_lands_on_a_deadline_rather_than_after_an_interval() = runTest {
        val time = testTime()
        val deadline = Fixtures.NOW + OFFER_TTL

        time.advanceTo(deadline)

        assertEquals(deadline, time.now())
        assertFailsWith<IllegalArgumentException> { time.advanceTo(Fixtures.NOW) }
    }

    @Test
    fun the_harness_clock_starts_where_the_fixtures_do() = runTest {
        assertEquals(Fixtures.NOW, testTime().now())
        assertEquals(Fixtures.NOW, TestClock().now())
    }

    private companion object {
        /** R-02 / D5' §11.11. */
        val OFFER_TTL = 15.seconds

        /** E-02: the MQTT session token is renewed well before its TTL. */
        val RENEWAL_INTERVAL = 25.minutes
    }
}

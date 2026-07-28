package lk.mageride.shared.testing.time

import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * A clock a test moves by hand.
 *
 * Every deadline in this platform is a comparison against "now" — the 15-second offer TTL (R-02),
 * the 30-second geocell hysteresis (R-06), the R-16 grace windows, the daily-fee business date
 * (D-13), the GPS cadence phases (D5' §5.2). All of them are injected as a `() -> Timestamp` or a
 * [Clock] precisely so a test never has to sleep, and this is what a test passes in:
 *
 * ```kotlin
 * val clock = TestClock()
 * val projection = RideProjection(initial = snapshot, clock = clock::now)
 * clock.advanceBy(16.seconds)
 * assertFalse(projection.canSend(RideCommand.ACCEPT_OFFER))
 * ```
 *
 * It satisfies both seam shapes the module uses: `clock::now` for `() -> Timestamp`, `clock` for
 * [Clock], and `clock::millis` for the epoch-millisecond form the circuit breaker and the MQTT
 * rate engine take.
 *
 * **Not thread-safe, and deliberately not.** A clock guarded by a mutex would hide the ordering a
 * concurrency test is trying to pin down; when a test needs time to move with the *coroutine*
 * scheduler rather than with a statement, use [TestTime].
 *
 * @param start Where the clock begins. Defaults to [Fixtures.NOW].
 */
@OptIn(ExperimentalTime::class)
public class TestClock(start: Timestamp = Fixtures.NOW) : Clock {

    private var current: Timestamp = start

    /** The instant this clock is currently at. */
    public val instant: Timestamp get() = current

    override fun now(): Instant = current

    /** Epoch milliseconds, for the seams that take a `() -> Long`. */
    public fun millis(): Long = current.toEpochMilliseconds()

    /**
     * Moves the clock forward and answers where it landed.
     *
     * Refuses to go backwards: a rewound clock makes a TTL test pass for the wrong reason, and
     * every place this stands in for reads time as monotonic within a single flow.
     */
    public fun advanceBy(duration: Duration): Timestamp {
        require(duration >= Duration.ZERO) { "a clock cannot run backwards; use set() to reposition it" }
        current += duration
        return current
    }

    /**
     * Repositions the clock to [instant], forwards or backwards.
     *
     * The escape hatch for a test that is *about* clock disagreement — a handset whose time was
     * corrected mid-ride, or a business-date boundary approached from the far side.
     */
    public fun set(instant: Timestamp): Timestamp {
        current = instant
        return current
    }
}

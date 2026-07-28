package lk.mageride.shared.testing.time

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * A wall clock wired to a [TestScope]'s **virtual** time.
 *
 * [TestClock] and `runTest` each control time, and a test that uses both has to remember to move
 * them together — the bug that produces is a renewal loop whose `delay` has elapsed but whose
 * `expiresAt` has not, which looks like a flake and is not one. This removes the choice: `now()`
 * is [origin] plus however far the scheduler has run, so a `delay(15.seconds)` inside the code
 * under test *is* fifteen seconds on the clock that code compares against.
 *
 * That is what the cadence, TTL and cycle tests need:
 *
 * ```kotlin
 * runTest {
 *     val time = testTime()
 *     val tokens = MqttSessionTokenManager(clock = time::now, ...)
 *     tokens.start(backgroundScope)
 *     time.advanceBy(50.minutes)      // the renewal loop wakes and the clock agrees
 *     assertEquals(2, issued)
 * }
 * ```
 *
 * @property origin The instant virtual time zero corresponds to.
 */
@OptIn(ExperimentalTime::class, ExperimentalCoroutinesApi::class)
public class TestTime internal constructor(private val scope: TestScope, public val origin: Timestamp) : Clock {

    override fun now(): Instant = origin + scope.testScheduler.currentTime.milliseconds

    /** Epoch milliseconds, for the seams that take a `() -> Long`. */
    public fun millis(): Long = now().toEpochMilliseconds()

    /**
     * Runs the scheduler forward by [duration], executing everything scheduled on the way —
     * **including** whatever is scheduled for the instant it lands on.
     *
     * `advanceTimeBy` alone stops just short of its target and leaves a task scheduled at exactly
     * that moment un-run, which reads as "the renewal did not fire" when the point of the
     * assertion is that it did. Following it with `runCurrent` makes the boundary inclusive, which
     * is what "advance to the deadline" has to mean for [advanceTo] to be usable at all.
     */
    public fun advanceBy(duration: Duration) {
        require(duration >= Duration.ZERO) { "virtual time cannot run backwards" }
        scope.testScheduler.advanceTimeBy(duration)
        scope.testScheduler.runCurrent()
    }

    /**
     * Runs the scheduler forward until the clock reads [instant].
     *
     * For assertions written against a deadline rather than against an interval — "advance to the
     * offer's `expiresAt`" says what it means where `advanceBy(15.seconds)` makes the reader do
     * the arithmetic.
     */
    public fun advanceTo(instant: Timestamp): Unit = advanceBy(instant - now())

    /** Runs everything the scheduler already holds, without inventing any elapsed time. */
    public fun runCurrent(): Unit = scope.testScheduler.runCurrent()

    /**
     * A dispatcher on the same scheduler — the one to inject where production code would take
     * `Dispatchers.Default`.
     *
     * `Standard` queues work rather than running it eagerly, which is what makes "the drain worker
     * has not run yet" an assertable state. Use [immediate] only when the test is about the value
     * a flow finally settles on and not about when it got there.
     */
    public val dispatcher: TestDispatcher get() = StandardTestDispatcher(scope.testScheduler)

    /** An eager dispatcher on the same scheduler. */
    public val immediate: TestDispatcher get() = UnconfinedTestDispatcher(scope.testScheduler)
}

/**
 * A clock tied to this scope's virtual time, starting at [origin].
 *
 * @param origin The instant the scheduler's time zero means. Defaults to [Fixtures.NOW].
 */
@OptIn(ExperimentalTime::class)
public fun TestScope.testTime(origin: Timestamp = Fixtures.NOW): TestTime = TestTime(this, origin)

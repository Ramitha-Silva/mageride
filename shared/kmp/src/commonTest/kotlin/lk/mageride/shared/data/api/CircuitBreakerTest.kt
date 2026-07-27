package lk.mageride.shared.data.api

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.ErrorCode
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * D6' §8.3: *"per external dependency — open after 5 failures/30 s, half-open probe after 15 s"*.
 *
 * The clock is injected, so this asserts the policy in microseconds rather than in half a minute.
 */
class CircuitBreakerTest {

    @Test
    fun five_failures_inside_the_window_open_the_breaker() = runTest {
        val clock = TestClock()
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), clock::now)

        repeat(THRESHOLD) {
            breaker.onCallStarted(ApiService.RIDE)
            breaker.onCallFinished(ApiService.RIDE, failed = true)
        }

        assertTrue(breaker.isOpen(ApiService.RIDE))
        assertFailsWith<MageRideError.CircuitOpen> { breaker.onCallStarted(ApiService.RIDE) }
    }

    @Test
    fun four_failures_do_not() = runTest {
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), TestClock()::now)

        repeat(THRESHOLD - 1) {
            breaker.onCallStarted(ApiService.RIDE)
            breaker.onCallFinished(ApiService.RIDE, failed = true)
        }

        assertFalse(breaker.isOpen(ApiService.RIDE))
    }

    @Test
    fun a_success_clears_the_failure_count() = runTest {
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), TestClock()::now)

        repeat(THRESHOLD - 1) {
            breaker.onCallStarted(ApiService.RIDE)
            breaker.onCallFinished(ApiService.RIDE, failed = true)
        }
        breaker.onCallStarted(ApiService.RIDE)
        breaker.onCallFinished(ApiService.RIDE, failed = false)
        repeat(THRESHOLD - 1) {
            breaker.onCallStarted(ApiService.RIDE)
            breaker.onCallFinished(ApiService.RIDE, failed = true)
        }

        assertFalse(breaker.isOpen(ApiService.RIDE))
    }

    @Test
    fun failures_older_than_the_sampling_window_are_forgotten() = runTest {
        val clock = TestClock()
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), clock::now)

        repeat(THRESHOLD - 1) {
            breaker.onCallStarted(ApiService.RIDE)
            breaker.onCallFinished(ApiService.RIDE, failed = true)
            clock.advance(TEN_SECONDS)
        }
        breaker.onCallStarted(ApiService.RIDE)
        breaker.onCallFinished(ApiService.RIDE, failed = true)

        assertFalse(breaker.isOpen(ApiService.RIDE), "the first failures aged out of the 30 s window")
    }

    @Test
    fun the_cooldown_admits_exactly_one_probe() = runTest {
        val clock = TestClock()
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), clock::now)
        breaker.trip()

        clock.advance(FIFTEEN_SECONDS)
        breaker.onCallStarted(ApiService.RIDE)
        assertFailsWith<MageRideError.CircuitOpen> { breaker.onCallStarted(ApiService.RIDE) }
    }

    @Test
    fun a_successful_probe_closes_the_breaker() = runTest {
        val clock = TestClock()
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), clock::now)
        breaker.trip()

        clock.advance(FIFTEEN_SECONDS)
        breaker.onCallStarted(ApiService.RIDE)
        breaker.onCallFinished(ApiService.RIDE, failed = false)

        assertFalse(breaker.isOpen(ApiService.RIDE))
        breaker.onCallStarted(ApiService.RIDE)
    }

    @Test
    fun a_failed_probe_re_opens_the_breaker_immediately() = runTest {
        // The half-open state asks "is it back?"; one "no" is a complete answer and must not wait
        // for another four failures.
        val clock = TestClock()
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), clock::now)
        breaker.trip()

        clock.advance(FIFTEEN_SECONDS)
        breaker.onCallStarted(ApiService.RIDE)
        breaker.onCallFinished(ApiService.RIDE, failed = true)

        assertTrue(breaker.isOpen(ApiService.RIDE))
    }

    @Test
    fun one_sick_service_does_not_take_the_others_down() = runTest {
        val clock = TestClock()
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), clock::now)
        breaker.trip(ApiService.FARE)

        assertTrue(breaker.isOpen(ApiService.FARE))
        assertFalse(breaker.isOpen(ApiService.RIDE))
        breaker.onCallStarted(ApiService.RIDE)
    }

    @Test
    fun the_pipeline_opens_the_breaker_after_repeated_server_errors() = runTest {
        // Two retries per call means the fifth *call*, not the fifth request, trips it.
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), TestClock()::now)
        val test = testApi(breaker = breaker) { _, _ ->
            respondProblem(HttpStatusCode.InternalServerError, ErrorCode.INTERNAL_ERROR.wire)
        }

        repeat(THRESHOLD) {
            assertFailsWith<MageRideError.Server> { test.api.ride.getRide("01RIDE") }
        }
        val refused = assertFailsWith<MageRideError.CircuitOpen> { test.api.ride.getRide("01RIDE") }

        assertEquals(ApiService.RIDE, refused.service)
        assertEquals(THRESHOLD * ATTEMPTS_PER_CALL, test.requests.size, "the refused call never reached the engine")
    }

    @Test
    fun a_4xx_never_opens_the_breaker() = runTest {
        // A mistyped OTP is the service working correctly. Counting it would take the app offline.
        val breaker = CircuitBreaker(CircuitBreakerPolicy(), TestClock()::now)
        val test = testApi(breaker = breaker) { _, _ ->
            respondProblem(HttpStatusCode.BadRequest, ErrorCode.INVALID_OTP.wire)
        }

        repeat(THRESHOLD * 2) {
            assertFailsWith<MageRideError.BadRequest> { test.api.ride.getRide("01RIDE") }
        }

        assertFalse(breaker.isOpen(ApiService.RIDE))
    }

    /** Trips [service] by feeding the breaker its threshold of failures. */
    private suspend fun CircuitBreaker.trip(service: ApiService = ApiService.RIDE) {
        repeat(THRESHOLD) {
            onCallStarted(service)
            onCallFinished(service, failed = true)
        }
    }

    private class TestClock(private var millis: Long = 0L) {
        fun now(): Long = millis

        fun advance(by: Long) {
            millis += by
        }
    }

    private companion object {
        const val THRESHOLD = 5
        const val ATTEMPTS_PER_CALL = 3
        const val TEN_SECONDS = 10_000L
        const val FIFTEEN_SECONDS = 15_000L
    }
}

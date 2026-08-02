package lk.mageride.shared.data.api

import io.ktor.http.HttpStatusCode
import io.ktor.http.headersOf
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProviderCallbackStatus
import lk.mageride.shared.data.models.ride.OtpAttempt
import lk.mageride.shared.data.models.wallet.TopupCallback
import kotlin.random.Random
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * D6' §8.3: *"3 attempts, exponential 100 ms→2 s, ±25% jitter; idempotent only"*.
 *
 * `runTest` runs the backoff on virtual time, so these assert the policy rather than waiting for it.
 */
class RetryAndBackoffTest {

    @Test
    fun a_rate_limited_get_is_retried_up_to_the_attempt_budget() = runTest {
        val test = testApi { attempt, _ ->
            if (attempt < 2) {
                respondProblem(
                    status = HttpStatusCode.TooManyRequests,
                    code = ErrorCode.RATE_LIMITED.wire,
                    headers = headersOf("Retry-After", "1"),
                )
            } else {
                respondJson(RIDE_STATE)
            }
        }

        val state = test.api.ride.getRideState("01RIDE")

        assertEquals(3, test.requests.size, "one try plus two retries")
        assertEquals(1, state.version)
    }

    @Test
    fun a_surviving_429_surfaces_as_rate_limited_with_its_retry_after() = runTest {
        val test = testApi { _, _ ->
            respondProblem(
                status = HttpStatusCode.TooManyRequests,
                code = ErrorCode.RATE_LIMITED.wire,
                headers = headersOf("Retry-After", "30"),
            )
        }

        val error = assertFailsWith<MageRideError.RateLimited> { test.api.ride.getRideState("01RIDE") }

        assertEquals(3, test.requests.size)
        assertEquals(30, error.retryAfterSeconds)
        assertEquals(ErrorCode.RATE_LIMITED, error.code)
    }

    @Test
    fun a_server_error_is_retried_and_a_client_error_is_not() = runTest {
        val retried = testApi { attempt, _ ->
            if (attempt == 0) {
                respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire)
            } else {
                respondJson(RIDE_STATE)
            }
        }
        retried.api.ride.getRideState("01RIDE")
        assertEquals(2, retried.requests.size)

        val notRetried = testApi { _, _ -> respondProblem(HttpStatusCode.NotFound, ErrorCode.NOT_FOUND.wire) }
        assertFailsWith<MageRideError.NotFound> { notRetried.api.ride.getRideState("01RIDE") }
        assertEquals(1, notRetried.requests.size, "a 404 is the service working; retrying it is noise")
    }

    @Test
    fun a_retried_post_reuses_the_original_idempotency_key() = runTest {
        // The definition of done, stated as a test: every attempt must carry the same key so the
        // service replays its recorded response (R-14, R-18) instead of applying the command twice.
        val keys = SequentialIdempotencyKeys()
        val test = testApi(idempotencyKeys = keys) { attempt, _ ->
            if (attempt < 2) {
                respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire)
            } else {
                respondJson(RIDE_STATE_CHANGE)
            }
        }

        test.api.ride.verifyPackagePickupOtp("01RIDE", OtpAttempt("123456"))

        assertEquals(3, test.requests.size)
        assertEquals(1, keys.count, "one command, one key")
        assertEquals(1, test.requests.mapNotNull { it.idempotencyKey }.distinct().size)
    }

    @Test
    fun a_post_without_an_idempotency_key_is_never_retried() = runTest {
        // The provider callbacks are `x-idempotency-exempt` and dedupe on
        // provider_transaction_id (R-19). Repeating one is the platform's business, not ours.
        val test =
            testApi { _, _ -> respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire) }

        assertFailsWith<MageRideError.Server> {
            test.api.wallet.onepayTopupWebhook(
                TopupCallback(providerTransactionId = "OP-1", status = ProviderCallbackStatus.SUCCESS),
            )
        }

        assertEquals(1, test.requests.size)
    }

    @Test
    fun a_transport_failure_is_retried_then_surfaces_as_a_network_error() = runTest {
        val test = testApi { _, _ -> throw MockTransportFailure() }

        assertFailsWith<MageRideError.Network> { test.api.ride.getRideState("01RIDE") }

        assertEquals(3, test.requests.size)
    }

    @Test
    fun retries_can_be_switched_off_by_policy() = runTest {
        val test = testApi(config = testConfig(retry = RetryPolicy(maxAttempts = 1))) { _, _ ->
            respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire)
        }

        assertFailsWith<MageRideError.Server> { test.api.ride.getRideState("01RIDE") }

        assertEquals(1, test.requests.size)
    }

    @Test
    fun backoff_grows_exponentially_and_is_capped() {
        val policy = RetryPolicy(jitterFraction = 0.0)

        assertEquals(100.milliseconds, policy.backoffFor(attempt = 1, retryAfterSeconds = null, random = Random(1)))
        assertEquals(200.milliseconds, policy.backoffFor(attempt = 2, retryAfterSeconds = null, random = Random(1)))
        assertEquals(400.milliseconds, policy.backoffFor(attempt = 3, retryAfterSeconds = null, random = Random(1)))
        assertEquals(2.seconds, policy.backoffFor(attempt = 9, retryAfterSeconds = null, random = Random(1)))
    }

    @Test
    fun jitter_stays_inside_the_declared_band() {
        // ±25% is what stops a fleet of reconnecting drivers retrying in lockstep (R-09).
        val policy = RetryPolicy()
        val random = Random(7)

        repeat(SAMPLES) {
            val delay = policy.backoffFor(attempt = 2, retryAfterSeconds = null, random = random).inWholeMilliseconds
            assertTrue(delay in JITTER_LOW..JITTER_HIGH, "delay $delay outside 150..250 ms")
        }
    }

    @Test
    fun a_retry_after_header_overrides_the_computed_backoff() {
        val policy = RetryPolicy()

        assertEquals(12.seconds, policy.backoffFor(attempt = 1, retryAfterSeconds = 12, random = Random(1)))
    }

    private class MockTransportFailure : RuntimeException("connection reset")

    private companion object {
        const val SAMPLES = 200
        const val JITTER_LOW = 150L
        const val JITTER_HIGH = 250L
        const val RIDE_STATE = """{"state":"Requested","version":1}"""
        const val RIDE_STATE_CHANGE = """{"rideId":"01RIDE","state":"InProgress","version":4}"""
    }
}

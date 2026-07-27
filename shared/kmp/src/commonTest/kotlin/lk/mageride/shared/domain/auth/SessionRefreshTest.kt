package lk.mageride.shared.domain.auth

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.respondJson
import lk.mageride.shared.data.api.respondProblem
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.RideState
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime

internal const val REFRESH_PATH: String = "/v1/auth/refresh"
private const val RIDE_STATE_BODY = """{"state":"Requested","version":1}"""

/**
 * The first definition-of-done line, asserted against the real pipeline: *"a 401 triggers exactly
 * one refresh attempt and replays the original request once."*
 *
 * C013 already proves the mechanism against a fake provider. What is new here is the real
 * [SessionTokenProvider] behind it — a rotating single-use refresh token, persisted, with
 * concurrent callers collapsed onto one rotation (D-29: "presenting a spent token revokes the
 * whole session family").
 */
@OptIn(ExperimentalTime::class)
class SessionRefreshTest {

    @Test
    fun a_401_rotates_the_token_pair_once_and_replays_the_request_once() = runTest {
        val harness = authHarness { _, request ->
            when {
                request.url.encodedPath == REFRESH_PATH -> respondJson(tokenPairJson("access-2", "refresh-2"))

                request.headers["Authorization"] == "Bearer access-1" ->
                    respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)

                else -> respondJson(RIDE_STATE_BODY)
            }
        }
        harness.signIn(accessToken = "access-1", refreshToken = "refresh-1")

        val state = harness.api.ride.getRideState("01RIDE")

        assertEquals(RideState.Requested, state.state, "the replayed call returned its body")
        assertEquals(3, harness.requests.size, "one 401, one refresh, one replay — nothing else")
        assertEquals(1, harness.requestsTo(REFRESH_PATH).size)
        assertEquals("Bearer access-1", harness.requests[0].authorization)
        assertEquals("Bearer refresh-1", harness.requests[1].authorization, "the refresh presents the opaque token")
        assertEquals("Bearer access-2", harness.requests[2].authorization, "the replay uses the rotated token")
        assertIs<SessionState.SignedIn>(harness.sessions.state.value)
    }

    @Test
    fun the_rotated_pair_is_persisted_before_the_replay_goes_out() = runTest {
        val harness = authHarness { _, request ->
            when {
                request.url.encodedPath == REFRESH_PATH -> respondJson(tokenPairJson("access-2", "refresh-2"))

                request.headers["Authorization"] == "Bearer access-1" ->
                    respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)

                else -> respondJson(RIDE_STATE_BODY)
            }
        }
        harness.signIn(accessToken = "access-1", refreshToken = "refresh-1")

        harness.api.ride.getRideState("01RIDE")

        // The old refresh token is spent the moment the server answers, so losing the new one to
        // a crash between "server answered" and "client wrote it" is a forced sign-out.
        val stored = requireNotNull(harness.store.loadSession())
        assertEquals("refresh-2", stored.refreshToken)
        assertEquals("access-2", stored.accessToken)
    }

    @Test
    fun a_401_that_survives_the_refresh_is_not_refreshed_a_second_time() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == REFRESH_PATH) {
                respondJson(tokenPairJson("access-2", "refresh-2"))
            } else {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            }
        }
        harness.signIn()

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertEquals(1, harness.requestsTo(REFRESH_PATH).size, "exactly one refresh, never two")
        assertEquals(3, harness.requests.size)
    }

    @Test
    fun five_concurrent_401s_produce_one_rotation() = runTest {
        // The refresh token is single-use: a second rotation with the same value revokes the
        // session family (D-29). Five requests that all fail at once must therefore share one
        // rotation, not race five.
        //
        // The engine keys off the token the request carried rather than off a "have I rotated
        // yet" flag: MockEngine may serve concurrent requests on several threads, and a flag
        // written on one and read on another makes the *test* the race. The Authorization header
        // travels with its own request, so `access-1` is a first attempt and `access-2` a replay,
        // whatever order they arrive in.
        val arrivals = MutableStateFlow(0)

        val harness = authHarness { _, request ->
            when {
                request.url.encodedPath == REFRESH_PATH -> respondJson(tokenPairJson("access-2", "refresh-2"))

                request.headers["Authorization"] == "Bearer access-1" -> {
                    // Hold every first attempt until all five have arrived, so they genuinely
                    // overlap instead of running to completion one after another.
                    arrivals.update { it + 1 }
                    arrivals.first { it == CONCURRENT_CALLERS }
                    respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
                }

                else -> respondJson(RIDE_STATE_BODY)
            }
        }
        harness.signIn(accessToken = "access-1", refreshToken = "refresh-1")

        coroutineScope { repeat(CONCURRENT_CALLERS) { launch { harness.api.ride.getRideState("01RIDE") } } }

        assertEquals(1, harness.requestsTo(REFRESH_PATH).size, "one rotation for five concurrent 401s")
        assertEquals(CONCURRENT_CALLERS * 2 + 1, harness.requests.size, "five 401s, one refresh, five replays")
        assertEquals(
            CONCURRENT_CALLERS,
            harness.requests.count { it.authorization == "Bearer access-2" },
            "every replay used the rotated token",
        )
    }

    @Test
    fun an_access_token_near_expiry_is_rotated_before_the_request_goes_out() = runTest {
        // ADD §12.1: "the API access JWT remains 30 min with proactive refresh". Waiting for the
        // 401 costs a round trip on the request that happened to be unlucky.
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == REFRESH_PATH) {
                respondJson(tokenPairJson("access-2", "refresh-2"))
            } else {
                respondJson(RIDE_STATE_BODY)
            }
        }
        harness.signIn(accessToken = "access-1", refreshToken = "refresh-1", ttl = 1.minutes)

        harness.api.ride.getRideState("01RIDE")

        assertEquals(2, harness.requests.size, "the refresh happened first, so the call never 401'd")
        assertEquals(REFRESH_PATH, harness.requests[0].path)
        assertEquals("Bearer access-2", harness.requests[1].authorization)
    }

    @Test
    fun a_healthy_access_token_is_not_rotated() = runTest {
        val harness = authHarness { _, _ -> respondJson(RIDE_STATE_BODY) }
        harness.signIn(accessToken = "access-1", ttl = 25.minutes)

        harness.api.ride.getRideState("01RIDE")

        assertTrue(harness.requestsTo(REFRESH_PATH).isEmpty())
        assertEquals("Bearer access-1", harness.requests.single().authorization)
    }

    @Test
    fun a_transient_proactive_failure_backs_off_instead_of_refreshing_per_request() = runTest {
        // accessToken() runs on every attempt of every request. Without the cooldown a handset
        // with no network would drive one refresh round trip per call, forever.
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == REFRESH_PATH) {
                respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire)
            } else {
                respondJson(RIDE_STATE_BODY)
            }
        }
        harness.signIn(accessToken = "access-1", refreshToken = "refresh-1", ttl = 1.minutes)

        harness.api.ride.getRideState("01RIDE")
        harness.api.ride.getRideState("01RIDE")
        harness.api.ride.getRideState("01RIDE")

        // One refresh *attempt*, which the retry policy repeated because 503 is transient, and
        // then nothing until the cooldown expires.
        assertNotEquals(0, harness.requestsTo(REFRESH_PATH).size)
        assertTrue(
            harness.requestsTo(REFRESH_PATH).size <= MAX_RETRY_ATTEMPTS,
            "a second call must not start a second refresh inside the cooldown",
        )
        assertIs<SessionState.SignedIn>(harness.sessions.state.value, "a 503 is not a revocation")
    }

    private companion object {
        const val CONCURRENT_CALLERS = 5

        /** `RetryPolicy.maxAttempts` — one refresh *call* can still be sent three times. */
        const val MAX_RETRY_ATTEMPTS = 3
    }
}

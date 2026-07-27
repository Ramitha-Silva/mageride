package lk.mageride.shared.data.api

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.iam.RefreshSessionRequest
import lk.mageride.shared.data.models.ride.OtpAttempt
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull

/**
 * `401` handling: refresh once, replay once, then stop (D-29).
 *
 * The rule is C014's definition of done — "a 401 triggers exactly one refresh attempt and replays
 * the original request once" — but the mechanism is C013's, so it is asserted here.
 */
class AuthRefreshTest {

    @Test
    fun a_401_triggers_one_refresh_and_replays_the_request_once() = runTest {
        val tokens = FakeTokenProvider(initialToken = "stale", rotatedToken = "fresh")
        val test = testApi(tokens = tokens) { attempt, _ ->
            if (attempt == 0) {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            } else {
                respondJson(RIDE_DETAIL)
            }
        }

        val ride = test.api.ride.getRide("01RIDE")

        assertEquals("01RIDE", ride.rideId)
        assertEquals(1, tokens.refreshCalls)
        assertEquals(0, tokens.authenticationLostCalls)
        assertEquals(2, test.requests.size)
        assertEquals("Bearer stale", test.requests[0].authorization)
        assertEquals("Bearer fresh", test.requests[1].authorization, "the replay must use the rotated token")
    }

    @Test
    fun the_replay_reuses_the_original_idempotency_key() = runTest {
        // A refresh must not turn one command into two. The key is minted before the first send
        // and the same request builder is replayed, so the service replays its recorded response
        // (R-14) instead of applying the mutation twice.
        val keys = SequentialIdempotencyKeys()
        val test = testApi(tokens = FakeTokenProvider(), idempotencyKeys = keys) { attempt, _ ->
            if (attempt == 0) {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            } else {
                respondJson(RIDE_STATE_CHANGE)
            }
        }

        test.api.ride.verifyPackagePickupOtp("01RIDE", OtpAttempt("123456"))

        assertEquals(2, test.requests.size)
        assertEquals(1, keys.count, "exactly one key should have been minted")
        assertEquals(test.requests[0].idempotencyKey, test.requests[1].idempotencyKey)
    }

    @Test
    fun a_second_401_after_a_successful_refresh_ends_the_session() = runTest {
        val tokens = FakeTokenProvider()
        val test = testApi(tokens = tokens) { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }

        assertFailsWith<MageRideError.Unauthorized> { test.api.ride.getRide("01RIDE") }

        assertEquals(1, tokens.refreshCalls, "the credential is not the problem; do not keep refreshing")
        assertEquals(1, tokens.authenticationLostCalls)
        assertEquals(2, test.requests.size)
    }

    @Test
    fun a_failed_refresh_ends_the_session_without_a_replay() = runTest {
        val tokens = FakeTokenProvider(refreshSucceeds = false)
        val test = testApi(tokens = tokens) { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }

        assertFailsWith<MageRideError.Unauthorized> { test.api.ride.getRide("01RIDE") }

        assertEquals(1, tokens.refreshCalls)
        assertEquals(1, tokens.authenticationLostCalls)
        assertEquals(1, test.requests.size, "there is nothing to replay with")
    }

    @Test
    fun refreshing_the_session_itself_is_never_refreshed() = runTest {
        // POST /v1/auth/refresh presents the opaque refresh token as its own credential. Treating
        // its 401 as "try a refresh" would recurse, and every attempt spends a single-use token.
        val tokens = FakeTokenProvider()
        val test = testApi(tokens = tokens) { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }

        assertFailsWith<MageRideError.Unauthorized> {
            test.api.iam.refreshSession(RefreshSessionRequest(refreshToken = "opaque-refresh"))
        }

        assertEquals(0, tokens.refreshCalls)
        assertEquals(1, test.requests.size)
        assertEquals("Bearer opaque-refresh", test.requests.single().authorization)
    }

    @Test
    fun a_401_on_a_public_route_is_not_treated_as_a_session_problem() = runTest {
        val tokens = FakeTokenProvider()
        val test = testApi(tokens = tokens) { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }

        assertFailsWith<MageRideError.Unauthorized> { test.api.version.checkAppVersion() }

        assertEquals(0, tokens.refreshCalls)
        assertEquals(0, tokens.authenticationLostCalls)
    }

    @Test
    fun an_anonymous_client_sends_no_authorization_header_at_all() = runTest {
        val test = testApi { _, _ -> respondJson(RIDE_DETAIL) }

        test.api.ride.getRide("01RIDE")

        assertNull(test.requests.single().authorization)
    }

    @Test
    fun an_attestation_failure_is_a_distinct_error_from_a_credential_failure() = runTest {
        // Both are 401 (D-30), but one means "re-attest" and the other means "sign in again".
        val tokens = FakeTokenProvider()
        val test = testApi(tokens = tokens) { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.ATTESTATION_FAILED.wire)
        }

        val error = assertFailsWith<MageRideError.AttestationFailed> { test.api.ride.getRide("01RIDE") }

        assertEquals(ErrorCode.ATTESTATION_FAILED, error.code)
    }

    private companion object {
        const val RIDE_DETAIL = """
            {"rideId":"01RIDE","kind":"passenger","state":"Accepted","version":3,
             "pickup":{"lat":6.9271,"lng":79.8612},"dropoff":{"lat":7.2906,"lng":80.6337},
             "vehicleType":"three_wheeler","paymentMethod":"cash","createdAt":"2026-07-27T04:15:00Z"}
        """
        const val RIDE_STATE_CHANGE = """{"rideId":"01RIDE","state":"InProgress","version":4}"""
    }
}

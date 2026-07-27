package lk.mageride.shared.domain.auth

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.respondJson
import lk.mageride.shared.data.api.respondNoContent
import lk.mageride.shared.data.api.respondProblem
import lk.mageride.shared.data.models.ErrorCode
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.ExperimentalTime

private const val RIDE_STATE_BODY = """{"state":"Requested","version":1}"""

/**
 * The second definition-of-done line: *"a revoked-session response clears local state and routes
 * the app to login."*
 *
 * And the rule that makes it survivable in the field: **offline is not revoked**. A driver whose
 * handset loses signal mid-ride must not come out of the tunnel signed out.
 */
@OptIn(ExperimentalTime::class)
class SessionRevocationTest {

    @Test
    fun a_refused_refresh_clears_the_stored_tokens_and_routes_to_login() = runTest {
        val harness = authHarness { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }
        harness.signIn()
        harness.store.saveMqttToken(
            MqttSessionToken("mqtt-jwt", harness.clock(), "01VEH", TEST_DEVICE_ID, "01RIDE"),
        )

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertEquals(SessionState.SignedOut(SignedOutReason.SESSION_REVOKED), harness.sessions.state.value)
        assertEquals(SessionEvent.RouteToLogin(SignedOutReason.SESSION_REVOKED), harness.sessions.events.first())
        assertFalse(harness.hasStoredSession(), "the token pair must be gone")
        assertFalse(harness.hasStoredMqttToken(), "the MQTT token belongs to the dead session")
    }

    @Test
    fun the_device_id_survives_a_revocation() = runTest {
        // `iam.yaml` calls deviceId a *per-install* identifier and AL-08's "new device" test is
        // meant to fire when the handset changes, not when a session ends.
        val harness = authHarness { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }
        harness.signIn()

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertEquals(TEST_DEVICE_ID, harness.sessions.deviceId())
        assertEquals(0, harness.secure.clears, "a revocation is not a PDPA erasure")
    }

    @Test
    fun a_403_device_revoked_says_the_user_signed_in_elsewhere() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == REFRESH_PATH) {
                respondProblem(HttpStatusCode.Forbidden, "device-revoked")
            } else {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            }
        }
        harness.signIn()

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertEquals(SessionState.SignedOut(SignedOutReason.SIGNED_IN_ELSEWHERE), harness.sessions.state.value)
    }

    @Test
    fun a_401_that_survives_a_successful_refresh_ends_the_session() = runTest {
        // The credential is demonstrably fresh and the server still says no — that is a revoked
        // session, not a stale token.
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == REFRESH_PATH) {
                respondJson(tokenPairJson("access-2", "refresh-2"))
            } else {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            }
        }
        harness.signIn()

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertEquals(SessionState.SignedOut(SignedOutReason.SESSION_REVOKED), harness.sessions.state.value)
        assertFalse(harness.hasStoredSession())
    }

    @Test
    fun a_refresh_that_cannot_reach_the_server_does_not_sign_anyone_out() = runTest {
        // The 401 says "this token is not accepted"; the 503 on the way to finding out why says
        // nothing about the token at all. Ending the session here is how a driver in a tunnel
        // gets logged out mid-ride.
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == REFRESH_PATH) {
                respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire)
            } else {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            }
        }
        harness.signIn()

        // The caller still learns its own call failed.
        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertIs<SessionState.SignedIn>(harness.sessions.state.value)
        assertTrue(harness.hasStoredSession(), "the tokens are still good; the network is not")
    }

    @Test
    fun logout_clears_local_state_even_when_the_call_fails() = runTest {
        val harness = authHarness { _, _ ->
            respondProblem(HttpStatusCode.ServiceUnavailable, ErrorCode.SERVICE_UNAVAILABLE.wire)
        }
        harness.signIn()

        harness.sessions.logout()

        assertEquals(SessionState.SignedOut(SignedOutReason.SIGNED_OUT), harness.sessions.state.value)
        assertEquals(SessionEvent.RouteToLogin(SignedOutReason.SIGNED_OUT), harness.sessions.events.first())
        assertFalse(harness.hasStoredSession())
    }

    @Test
    fun logout_revokes_the_refresh_token_server_side() = runTest {
        val harness = authHarness { _, _ -> respondNoContent() }
        harness.signIn()

        harness.sessions.logout()

        assertEquals("/v1/auth/logout", harness.requests.single().path)
        assertFalse(harness.hasStoredSession())
    }

    @Test
    fun deleting_the_account_erases_the_whole_namespace() = runTest {
        // PDPA erasure is the one path that takes the device id too — after it the install is
        // indistinguishable from a fresh one (`mobile_db_schema.md` §0.4).
        val harness = authHarness { _, _ -> respondJson("""{"requestId":"01JPDPAREQUEST"}""") }
        harness.signIn()

        val requestId = harness.sessions.deleteAccount()

        assertEquals("01JPDPAREQUEST", requestId)
        assertEquals(SessionState.SignedOut(SignedOutReason.ACCOUNT_DELETED), harness.sessions.state.value)
        assertEquals(1, harness.secure.clears)
        assertTrue(harness.secure.values.isEmpty())
    }

    @Test
    fun an_unauthenticated_call_that_401s_does_not_publish_a_route_to_login() = runTest {
        // Nothing to revoke, so nothing to announce. A login screen that received RouteToLogin
        // while already on the login screen would rebuild its own back stack.
        val harness = authHarness { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }
        harness.sessions.restore()

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState("01RIDE") }

        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
        assertEquals(1, harness.requests.size, "no session means no refresh to attempt")
    }

    @Test
    fun a_restored_session_is_usable_without_a_round_trip() = runTest {
        val harness = authHarness { _, _ -> respondJson(RIDE_STATE_BODY) }
        harness.signIn(accessToken = "access-1")

        val state = harness.sessions.state.value

        assertIs<SessionState.SignedIn>(state)
        assertEquals(TEST_USER_ID, state.userId)
        assertFalse(state.isNewUser, "a relaunch is not a new account")
        harness.api.ride.getRideState("01RIDE")
        assertEquals("Bearer access-1", harness.requests.single().authorization)
    }
}

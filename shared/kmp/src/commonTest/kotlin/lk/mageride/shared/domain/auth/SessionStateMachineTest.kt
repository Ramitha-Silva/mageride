package lk.mageride.shared.domain.auth

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.respondJson
import lk.mageride.shared.data.api.respondProblem
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.ErrorCode
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

private const val OTP_REQUEST_PATH = "/v1/auth/otp/request"
private const val OTP_VERIFY_PATH = "/v1/auth/otp/verify"
private const val OTP_RESEND_PATH = "/v1/auth/otp/resend"
private const val OTP_REQUEST_BODY =
    """{"authId":"01JAUTHATTEMPT","attemptsRemaining":5,"cooldownSeconds":60,"isBlocked":false}"""

/**
 * The phone-OTP sign-in flow and the states around it (D5' §14.1/§14.2, AL-07, AL-08).
 *
 * Apps are **Phone OTP only**: the Google, Apple and password routes `IamApi` exposes belong to
 * the portals, and nothing in `domain/auth` calls them — `PlatformSecurityHygieneTest` asserts that
 * against the source rather than trusting it.
 */
@OptIn(ExperimentalTime::class, ExperimentalCoroutinesApi::class)
class SessionStateMachineTest {

    @Test
    fun a_fresh_install_restores_to_signed_out() = runTest {
        val harness = authHarness { _, _ -> respondJson(OTP_REQUEST_BODY) }

        assertEquals(SessionState.Loading, harness.sessions.state.value)
        harness.sessions.restore()

        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
    }

    @Test
    fun requesting_an_otp_sends_the_device_id_and_the_app_surface() = runTest {
        val harness = authHarness { _, _ -> respondJson(OTP_REQUEST_BODY) }
        harness.sessions.restore()

        val challenge = harness.sessions.requestOtp("+94771234567", fcmToken = "fcm-1")

        val sent = harness.requests.single()
        assertEquals(OTP_REQUEST_PATH, sent.path)
        // AL-08 scopes single-active-device by (user, app); the server cannot apply it without both.
        assertTrue(sent.body.contains(""""deviceId":"$TEST_DEVICE_ID""""))
        assertTrue(sent.body.contains(""""role":"driver""""))
        assertEquals("01JAUTHATTEMPT", challenge.authId)
        assertEquals(SessionState.AwaitingOtp(challenge), harness.sessions.state.value)
    }

    @Test
    fun verifying_the_otp_stores_the_session_and_signs_in() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == OTP_VERIFY_PATH) {
                respondJson(verifyOtpJson(isNewUser = true))
            } else {
                respondJson(OTP_REQUEST_BODY)
            }
        }
        harness.sessions.restore()
        harness.sessions.requestOtp("+94771234567")

        val signedIn = harness.sessions.verifyOtp("123456")

        assertTrue(signedIn.isNewUser, "a created account routes to onboarding")
        assertEquals(TEST_USER_ID, signedIn.userId)
        assertEquals(AppSurface.DRIVER, signedIn.app)
        val stored = assertNotNull(harness.store.loadSession())
        assertEquals("refresh-1", stored.refreshToken)
        assertEquals(harness.clock() + TokenLifetimes.ACCESS_SECONDS.seconds, stored.accessTokenExpiresAt)
    }

    @Test
    fun a_wrong_code_keeps_the_attempt_alive_and_counts_down() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == OTP_VERIFY_PATH) {
                respondProblem(HttpStatusCode.Unauthorized, ErrorCode.INVALID_OTP.wire)
            } else {
                respondJson(OTP_REQUEST_BODY)
            }
        }
        harness.sessions.restore()
        harness.sessions.requestOtp("+94771234567")

        assertFailsWith<MageRideError.Unauthorized> { harness.sessions.verifyOtp("000000") }

        val state = assertIs<SessionState.AwaitingOtp>(harness.sessions.state.value)
        assertEquals(4, state.challenge.attemptsRemaining)
        // Never refreshed: the verify route is public, so a 401 there is a wrong code, not a
        // stale credential.
        assertTrue(harness.requestsTo(REFRESH_PATH).isEmpty())
    }

    @Test
    fun a_locked_attempt_returns_to_the_login_screen() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == OTP_VERIFY_PATH) {
                respondProblem(HttpStatusCode.Locked, ErrorCode.OTP_LOCKED.wire)
            } else {
                respondJson(OTP_REQUEST_BODY)
            }
        }
        harness.sessions.restore()
        harness.sessions.requestOtp("+94771234567")

        assertFailsWith<MageRideError.Locked> { harness.sessions.verifyOtp("000000") }

        // That authId can never succeed again, so keeping the code screen up would be a dead end.
        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
    }

    @Test
    fun an_expired_code_returns_to_the_login_screen() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == OTP_VERIFY_PATH) {
                respondProblem(HttpStatusCode.BadRequest, ErrorCode.OTP_EXPIRED.wire)
            } else {
                respondJson(OTP_REQUEST_BODY)
            }
        }
        harness.sessions.restore()
        harness.sessions.requestOtp("+94771234567")

        assertFailsWith<MageRideError.BadRequest> { harness.sessions.verifyOtp("123456") }

        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
    }

    @Test
    fun a_resend_inside_the_cooldown_never_reaches_the_network() = runTest {
        // D-32 allows five OTPs an hour. Spending one on a button the screen should have
        // disabled is a user locked out by their own app.
        val harness = authHarness { _, _ -> respondJson(OTP_REQUEST_BODY) }
        harness.sessions.restore()
        harness.sessions.requestOtp("+94771234567")

        assertFailsWith<IllegalStateException> { harness.sessions.resendOtp() }

        assertEquals(1, harness.requests.size)
        assertTrue(harness.requestsTo(OTP_RESEND_PATH).isEmpty())
    }

    @Test
    fun a_resend_after_the_cooldown_extends_the_window() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == OTP_RESEND_PATH) {
                respondJson("""{"attemptsRemaining":4,"cooldownSeconds":60}""")
            } else {
                respondJson(OTP_REQUEST_BODY)
            }
        }
        harness.sessions.restore()
        val first = harness.sessions.requestOtp("+94771234567")

        advanceTimeBy(61.seconds)
        val second = harness.sessions.resendOtp()

        assertEquals(4, second.attemptsRemaining)
        assertEquals(first.authId, second.authId, "a resend is the same attempt, not a new one")
        assertTrue(second.resendAllowedAt > first.resendAllowedAt)
    }

    @Test
    fun signing_in_as_a_different_user_wipes_what_the_previous_one_left() = runTest {
        val harness = authHarness { _, request ->
            if (request.url.encodedPath == OTP_VERIFY_PATH) {
                respondJson(verifyOtpJson(access = "access-2", refresh = "refresh-2", userId = "01JOTHERUSER"))
            } else {
                respondJson(OTP_REQUEST_BODY)
            }
        }
        harness.signIn(accessToken = "access-1", refreshToken = "refresh-1")
        harness.store.saveMqttToken(
            MqttSessionToken("mqtt-jwt", harness.clock(), "01VEH", TEST_DEVICE_ID, "01RIDE"),
        )
        harness.sessions.requestOtp("+94770000000")

        harness.sessions.verifyOtp("123456")

        val stored = assertNotNull(harness.store.loadSession())
        assertEquals("01JOTHERUSER", stored.userId)
        assertEquals("refresh-2", stored.refreshToken)
        assertFalse(harness.hasStoredMqttToken(), "the old MQTT token is bound to the old session (E-02)")
    }

    @Test
    fun cancelling_an_attempt_goes_back_to_the_login_screen() = runTest {
        val harness = authHarness { _, _ -> respondJson(OTP_REQUEST_BODY) }
        harness.sessions.restore()
        harness.sessions.requestOtp("+94771234567")

        harness.sessions.cancelOtp()

        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
    }

    @Test
    fun verifying_without_an_attempt_in_flight_is_a_programming_error() = runTest {
        val harness = authHarness { _, _ -> respondJson(OTP_REQUEST_BODY) }
        harness.sessions.restore()

        assertFailsWith<IllegalStateException> { harness.sessions.verifyOtp("123456") }
        assertTrue(harness.requests.isEmpty())
    }

    @Test
    fun a_blocked_number_is_reported_rather_than_hidden() = runTest {
        val harness = authHarness { _, _ ->
            respondJson("""{"authId":"01JAUTHATTEMPT","attemptsRemaining":0,"cooldownSeconds":60,"isBlocked":true}""")
        }
        harness.sessions.restore()

        val challenge = harness.sessions.requestOtp("+94771234567")

        assertTrue(challenge.isBlocked, "the server answers 200 so the screen has to read the flag")
    }

    @Test
    fun a_session_stored_by_the_other_surface_is_discarded() = runTest {
        // Belt and braces on top of the namespaced store key: a driver build must never resume a
        // passenger session, because the `app` claim is what AL-08 scopes revocation by.
        val harness = authHarness(config = AuthConfig(app = AppSurface.DRIVER)) { _, _ ->
            respondJson(OTP_REQUEST_BODY)
        }
        harness.store.saveSession(
            AuthSession(
                userId = TEST_USER_ID,
                app = AppSurface.PASSENGER,
                deviceId = TEST_DEVICE_ID,
                accessToken = "access-1",
                accessTokenExpiresAt = harness.clock(),
                refreshToken = "refresh-1",
            ),
        )

        harness.sessions.restore()

        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
        assertFalse(harness.hasStoredSession())
    }

    @Test
    fun a_store_written_by_an_older_build_does_not_wedge_the_cold_start() = runTest {
        val harness = authHarness { _, _ -> respondJson(OTP_REQUEST_BODY) }
        harness.secure.values[harness.config.storeKey("session")] = """{"shape":"from an older release"}"""

        harness.sessions.restore()

        assertEquals(SessionState.SignedOut(SignedOutReason.NEVER_SIGNED_IN), harness.sessions.state.value)
        assertFalse(harness.hasStoredSession(), "the unreadable record is dropped, not re-read every launch")
    }

    private object TokenLifetimes {
        /** `_shared.yaml#/components/schemas/TokenPair` pins `expiresIn` at 1800 (D-29). */
        const val ACCESS_SECONDS = 1800
    }
}

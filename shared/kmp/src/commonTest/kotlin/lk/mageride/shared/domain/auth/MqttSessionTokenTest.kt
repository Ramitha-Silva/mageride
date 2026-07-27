package lk.mageride.shared.domain.auth

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.respondJson
import lk.mageride.shared.data.api.respondProblem
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.data.models.iam.IssueMqttTokenRequest
import lk.mageride.shared.data.models.iam.IssueMqttTokenResponse
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime

private const val MQTT_PATH = "/v1/auth/mqtt-token"
private const val VEHICLE_ID = "01JVEHICLETESTTESTTESTTEST"
private const val RIDE_ID = "01JRIDETESTTESTTESTTESTTES"
private const val FOUR_HOURS_SECONDS = 14400

/**
 * The third definition-of-done line: *"the MQTT token is renewed before expiry while a ride is
 * active, independent of API-token failures."*
 *
 * E-02 exists because the 30-minute API token expires mid-trip in low coverage. A test that let
 * an API failure take the MQTT token down with it would be testing the bug the decision record
 * was written to prevent.
 *
 * **Why the renewal tests use a stubbed `issueMqttToken`.** Everything about renewal is timing:
 * the loop sleeps on `delay`, and expiry is measured against a clock driven by the same virtual
 * scheduler. Ktor's engine runs a request on its own dispatcher, off that scheduler, so a loop
 * that made real calls would advance virtual time and real time independently and the assertions
 * would race. The stub delegates every other operation to the real client, so the first test here
 * still proves the wire shape end to end.
 */
@OptIn(ExperimentalTime::class, ExperimentalCoroutinesApi::class)
class MqttSessionTokenTest {

    /** The real client for everything except the one operation a timing test needs to control. */
    private class StubbedMqttIam(delegate: IamApi, private val answer: (Int) -> IssueMqttTokenResponse) :
        IamApi by delegate {
        val requests: MutableList<IssueMqttTokenRequest> = mutableListOf()

        override suspend fun issueMqttToken(
            request: IssueMqttTokenRequest,
            idempotencyKey: String?,
        ): IssueMqttTokenResponse {
            requests += request
            return answer(requests.size)
        }
    }

    private fun serviceUnavailable(): Nothing = throw MageRideError.of(
        ProblemDetails(
            type = ErrorCode.SERVICE_UNAVAILABLE.typeUri,
            title = "Service Unavailable",
            status = HttpStatusCode.ServiceUnavailable.value,
        ),
    )

    @Test
    fun binding_mints_a_token_for_the_vehicle_the_device_and_the_ride() = runTest {
        val harness = authHarness { _, _ -> respondJson(mqttTokenJson("mqtt-1")) }
        harness.signIn(ttl = 24.hours)

        val token = harness.mqtt(backgroundScope).bind(VEHICLE_ID, RIDE_ID)

        val sent = harness.requests.single()
        assertEquals(MQTT_PATH, sent.path)
        assertTrue(sent.body.contains(""""vehicleId":"$VEHICLE_ID""""))
        assertTrue(sent.body.contains(""""deviceId":"$TEST_DEVICE_ID""""))
        assertTrue(sent.body.contains(""""rideId":"$RIDE_ID""""))
        assertEquals("mqtt-1", token.jwt)
        assertEquals(TEST_EPOCH + 4.hours, token.expiresAt, "the contract's four-hour floor")
    }

    @Test
    fun the_token_is_renewed_before_it_expires() = runTest {
        val harness = authHarness { _, _ -> respondJson("{}") }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { n -> IssueMqttTokenResponse("mqtt-$n", FOUR_HOURS_SECONDS) }
        val mqtt = harness.mqtt(backgroundScope, iam)
        mqtt.bind(VEHICLE_ID, RIDE_ID)

        advanceTimeBy(3.hours + 49.minutes)
        runCurrent()
        assertEquals("mqtt-1", mqtt.token.value?.jwt, "renewal starts 10 minutes out, not 11")

        advanceTimeBy(2.minutes)
        runCurrent()

        assertEquals("mqtt-2", mqtt.token.value?.jwt)
        assertTrue(harness.clock() < TEST_EPOCH + 4.hours, "renewed while the first token was still valid")
    }

    @Test
    fun a_failed_renewal_keeps_the_live_token_and_tries_again() = runTest {
        val harness = authHarness { _, _ -> respondJson("{}") }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { n ->
            // The first mint succeeds; the next three renewals fail; the fifth recovers.
            if (n in 2..4) serviceUnavailable() else IssueMqttTokenResponse("mqtt-$n", FOUR_HOURS_SECONDS)
        }
        val mqtt = harness.mqtt(backgroundScope, iam)
        mqtt.bind(VEHICLE_ID, RIDE_ID)

        advanceTimeBy(3.hours + 51.minutes)
        runCurrent()
        // Renewal has already failed and the token it holds is still good for another nine
        // minutes. Dropping it here is what would take a live ride dark.
        assertEquals("mqtt-1", mqtt.token.value?.jwt)
        assertTrue(iam.requests.size > 1, "the loop is retrying")

        advanceTimeBy(5.minutes)
        runCurrent()

        assertEquals("mqtt-5", mqtt.token.value?.jwt, "the loop kept retrying and recovered")
        assertTrue(harness.clock() < TEST_EPOCH + 4.hours, "and it recovered before the old token died")
    }

    @Test
    fun an_api_token_failure_does_not_take_the_mqtt_token_with_it() = runTest {
        val harness = authHarness { _, request ->
            when (request.url.encodedPath) {
                REFRESH_PATH -> respondProblem(
                    HttpStatusCode.ServiceUnavailable,
                    ErrorCode.SERVICE_UNAVAILABLE.wire,
                )

                else -> respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
            }
        }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { IssueMqttTokenResponse("mqtt-1", FOUR_HOURS_SECONDS) }
        val mqtt = harness.mqtt(backgroundScope, iam)
        mqtt.bind(VEHICLE_ID, RIDE_ID)

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState(RIDE_ID) }
        runCurrent()

        assertIs<SessionState.SignedIn>(harness.sessions.state.value)
        assertEquals("mqtt-1", mqtt.token.value?.jwt, "a 401 on a REST call is not this token's problem")
    }

    @Test
    fun a_revoked_session_releases_the_mqtt_token() = runTest {
        val harness = authHarness { _, _ ->
            respondProblem(HttpStatusCode.Unauthorized, ErrorCode.UNAUTHORIZED.wire)
        }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { IssueMqttTokenResponse("mqtt-1", FOUR_HOURS_SECONDS) }
        val mqtt = harness.mqtt(backgroundScope, iam)
        mqtt.bind(VEHICLE_ID, RIDE_ID)

        assertFailsWith<MageRideError.Unauthorized> { harness.api.ride.getRideState(RIDE_ID) }
        runCurrent()

        assertEquals(SessionState.SignedOut(SignedOutReason.SESSION_REVOKED), harness.sessions.state.value)
        assertNull(mqtt.token.value, "no session, no publishing")
        assertTrue(harness.secure.values.values.none { "mqtt-1" in it })
    }

    @Test
    fun a_new_ride_gets_a_new_token() = runTest {
        // The ride id is what extends the TTL past four hours (E-02), so reusing a token minted
        // for the previous ride would silently give the new one the floor instead.
        val harness = authHarness { _, _ -> respondJson("{}") }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { n -> IssueMqttTokenResponse("mqtt-$n", FOUR_HOURS_SECONDS) }
        val mqtt = harness.mqtt(backgroundScope, iam)

        mqtt.bind(VEHICLE_ID, RIDE_ID)
        val second = mqtt.bind(VEHICLE_ID, "01JOTHERRIDE")

        assertEquals("mqtt-2", second.jwt)
        assertEquals("01JOTHERRIDE", second.rideId)
        assertEquals(2, iam.requests.size)
    }

    @Test
    fun rebinding_to_the_same_ride_reuses_the_live_token() = runTest {
        val harness = authHarness { _, _ -> respondJson("{}") }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { n -> IssueMqttTokenResponse("mqtt-$n", FOUR_HOURS_SECONDS) }
        val mqtt = harness.mqtt(backgroundScope, iam)

        mqtt.bind(VEHICLE_ID, RIDE_ID)
        val again = mqtt.bind(VEHICLE_ID, RIDE_ID)

        assertEquals("mqtt-1", again.jwt)
        assertEquals(1, iam.requests.size, "an idempotent bind is not a second mint")
    }

    @Test
    fun a_persisted_token_survives_a_restart() = runTest {
        // A driver app relaunched mid-ride must be able to publish before its first round trip.
        val harness = authHarness { _, _ -> respondJson("{}") }
        harness.signIn(ttl = 24.hours)
        harness.store.saveMqttToken(
            MqttSessionToken(
                jwt = "mqtt-persisted",
                expiresAt = harness.clock() + 4.hours,
                vehicleId = VEHICLE_ID,
                deviceId = TEST_DEVICE_ID,
                rideId = RIDE_ID,
            ),
        )
        val iam = StubbedMqttIam(harness.api.iam) { IssueMqttTokenResponse("mqtt-fresh", FOUR_HOURS_SECONDS) }

        val restored = harness.mqtt(backgroundScope, iam).bind(VEHICLE_ID, RIDE_ID)

        assertEquals("mqtt-persisted", restored.jwt)
        assertTrue(iam.requests.isEmpty())
    }

    @Test
    fun releasing_stops_the_renewal_loop_and_forgets_the_token() = runTest {
        val harness = authHarness { _, _ -> respondJson("{}") }
        harness.signIn(ttl = 24.hours)
        val iam = StubbedMqttIam(harness.api.iam) { n -> IssueMqttTokenResponse("mqtt-$n", FOUR_HOURS_SECONDS) }
        val mqtt = harness.mqtt(backgroundScope, iam)
        mqtt.bind(VEHICLE_ID, RIDE_ID)

        mqtt.release()
        advanceTimeBy(5.hours)
        runCurrent()

        assertNull(mqtt.token.value)
        assertEquals(1, iam.requests.size, "a released binding is not renewed")
        assertNull(harness.store.loadMqttToken())
    }
}

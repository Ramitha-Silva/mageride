package lk.mageride.shared.data.api

import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.ProviderCallbackStatus
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.ProviderCallback
import lk.mageride.shared.data.models.iam.RequestOtpRequest
import lk.mageride.shared.data.models.ride.OtpAttempt
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The conventions D3' §0 applies to **every** endpoint, asserted once instead of 176 times.
 *
 * If one of these breaks, every call in the platform is wrong, which is exactly why they belong
 * in the transport rather than in each client.
 */
class RequestConventionsTest {

    @Test
    fun every_request_carries_the_version_gate_headers() = runTest {
        val test = testApi { _, _ -> respondJson("""{"registered":false}""") }

        test.api.iam.lookupUserByPhone("+94771234567")

        val request = test.requests.single()
        assertEquals(TEST_APP_VERSION, request.headers[MageRideHeaders.APP_VERSION])
        assertEquals("android", request.headers[MageRideHeaders.PLATFORM])
    }

    @Test
    fun the_contract_path_is_appended_to_the_configured_origin() = runTest {
        val test = testApi { _, _ -> respondJson("""{"items":[],"cursor":null,"hasMore":false}""") }

        test.api.ride.listRideHistory(PageRequest(cursor = "abc", limit = 50))

        val request = test.requests.single()
        assertEquals("/v1/rides/history", request.path)
        assertEquals("abc", request.query["cursor"])
        assertEquals("50", request.query["limit"])
    }

    @Test
    fun an_absent_page_parameter_is_omitted_rather_than_sent_empty() = runTest {
        val test = testApi { _, _ -> respondJson("""{"items":[],"cursor":null,"hasMore":false}""") }

        test.api.ride.listRideHistory()

        val request = test.requests.single()
        assertNull(request.query["cursor"])
        assertNull(request.query["limit"])
    }

    @Test
    fun every_post_mutation_carries_an_idempotency_key() = runTest {
        val test = testApi { _, _ -> respondJson(RIDE_STATE_CHANGE) }

        test.api.ride.verifyPackagePickupOtp("01RIDE", OtpAttempt(otp = "123456"))

        val key = test.requests.single().idempotencyKey
        assertTrue(key != null && key.length >= MIN_KEY_LENGTH, "expected a minted key, got $key")
    }

    @Test
    fun a_caller_supplied_idempotency_key_is_used_verbatim() = runTest {
        val test = testApi { _, _ -> respondJson(RIDE_STATE_CHANGE) }

        test.api.ride.verifyPackagePickupOtp("01RIDE", OtpAttempt("123456"), idempotencyKey = "MY-OWN-REPLAY-KEY-01")

        assertEquals("MY-OWN-REPLAY-KEY-01", test.requests.single().idempotencyKey)
    }

    @Test
    fun a_get_carries_no_idempotency_key() = runTest {
        val test = testApi { _, _ -> respondJson("""{"state":"Requested","version":1}""") }

        test.api.ride.getRideState("01RIDE")

        assertNull(test.requests.single().idempotencyKey)
    }

    @Test
    fun an_idempotency_exempt_provider_callback_carries_no_key() = runTest {
        // The six HMAC-signed callbacks dedupe on provider_transaction_id (R-19); sending our
        // header would imply a guarantee the gateway does not make for them.
        val test = testApi { _, _ -> respondJson("""{"received":true}""") }

        test.api.fare.onepayPaymentWebhook(
            ProviderCallback(providerTransactionId = "OP-1", status = ProviderCallbackStatus.SUCCESS),
        )

        assertNull(test.requests.single().idempotencyKey)
    }

    @Test
    fun a_sensitive_mutation_carries_the_attestation_header() = runTest {
        val test = testApi(
            attestation = AttestationProvider { operationId -> "verdict-for-$operationId" },
        ) { _, _ -> respondJson("""{"authId":"01A","attemptsRemaining":4,"cooldownSeconds":60,"isBlocked":false}""") }

        test.api.iam.requestOtp(RequestOtpRequest(phone = "+94771234567", deviceId = "device-1"))

        assertEquals("verdict-for-requestOtp", test.requests.single().headers[MageRideHeaders.ATTESTATION])
    }

    @Test
    fun an_ordinary_call_carries_no_attestation_header() = runTest {
        val test = testApi(
            attestation = AttestationProvider { "should-not-be-sent" },
        ) { _, _ -> respondJson("""{"state":"Requested","version":1}""") }

        test.api.ride.getRideState("01RIDE")

        assertNull(test.requests.single().headers[MageRideHeaders.ATTESTATION])
    }

    @Test
    fun a_bearer_token_is_attached_when_the_operation_declares_bearer_auth() = runTest {
        val test = testApi(tokens = FakeTokenProvider(initialToken = "jwt-abc")) { _, _ ->
            respondJson("""{"state":"Requested","version":1}""")
        }

        test.api.ride.getRideState("01RIDE")

        assertEquals("Bearer jwt-abc", test.requests.single().authorization)
    }

    @Test
    fun a_public_route_sends_no_bearer_token_even_when_one_exists() = runTest {
        // `security: []` in the contract is deny-by-default's opposite stated explicitly; sending
        // a session token to a token-scoped route would widen what the response may contain.
        val test = testApi(tokens = FakeTokenProvider(initialToken = "jwt-abc")) { _, _ ->
            respondJson("""{"updateRequired":false,"latestVersion":"1.4.0","updateUrl":"u","isMandatory":false}""")
        }

        test.api.version.checkAppVersion()

        assertNull(test.requests.single().authorization)
    }

    @Test
    fun the_version_check_defaults_to_the_configured_build_and_platform() = runTest {
        val test = testApi { _, _ ->
            respondJson("""{"updateRequired":false,"latestVersion":"1.4.0","updateUrl":"u","isMandatory":false}""")
        }

        test.api.version.checkAppVersion()

        val request = test.requests.single()
        assertEquals("android", request.query["platform"])
        assertEquals(TEST_APP_VERSION, request.query["current"])
    }

    @Test
    fun an_enum_query_parameter_is_sent_as_its_wire_spelling() = runTest {
        val test = testApi { _, _ -> respondJson(FARE_ESTIMATE) }

        test.api.fare.estimateFare(
            fromLat = 6.9271,
            fromLng = 79.8612,
            toLat = 7.2906,
            toLng = 80.6337,
            vehicleType = RideVehicleType.THREE_WHEELER,
        )

        assertEquals("three_wheeler", test.requests.single().query["vehicleType"])
    }

    @Test
    fun a_json_body_is_serialised_with_the_platform_json() = runTest {
        val test = testApi { _, _ -> respondJson(RIDE_STATE_CHANGE) }

        test.api.ride.verifyPackagePickupOtp("01RIDE", OtpAttempt(otp = "424242"))

        assertEquals("""{"otp":"424242"}""", test.requests.single().body)
    }

    private companion object {
        const val MIN_KEY_LENGTH = 16
        const val RIDE_STATE_CHANGE = """{"rideId":"01RIDE","state":"InProgress","version":4}"""
        const val FARE_ESTIMATE = """
            {"fareEstimateToken":"tok","amountMinor":48000,"currency":"LKR",
             "breakdown":{"firstKmMinor":9000,"perKmMinor":6000,"distanceKm":6.5}}
        """
    }
}

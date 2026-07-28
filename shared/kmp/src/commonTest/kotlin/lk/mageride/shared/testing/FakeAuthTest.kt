package lk.mageride.shared.testing

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fake.FakeTokenProvider
import lk.mageride.shared.testing.fake.InMemorySecureStore
import lk.mageride.shared.testing.fake.RecordingAttestationProvider
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.scenario.ModeCRide
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The credential-side fakes, against the real send pipeline.
 *
 * These are the ones whose value is entirely in what they **count**. "Exactly one refresh, then
 * give up" and "the twenty D-30 operations attest and the rest do not" are arithmetic on calls, so
 * a fake that did not record its calls would leave both unassertable — and both are rules a client
 * gets wrong silently.
 */
class FakeAuthTest {

    @Test
    fun a_401_refreshes_once_and_replays_once() = runTest {
        val tokens = FakeTokenProvider()
        val backend = FakeApiBackend()
        backend.next("getMyProfile", FakeReply.problem(HttpStatusCode.Unauthorized, "unauthorized"))

        backend.mageRideApi(tokens = tokens).iam.getMyProfile()

        assertEquals(1, tokens.refreshCalls)
        assertEquals(0, tokens.authenticationLostCalls)
        assertEquals(listOf<String?>("access-1"), tokens.staleTokens, "the provider is told which token failed")
        assertEquals(2, backend.callsTo("getMyProfile").size, "the original request is replayed once")
        assertEquals("Bearer access-2", backend.lastCall("getMyProfile").authorization)
    }

    @Test
    fun a_second_401_ends_the_session_rather_than_refreshing_again() = runTest {
        val tokens = FakeTokenProvider()
        val backend = FakeApiBackend()
        backend.fails("getMyProfile", HttpStatusCode.Unauthorized, "unauthorized")

        assertFailsWith<MageRideError.Unauthorized> { backend.mageRideApi(tokens = tokens).iam.getMyProfile() }

        assertEquals(1, tokens.refreshCalls, "D-29: racing the single-use refresh token revokes the family")
        assertEquals(1, tokens.authenticationLostCalls)
    }

    @Test
    fun an_attested_operation_is_vouched_for_and_an_ordinary_one_is_not() = runTest {
        val attestation = RecordingAttestationProvider()
        val backend = FakeApiBackend()
        val api = backend.mageRideApi(attestation = attestation)

        api.ride.requestRide(ModeCRide.request)
        api.ride.getRide(ModeCRide.booked.rideId)

        assertEquals(listOf("requestRide"), attestation.operationIds, "D-30 is per operation, not per call")
        val request = attestation.requests.single()
        assertEquals("POST /v1/rides/request", request.clientData, "the gateway signs over method + path")
    }

    @Test
    fun a_build_that_cannot_attest_sends_no_header() = runTest {
        val backend = FakeApiBackend()
        backend.mageRideApi(attestation = RecordingAttestationProvider(token = null))
            .ride
            .requestRide(ModeCRide.request)

        assertNull(backend.lastCall("requestRide").headers["X-Attestation"])
    }

    @Test
    fun the_in_memory_secure_store_counts_the_wipe_that_logout_depends_on() = runTest {
        val store = InMemorySecureStore()
        store.write("refresh", "opaque-token")

        assertEquals("opaque-token", store.read("refresh"))
        store.clear()

        assertNull(store.read("refresh"))
        assertEquals(1, store.clears, "an emptied map does not distinguish 'cleared' from 'never written'")
        assertTrue(store.values.isEmpty())
    }
}

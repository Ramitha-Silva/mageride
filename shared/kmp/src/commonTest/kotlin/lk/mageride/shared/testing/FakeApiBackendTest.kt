package lk.mageride.shared.testing

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.AcceptRideOfferRequest
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.ApiOperations
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.testing.scenario.ModeCRide
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The C019 definition of done: *"every typed client has a fake with the same surface."*
 *
 * The reading taken here is the strong one. There is no second implementation of `RideApi` to keep
 * in step with the first — the clients under test are the production ones, and what is faked is
 * the *backend*. So "the same surface" is not a property somebody has to maintain; it is the same
 * sixteen interfaces, and the sweep below proves every one of their 189 operations answers with
 * something its own return type accepts.
 */
class FakeApiBackendTest {

    @Test
    fun the_table_covers_every_operation_of_every_service() {
        assertEquals(EXPECTED_OPERATIONS, ApiOperations.ALL.size)
        assertEquals(EXPECTED_OPERATIONS, ApiOperations.BY_ID.size, "operation ids must be unique")
        assertEquals(
            ApiService.entries.toSet(),
            ApiOperations.ALL.mapTo(mutableSetOf()) { it.service },
            "every typed client must be represented, including the ones an app never reaches",
        )
    }

    @Test
    fun every_operation_answers_with_a_body_its_own_return_type_decodes() {
        val backend = FakeApiBackend()
        val failures = ApiOperations.ALL.mapNotNull { operation ->
            val serializer = operation.response ?: return@mapNotNull null
            val body = backend.defaultBodyOf(operation.operationId)
                ?: return@mapNotNull "$operation served no body"
            runCatching { MageRideJson.decodeFromJsonElement(serializer, body) }
                .exceptionOrNull()
                ?.let { "$operation: ${it.message}" }
        }
        assertEquals(emptyList(), failures, "a synthesised response must decode into the DTO the client returns")
    }

    @Test
    fun the_bodiless_operations_are_exactly_the_ones_the_contracts_declare_bodiless() {
        val bodiless = ApiOperations.ALL.filterNot { it.hasBody }
        assertTrue(bodiless.all { it.status == 204 || it.status == 302 }, "$bodiless")
        assertEquals(
            listOf(302),
            bodiless.filter { it.operationId == "downloadGtfsFeed" }.map { it.status },
            "the GTFS download is a redirect whose payload is its Location header",
        )
    }

    @Test
    fun a_call_reaches_the_real_client_over_the_fake() = runTest {
        val backend = FakeApiBackend()
        val api = backend.mageRideApi()

        val ride = api.ride.getRide(Fixtures.RIDE_ID)
        val profile = api.iam.getMyProfile()
        val wallet = api.wallet.getWallet(Fixtures.PASSENGER_ID)
        val vehicle = api.registry.getVehicle(Fixtures.VEHICLE_ID)
        val fee = api.subscription.getTodaysDailyFee(Fixtures.DRIVER_ID)

        assertEquals(Fixtures.RIDE_ID, ride.rideId)
        assertNotNull(profile)
        assertNotNull(wallet)
        assertNotNull(vehicle)
        assertNotNull(fee)
        assertEquals(
            listOf("getRide", "getMyProfile", "getWallet", "getVehicle", "getTodaysDailyFee"),
            backend.calls.map { it.operationId },
        )
    }

    @Test
    fun the_recorded_call_carries_the_route_the_contract_declares() = runTest {
        val backend = FakeApiBackend()
        backend.mageRideApi().ride.getRide(Fixtures.RIDE_ID)

        val call = backend.lastCall("getRide")
        assertEquals("GET", call.method)
        assertEquals("/v1/rides/${Fixtures.RIDE_ID}", call.path)
        assertNull(call.idempotencyKey, "a GET carries no idempotency key")
    }

    @Test
    fun a_post_carries_one_idempotency_key_and_the_body_the_caller_built() = runTest {
        val backend = FakeApiBackend()
        val api = backend.mageRideApi()

        api.ride.requestRide(ModeCRide.request)

        val call = backend.lastCall("requestRide")
        assertEquals("POST", call.method)
        assertNotNull(call.idempotencyKey)
        assertEquals(Fixtures.FARE_ESTIMATE_TOKEN, call.json["fareEstimateToken"]?.toString()?.trim('"'))
    }

    @Test
    fun a_stub_replaces_the_default_and_a_queue_drains_in_order() = runTest {
        val backend = FakeApiBackend()
        backend.next(
            "getRideState",
            FakeReply.raw("""{"state":"Matching","version":2}"""),
            FakeReply.raw("""{"state":"Offered","version":3}"""),
        )
        backend.always("getRideState", FakeReply.raw("""{"state":"Accepted","version":4}"""))
        val api = backend.mageRideApi()

        assertEquals(
            listOf(RideState.Matching, RideState.Offered, RideState.Accepted, RideState.Accepted),
            List(4) { api.ride.getRideState(Fixtures.RIDE_ID).state },
            "the queue drains first, then the standing reply holds",
        )
    }

    @Test
    fun a_problem_reply_becomes_the_typed_error_the_client_documents() = runTest {
        val backend = FakeApiBackend()
        backend.fails("acceptRideOffer", HttpStatusCode.Gone, "offer-expired")
        val api = backend.mageRideApi()

        val error = assertFailsWith<MageRideError.Gone> {
            api.ride.acceptRideOffer(
                rideId = Fixtures.RIDE_ID,
                driverId = Fixtures.DRIVER_ID,
                request = AcceptRideOfferRequest(offerId = Fixtures.RIDE_ID, version = 3),
            )
        }
        assertEquals("offer-expired", error.problem.code)
    }

    @Test
    fun a_synthesised_page_is_complete_so_a_paging_loop_terminates() = runTest {
        val backend = FakeApiBackend()
        val page = backend.mageRideApi().ride.listRideHistory()

        assertEquals(1, page.items.size)
        assertEquals(false, page.hasMore, "a synthesised page must not claim another one exists")
        assertNull(page.cursor)
    }

    @Test
    fun an_unknown_operation_id_fails_loudly() {
        val backend = FakeApiBackend()
        val failure = assertFailsWith<IllegalStateException> {
            backend.always("bookARide", FakeReply.empty())
        }
        assertTrue(failure.message.orEmpty().contains("bookARide"), failure.message.orEmpty())
    }

    private companion object {
        /**
         * How many rows the table carries. **Not** the contract's operation count — see below.
         *
         * **Δ C068: 179 → 180.** `setOperatingCity` had been in `iam.yaml` since C027 with no
         * typed client; AL-27's first-run city screen is the caller.
         *
         * **Δ MCS-02: 180 → 176.** Four operations retired by AL-57/AL-47 were deleted.
         *
         * **Δ MCS-03: 176 → 189**, as the 65 missing operations land a slice at a time. The
         * in-scope contracts declare **241**, so this number is still climbing and
         * `ContractCoverageTest` stays red until it arrives.
         *
         * One of the 65 is deliberately **not** here: `getSupportScreenshot` answers `200` with
         * `image/jpeg`, and [lk.mageride.shared.testing.fake.FakeOperation] can express a JSON
         * body or no body and nothing else. Its client function exists; the row waits on a table
         * that can say "binary". `downloadSignedGtfsObject` is the same shape. See the MCS-03
         * handoff.
         */
        const val EXPECTED_OPERATIONS = 189
    }
}

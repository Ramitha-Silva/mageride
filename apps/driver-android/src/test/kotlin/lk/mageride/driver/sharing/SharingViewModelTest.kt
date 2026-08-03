package lk.mageride.driver.sharing

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.HOME_VEHICLE_ID
import lk.mageride.driver.home.liveVehicle
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.CreateShareGrantResponse
import lk.mageride.shared.data.models.registry.GrantStatus
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.data.models.subscription.AccessRequest
import lk.mageride.shared.data.models.subscription.AccessRequestAccepted
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-028 — AL-35's per-vehicle scope, and the two services behind it.
 *
 * The definition-of-done case is here: *"switching the vehicle selector re-scopes the sharing list
 * and the request queue"*. Both endpoints take the vehicle in the path, so what is asserted is that
 * the reads are re-issued **for the newly selected vehicle** and that the previous vehicle's rows
 * are never left on screen under the new chip.
 */
@OptIn(ExperimentalTime::class)
class SharingViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    private val vanId: Ulid = HOME_VEHICLE_ID
    private val sedanId: Ulid = "01JVEHICLE0000000000000021"
    private val requestId: Ulid = "01JACCESSREQUEST000000001"

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                items = listOf(
                    liveVehicle(vehicleId = vanId, mode = ServiceMode.B),
                    liveVehicle(vehicleId = sedanId, mode = ServiceMode.B),
                ),
            ),
        )
        backend.returns("listModeBAccessRequests", Page(items = listOf(request(vanId))))
        backend.returns("listVehicleSubscribers", Page(items = listOf(grantee())))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun only_mode_a_and_mode_b_vehicles_can_be_shared() = runBlocking {
        // `POST /v1/vehicles/{id}/share` is documented for Mode A/B and the request queue is
        // literally `/v1/mode-b/…`. A Mode C standby tuk has no subscribers and nothing to share.
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(items = listOf(liveVehicle(mode = ServiceMode.C))),
        )

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertTrue(state.vehicles.isEmpty())
        assertTrue(state.hasNoShareableVehicle)
        assertFalse(state.canGrant)
    }

    @Test
    fun switching_the_selector_re_reads_both_lists_for_the_new_vehicle() = runBlocking {
        val model = viewModel()
        model.state.await { it.requests.isNotEmpty() }
        assertEquals(vanId, model.state.value.selectedVehicleId)
        val queuePath = backend.lastCall("listModeBAccessRequests").path
        assertEquals(vanId, queuePath.substringAfter("/mode-b/").substringBefore("/"))

        backend.returns("listModeBAccessRequests", Page(items = listOf(request(sedanId, "01JACCESSREQUEST000000002"))))
        model.selectVehicle(sedanId)

        val state = model.state.await { it.requests.any { row -> row.vehicleId == sedanId } }
        assertEquals(sedanId, state.selectedVehicleId)
        assertEquals(listOf(sedanId), state.requests.map { it.vehicleId }, "never mixed across vehicles")
        assertTrue(backend.lastCall("listVehicleSubscribers").path.contains(sedanId))
    }

    @Test
    fun tapping_the_chip_that_is_already_selected_re_reads_nothing() = runBlocking {
        // The selector is a radio row, and a driver taps the lit chip by accident. Re-issuing both
        // reads would blank the queue they were looking at and fill it again.
        val model = viewModel()
        model.state.await { it.requests.isNotEmpty() }
        val readsSoFar = backend.callsTo("listModeBAccessRequests").size

        model.selectVehicle(vanId)

        assertEquals(readsSoFar, backend.callsTo("listModeBAccessRequests").size)
        assertFalse(model.state.value.listsLoading)
    }

    @Test
    fun granting_sends_the_id_and_the_expiry_and_clears_the_form() = runBlocking {
        backend.returns("createShareGrant", CreateShareGrantResponse(grantId = "01JGRANT00000000000000001"))

        val model = viewModel()
        // `loading` goes false BEFORE the per-vehicle lists are read (Δ C075), so awaiting it alone
        // and then asserting on the roster is a race with `readListsFor` rather than a claim about
        // the grant.
        model.state.await { !it.loading && it.grantees.isNotEmpty() }
        model.onUserIdChange(Fixtures.PASSENGER_ID)
        model.onExpiryChange(Fixtures.NOW)
        assertTrue(model.state.value.canGrant)

        model.grant()

        val state = model.state.await { it.granted != null }
        assertEquals(Fixtures.PASSENGER_ID, state.granted)
        assertEquals("", state.userId, "the form clears; a second tap would be a second invitation")
        // US-4.3b: the grant is pending until the passenger accepts, so the roster is untouched.
        assertEquals(listOf(Fixtures.PASSENGER_ID), state.grantees.map { it.userId })

        val body = MageRideJson.parseToJsonElement(backend.lastCall("createShareGrant").body).toString()
        assertTrue(body.contains(Fixtures.PASSENGER_ID), body)
        assertTrue(body.contains("expiresAt"), body)
    }

    @Test
    fun a_malformed_user_id_never_reaches_the_gateway() = runBlocking {
        // The wireframe prints `PAX-90431` and no such identifier exists — see `PlatformId`.
        val model = viewModel()
        model.state.await { !it.loading }
        model.onUserIdChange("PAX-90431")

        assertTrue(model.state.value.userIdRejected)
        assertFalse(model.state.value.canGrant)

        model.grant()
        assertFalse(backend.called("createShareGrant"))
    }

    @Test
    fun accepting_a_request_takes_it_out_of_the_queue_and_into_the_roster() = runBlocking {
        backend.returns(
            "acceptModeBAccessRequest",
            AccessRequestAccepted(
                requestId = requestId,
                grantId = "01JGRANT00000000000000002",
                subscriptionId = Fixtures.SUBSCRIPTION_ID,
            ),
        )

        val model = viewModel()
        model.state.await { it.requests.isNotEmpty() }

        // The accept creates the grant AND starts the subscription in one transaction, so the
        // re-read is what both lists agree through.
        backend.returns("listModeBAccessRequests", Page(items = emptyList<AccessRequest>()))
        model.decide(requestId, accept = true)

        // Both conditions in one predicate (Δ C075): `readListsFor` folds the lists in and `decide`
        // clears `busyRequestId` in a SECOND update, so awaiting the empty queue alone can land on
        // the state between them — a conflated `StateFlow` makes that a coin toss rather than a
        // bug in the view model.
        val state = model.state.await { it.requests.isEmpty() && it.busyRequestId == null }
        assertTrue(backend.called("acceptModeBAccessRequest"))
        assertEquals(listOf(Fixtures.PASSENGER_ID), state.grantees.map { it.userId })
    }

    @Test
    fun rejecting_sends_no_invented_reason() = runBlocking {
        // The body's `reason` is owner-written and optional, and the wireframe's row is a bare
        // Reject beside Accept. A rejection with a made-up justification is worse than one with none.
        backend.returns("rejectModeBAccessRequest", request(vanId, status = AccessRequestStatus.REJECTED))

        val model = viewModel()
        model.state.await { it.requests.isNotEmpty() }
        model.decide(requestId, accept = false)

        model.state.await { it.busyRequestId == null && backend.called("rejectModeBAccessRequest") }
        val body = MageRideJson.parseToJsonElement(backend.lastCall("rejectModeBAccessRequest").body).toString()
        assertFalse(body.contains("reason"), body)
    }

    @Test
    fun an_unsubscribed_grantee_is_not_drawn_as_someone_who_can_track_the_vehicle() = runBlocking {
        backend.returns(
            "listVehicleSubscribers",
            Page(items = listOf(grantee(), grantee(Fixtures.RECIPIENT_ID, GrantStatus.UNSUBSCRIBED))),
        )

        val model = viewModel()
        val state = model.state.await { it.grantees.isNotEmpty() }

        assertEquals(listOf(Fixtures.PASSENGER_ID), state.grantees.map { it.userId })
    }

    private fun request(
        vehicleId: Ulid,
        id: Ulid = requestId,
        status: AccessRequestStatus = AccessRequestStatus.PENDING,
    ): AccessRequest = AccessRequest(
        requestId = id,
        vehicleId = vehicleId,
        passengerId = Fixtures.PASSENGER_ID,
        passengerName = "Sunethra",
        passengerMobileMasked = Fixtures.PASSENGER_PHONE_MASKED,
        status = status,
        createdAt = Fixtures.NOW,
    )

    private fun grantee(userId: Ulid = Fixtures.PASSENGER_ID, status: GrantStatus = GrantStatus.ACTIVE): Subscriber =
        Subscriber(
            userId = userId,
            name = "Ramith de Silva",
            phoneMasked = Fixtures.PASSENGER_PHONE_MASKED,
            status = status,
            grantedAt = Fixtures.NOW,
        )

    private suspend fun viewModel(): SharingViewModel {
        val api = backend.mageRideApi()
        return main.own(
            SharingViewModel(
                identity = identity(backend, signedInSessions(backend)),
                sharing = SharingRepository(registry = api.registry, subscription = api.subscription),
            ),
        )
    }
}

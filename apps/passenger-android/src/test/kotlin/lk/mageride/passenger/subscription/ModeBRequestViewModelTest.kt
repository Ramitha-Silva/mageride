package lk.mageride.passenger.subscription

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.passenger.nav.PassengerRoute
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ProblemDetails
import lk.mageride.shared.domain.auth.AuthSessionManager
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-PA-024, and AL-23's fence: **a request is per vehicle, and the id comes from the marker.**
 *
 * The two entry points are the whole design of this screen — a Mode B marker tap pre-fills the
 * Vehicle ID, the drawer's *"Private transport"* row does not — and the route is what carries the
 * difference, so it is asserted here alongside the view model.
 */
class ModeBRequestViewModelTest {

    private val main = MainDispatcher()
    private val repository = FakeSubscriptionRepository()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_marker_tap_arrives_pre_filled_and_the_drawer_row_does_not() {
        // AL-23 / US-4.6. `ModeBRequest` carries the id as an OPTIONAL query argument for exactly
        // this: one destination, two doors.
        assertEquals(
            "private-transport?vehicleId=${FakeSubscriptionRepository.VEHICLE_ID}",
            PassengerRoute.ModeBRequest(FakeSubscriptionRepository.VEHICLE_ID).path,
        )
        assertEquals("private-transport", PassengerRoute.ModeBRequest().path, "the drawer row has no marker to read")

        val fromMarker = viewModel(FakeSubscriptionRepository.VEHICLE_ID)
        assertEquals(FakeSubscriptionRepository.VEHICLE_ID, fromMarker.state.value.vehicleId)
        assertTrue(fromMarker.state.value.prefilled)

        val fromDrawer = viewModel(vehicleId = null)
        assertEquals("", fromDrawer.state.value.vehicleId)
        assertFalse(fromDrawer.state.value.prefilled)
    }

    @Test
    fun nothing_is_sent_until_a_vehicle_id_is_present() = runBlocking {
        val model = viewModel(vehicleId = null)
        assertFalse(model.state.value.canSend)

        model.send()
        assertTrue(repository.accessRequested.isEmpty(), "an empty field sends nothing")

        model.onVehicleIdChange("  ${FakeSubscriptionRepository.VEHICLE_ID}  ")
        assertEquals(
            FakeSubscriptionRepository.VEHICLE_ID,
            model.state.value.vehicleId,
            "trimmed — a pasted id brings whitespace with it and the server 404s on it",
        )
        assertTrue(model.state.value.canSend)
    }

    @Test
    fun sending_raises_one_request_per_vehicle_and_shows_the_pending_chip() = runBlocking {
        val model = viewModel(FakeSubscriptionRepository.VEHICLE_ID)

        model.send()
        val state = model.state.await { it.status != null }

        assertEquals(AccessRequestStatus.PENDING, state.status, "US-4.6's chip")
        assertEquals(listOf(FakeSubscriptionRepository.VEHICLE_ID), repository.accessRequested)
        assertTrue(repository.idempotencyKeys.all { !it.isNullOrBlank() }, "R-14 — a double tap is one request")

        // A second tap while the request is pending must not raise a duplicate.
        assertFalse(state.canSend)
        model.send()
        assertEquals(1, repository.accessRequested.size)
    }

    @Test
    fun a_vehicle_already_subscribed_to_reads_as_accepted_rather_than_as_a_new_request() = runBlocking {
        // The gap this works around: there is no passenger-facing read of one's own access
        // requests and no Mode B push kind, so "accepted" is only ever visible as the subscription
        // the accept created in the same transaction. See the view model's KDoc.
        repository.subscriptions = listOf(FakeSubscriptionRepository.paidSubscription())

        val model = viewModel(FakeSubscriptionRepository.VEHICLE_ID)
        val state = model.state.await { it.existing != null }

        assertEquals(AccessRequestStatus.ACCEPTED, state.status)
        assertFalse(state.canSend, "asking again would be a 409")
    }

    @Test
    fun a_typed_id_that_matches_no_vehicle_says_so() = runBlocking {
        // D-26 — the kebab code is the key and the copy is `strings.xml`'s. "Something went wrong"
        // would send a passenger back to a field that is correct except for one character.
        repository.failWith = notFound()
        val model = viewModel(vehicleId = null)
        model.onVehicleIdChange("MR-VEH-00000")

        model.send()
        val state = model.state.await { it.error != null }

        assertEquals(R.string.error_vehicle_not_found, state.error)
        assertTrue(state.canSend, "the passenger can fix the id and try again")
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel(vehicleId: String?, sessions: AuthSessionManager = session()) = main.own(
        ModeBRequestViewModel(
            vehicleId = vehicleId,
            subscriptions = repository,
            sessions = sessions,
            keys = { KEY },
        ),
    )

    private fun session(): AuthSessionManager = signedInSession().also { runBlocking { it.signIn() } }

    private fun notFound() = MageRideError.NotFound(
        ProblemDetails(
            type = ErrorCode.VEHICLE_NOT_FOUND.typeUri,
            title = "Vehicle not found",
            status = HttpStatusCode.NotFound.value,
        ),
    )

    private companion object {
        const val KEY = "01JIDEMPOTENCY000000000001"
    }
}

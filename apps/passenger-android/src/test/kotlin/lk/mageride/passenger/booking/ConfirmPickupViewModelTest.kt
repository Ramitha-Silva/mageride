package lk.mageride.passenger.booking

import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.location.PassengerFix
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.ride.LocationRequest
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.minutes

/**
 * SCR-PA-011, and the Definition-of-Done line that says *"a rider Decline sends no coordinates"*.
 *
 * That is the single most important assertion in this component, and it is asserted the only way
 * that means anything: by looking at **what the repository was handed**. The decline operation
 * takes a request id and nothing else — `ride.yaml` gives it no body — so the fake can only record
 * an id, and a test that finds a coordinate anywhere in that record would mean the contract itself
 * had changed.
 */
class ConfirmPickupViewModelTest {

    private val main = MainDispatcher()
    private val bookings = FakeBookingRepository()
    private val locations = FakeFixes()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun declining_sends_no_coordinates() = runBlocking {
        // P-02. The rider has a GPS lock — `locations` has published one and the pin is on screen
        // — and declining still sends nothing but the request id. There is no "approximate
        // location" consolation and no city-level fallback.
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9344, lng = 79.8428, accuracyMetres = 8.0))
        model.state.await { it.pin != null }

        model.decline()
        model.state.await { it.outcome != null }

        assertEquals(listOf(REQUEST_ID), bookings.declines)
        assertTrue(bookings.confirms.isEmpty(), "nothing was confirmed on the way out")
        assertEquals(LocationRequestState.Declined, model.state.value.outcome)
    }

    @Test
    fun a_decline_that_fails_still_stands_locally() = runBlocking {
        // A rider must never be left sitting on a "share your location" screen because a decline
        // failed. The refusal is theirs; the network's opinion of it is not the point, and no
        // position was sent either way.
        val failing = object : BookingRepository by bookings {
            override suspend fun declineLocationRequest(requestId: String): LocationRequest =
                error("the network is gone")
        }
        val model = main.own(ConfirmPickupViewModel(REQUEST_ID, failing, locations) { Fixtures.NOW })

        model.decline()
        val state = model.state.await { it.outcome != null }

        assertEquals(LocationRequestState.Declined, state.outcome)
        assertTrue(bookings.confirms.isEmpty())
    }

    @Test
    fun sharing_sends_the_pin_and_the_accuracy_it_was_taken_at() = runBlocking {
        // `GeoPointWithAccuracy` has a field for the accuracy and dispatch uses it: a 500 m
        // cell-tower fix and a 5 m GPS lock are different instructions to a driver, and sending
        // only the coordinate would make them look identical.
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9344, lng = 79.8428, accuracyMetres = 12.5))
        model.state.await { it.pin != null }

        model.share()
        model.state.await { it.outcome == LocationRequestState.Confirmed }

        val (id, point) = bookings.confirms.single()
        assertEquals(REQUEST_ID, id)
        assertEquals(6.9344, point.lat)
        assertEquals(12.5, point.accuracy)
        assertTrue(bookings.declines.isEmpty())
    }

    @Test
    fun the_pin_the_rider_dragged_is_the_pin_that_is_sent() = runBlocking {
        // The wireframe's "drag to adjust". A GPS lock indoors is often a building away, and the
        // rider is the only one who knows where the driver should actually stop.
        val model = viewModel()
        locations.emit(PassengerFix(lat = 6.9344, lng = 79.8428, accuracyMetres = 30.0))
        model.state.await { it.pin != null }

        model.onPinMoved(GeoPoint(lat = 6.9350, lng = 79.8440))
        model.share()
        model.state.await { it.outcome == LocationRequestState.Confirmed }

        val point = bookings.confirms.single().second
        assertEquals(6.9350, point.lat)
        assertEquals(79.8440, point.lng)
    }

    @Test
    fun the_countdown_starts_from_the_servers_expiry_and_not_from_a_fresh_five_minutes() = runBlocking {
        // The FCM may have sat in a Doze bucket for minutes before the handset woke. A countdown
        // that restarted at 5:00 on open would promise time the server has already spent, and the
        // rider would tap Share into a rejection.
        bookings.locationRequestAnswer = LocationRequest(
            requestId = REQUEST_ID,
            state = LocationRequestState.Pending,
            expiresAt = Fixtures.NOW + 2.minutes,
        )
        val model = viewModel()

        val state = model.state.await { it.secondsLeft > 0 }

        assertTrue(state.secondsLeft <= 120, "two minutes left, not five: was ${state.secondsLeft}")
        assertEquals("2:00", state.countdown)
    }

    @Test
    fun an_already_resolved_request_closes_the_screen_without_asking_again() = runBlocking {
        // The rider answered on another device, or the window closed while the push sat in the
        // tray. Either way there is nothing to decide and the screen must not offer a choice that
        // would be rejected.
        bookings.locationRequestAnswer = LocationRequest(
            requestId = REQUEST_ID,
            state = LocationRequestState.Expired,
            expiresAt = Fixtures.NOW,
        )
        val model = viewModel()

        val state = model.state.await { it.outcome != null }

        assertEquals(LocationRequestState.Expired, state.outcome)
        assertTrue(bookings.confirms.isEmpty() && bookings.declines.isEmpty())
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        ConfirmPickupViewModel(
            requestId = REQUEST_ID,
            bookings = bookings,
            locations = locations,
            now = { Fixtures.NOW },
        ),
    )

    private class FakeFixes : PassengerLocationSource {
        private val flow = MutableSharedFlow<PassengerFix>(replay = 1)
        override val fixes: Flow<PassengerFix> = flow
        suspend fun emit(fix: PassengerFix) = flow.emit(fix)
    }

    private companion object {
        const val REQUEST_ID = FakeBookingRepository.REQUEST_ID
    }
}

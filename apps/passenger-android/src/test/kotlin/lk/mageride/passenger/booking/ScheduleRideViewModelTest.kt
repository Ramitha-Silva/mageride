package lk.mageride.passenger.booking

import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.onboarding.FakeAppPreferences
import lk.mageride.passenger.settings.PaymentPreference
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes

/**
 * SCR-PA-013, and the Definition-of-Done line *"Confirm on Schedule Ride is disabled until a
 * destination is chosen"* (AL-36).
 *
 * The other half of this screen is the **absence** of a fare. `MaterialiseScheduledRideRequest`'s
 * own contract note explains it: *"the price of a ride thirty minutes from now is not the price
 * quoted when it was booked"*, so dispatch meters it at the time and there is deliberately no
 * `fareEstimateToken` anywhere on this path.
 */
class ScheduleRideViewModelTest {

    private val main = MainDispatcher()
    private val bookings = FakeBookingRepository()
    private val draft = BookingDraft(PaymentPreference(FakeAppPreferences()))

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun confirm_is_refused_until_a_destination_is_set() = runBlocking {
        // AL-36. A time on its own is not a booking — dispatch would have nothing to put on the
        // Job Board — so the gate is the destination and only the destination.
        val model = viewModel()
        model.setPickupTime(Fixtures.NOW + 2.hours)

        assertFalse(model.state.value.canConfirm, "a time with no destination is not schedulable")
        model.confirm()
        assertTrue(bookings.scheduled.isEmpty(), "and nothing was sent")

        model.setDestination(NUGEGODA)

        assertTrue(model.state.value.canConfirm)
    }

    @Test
    fun a_destination_alone_is_not_enough_either() = runBlocking {
        // `ScheduleRideRequest.pickupTime` is non-null in the contract, so a destination with no
        // time cannot be sent. AL-36 names the destination because that is the one a passenger can
        // forget; the time is a field they have to touch.
        val model = viewModel()
        model.setDestination(NUGEGODA)

        assertFalse(model.state.value.canConfirm)
        model.confirm()

        assertTrue(bookings.scheduled.isEmpty())
    }

    @Test
    fun a_time_inside_the_job_board_window_is_refused_with_a_reason() = runBlocking {
        // The board opens at T-30 (US-6A.4/6A.5). A ride scheduled for twenty minutes' time would
        // be posted to a board it has already passed — a booking that quietly never dispatches,
        // which is worse than a refusal the passenger can act on.
        val model = viewModel()
        model.setDestination(NUGEGODA)

        model.setPickupTime(Fixtures.NOW + 20.minutes)

        val state = model.state.await { it.error != null }
        assertFalse(state.canConfirm, "no time was accepted")
        assertEquals(null, state.pickupTime)
    }

    @Test
    fun confirming_sends_both_ends_the_time_and_the_tier() = runBlocking {
        val model = viewModel()
        model.setPickup(COLOMBO)
        model.setDestination(NUGEGODA)
        model.setVehicleType(RideVehicleType.SEDAN)
        model.setPickupTime(Fixtures.NOW + 2.hours)

        model.confirm()
        model.state.await { it.scheduled != null }

        val sent = bookings.scheduled.single()
        assertEquals(COLOMBO.lat, sent.pickupLat)
        assertEquals(NUGEGODA.lat, sent.destLat)
        assertEquals(RideVehicleType.SEDAN, sent.vehicleType)
        assertEquals(Fixtures.NOW + 2.hours, sent.pickupTime)
    }

    @Test
    fun a_scheduled_ride_carries_no_fare_token() = runBlocking {
        // Asserted from the other side: nothing on this path calls `estimate`, because there is
        // nothing on `ScheduleRideRequest` to put a token in. A screen that quoted a price would
        // be quoting one nobody promised.
        val model = viewModel()
        model.setDestination(NUGEGODA)
        model.setPickupTime(Fixtures.NOW + 2.hours)

        model.confirm()
        model.state.await { it.scheduled != null }

        assertTrue(bookings.estimated.isEmpty(), "no quote is taken for a ride hours from now")
        assertTrue(bookings.requested.isEmpty(), "and it is not a ride yet")
    }

    @Test
    fun a_scheduled_ride_with_no_pickup_is_a_legitimate_booking() = runBlocking {
        // `pickupLat`/`pickupLng` are nullable in the contract: a ride that starts wherever the
        // passenger happens to be at the time is a real thing to book, not a missing field.
        val model = viewModel()
        model.setDestination(NUGEGODA)
        model.setPickupTime(Fixtures.NOW + 2.hours)

        model.confirm()
        model.state.await { it.scheduled != null }

        val sent = bookings.scheduled.single()
        assertNotNull(sent.pickupTime)
        assertEquals(null, sent.pickupLat)
        assertEquals(null, sent.pickupLng)
    }

    @Test
    fun the_draft_is_cleared_once_the_row_exists() = runBlocking {
        val model = viewModel()
        model.setDestination(NUGEGODA)
        model.setPickupTime(Fixtures.NOW + 2.hours)

        model.confirm()
        model.state.await { it.scheduled != null }

        assertEquals(null, draft.current.dropoff)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        ScheduleRideViewModel(draft = draft, bookings = bookings, now = { Fixtures.NOW }),
    )

    private companion object {
        val COLOMBO = Place(lat = 6.9344, lng = 79.8428, address = "Colombo Fort")
        val NUGEGODA = Place(lat = 6.8649, lng = 79.8997, address = "Nugegoda")
    }
}

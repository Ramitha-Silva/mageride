package lk.mageride.driver.jobs

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.domain.dispatch.JobBoard
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-018 — the reminder window, and the one cancellation route this surface has.
 *
 * **The reminder and the go-live are the same instant.** D5' §3.7 dispatches a scheduled ride at
 * T-30 and §14.4 pushes `SCHEDULED_REMINDER` at 30 minutes for a driver, so the row that says
 * *"in 28 min"* is exactly the row whose ride is being offered — which is why the screen derives the
 * state from `JobBoard.GO_LIVE_LEAD` rather than keeping a threshold of its own.
 */
@OptIn(ExperimentalTime::class)
class ScheduledRidesViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    private var now: Timestamp = Fixtures.NOW

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_list_is_soonest_first_and_the_imminent_row_is_inside_its_reminder_window() = runBlocking {
        backend.returns(
            "listDriverScheduledRides",
            page(
                scheduledRide(id = JOB_TWO, inFuture = 8.hours),
                scheduledRide(id = JOB_ONE, inFuture = 28.minutes),
            ),
        )

        val model = viewModel()
        val state = model.state.await { it.rows.size == 2 }

        assertEquals(listOf(JOB_ONE, JOB_TWO), state.rows.map { it.id })

        val imminent = state.rows.first()
        assertTrue(imminent.reminderFired, "T-30 has passed, so notification-svc has pushed")
        assertEquals(28, imminent.minutesToPickup, "the wireframe's 'in 28 min'")

        val later = state.rows.last()
        assertFalse(later.reminderFired, "eight hours out is an 'Accepted' pill, not a countdown")
    }

    @Test
    fun the_reminder_window_opens_exactly_at_t_minus_thirty() = runBlocking {
        backend.returns("listDriverScheduledRides", page(scheduledRide(inFuture = JobBoard.GO_LIVE_LEAD)))

        val model = viewModel()
        val state = model.state.await { it.rows.isNotEmpty() }

        assertTrue(state.rows.first().reminderFired, "30 minutes is inside the window, not before it")
    }

    @Test
    fun a_cancelled_booking_is_not_listed() = runBlocking {
        backend.returns(
            "listDriverScheduledRides",
            page(
                scheduledRide(id = JOB_ONE, inFuture = 4.hours, status = ScheduledRideStatus.CANCELLED),
                scheduledRide(id = JOB_TWO, inFuture = 5.hours),
            ),
        )

        val model = viewModel()
        val state = model.state.await { it.rows.isNotEmpty() }

        assertEquals(listOf(JOB_TWO), state.rows.map { it.id })
    }

    @Test
    fun a_dispatched_ride_cannot_be_withdrawn_here() = runBlocking {
        // From T-30 the ride exists and `POST /v1/rides/{rideId}/cancel` owns the outcome — with the
        // D5' §7 penalty matrix. `DELETE /v1/rides/schedule/{id}` answers 409 from that point on, so
        // the button is refused before the call rather than after it.
        backend.returns(
            "listDriverScheduledRides",
            page(scheduledRide(inFuture = 20.minutes, status = ScheduledRideStatus.DISPATCHED)),
        )

        val model = viewModel()
        val state = model.state.await { it.rows.isNotEmpty() }

        model.cancel(state.rows.first())

        assertFalse(state.rows.first().isScheduled)
        assertFalse(backend.called("cancelScheduledRide"), "nothing is sent for a materialised ride")
    }

    @Test
    fun the_servers_refusal_of_a_withdrawal_becomes_copy_and_keeps_the_row() = runBlocking {
        // `DELETE /v1/rides/schedule/{id}` is mapped inside dispatch-svc's **passenger** role group,
        // so a driver's call is a 403. The row stays and the driver is told, rather than the screen
        // pretending the booking is gone. Recorded as a spec gap in the C072 handoff.
        backend.returns("listDriverScheduledRides", page(scheduledRide(inFuture = 4.hours)))
        backend.fails("cancelScheduledRide", HttpStatusCode.Forbidden, "forbidden")

        val model = viewModel()
        val loaded = model.state.await { it.rows.isNotEmpty() }

        model.cancel(loaded.rows.first())
        val state = model.state.await { it.error != null }

        assertEquals(listOf(JOB_ONE), state.rows.map { it.id }, "the booking is still the driver's")
        assertFalse(state.rows.first().cancelling)
    }

    private suspend fun viewModel(): ScheduledRidesViewModel {
        val api = backend.mageRideApi()
        return ScheduledRidesViewModel(
            identity = identity(backend, signedInSessions(backend)),
            jobs = JobsRepository(dispatch = api.dispatch),
            clock = { now },
        )
    }
}

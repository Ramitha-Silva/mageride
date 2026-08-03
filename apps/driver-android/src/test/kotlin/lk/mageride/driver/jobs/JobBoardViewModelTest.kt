package lk.mageride.driver.jobs

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.FakeDriverLocationSource
import lk.mageride.driver.home.fix
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.DriverLevelResponse
import lk.mageride.shared.data.models.dispatch.DriverStatsResponse
import lk.mageride.shared.data.models.dispatch.JobBoardIntentResponse
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.domain.dispatch.DriverStanding
import lk.mageride.shared.domain.dispatch.JobBoard
import lk.mageride.shared.domain.dispatch.JobBoardRejection
import lk.mageride.shared.domain.dispatch.JobBoardVerdict
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-017's two fences and its one clock.
 *
 * **Level 1 is a gate, not an error** (US-6A.8) — a gated driver never even reaches the board read,
 * because a `GET /v1/rides/job-board` they are not allowed to act on is a round trip spent to draw
 * a list with every button disabled.
 *
 * **The board is post-intent only** (D5' §3.7). The only call these tests can make it produce is
 * `postJobBoardIntent`; there is no accept on this surface and there is no route for one.
 *
 * **T-30 is the edge everything turns on.** A row goes out of reach at the instant dispatch starts
 * choosing, and it leaves the list a fade later.
 */
@OptIn(ExperimentalTime::class)
class JobBoardViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val location = FakeDriverLocationSource()

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
    fun a_level_one_driver_sees_the_gate_and_the_board_is_never_read() = runBlocking {
        // US-6A.8. Not a ban and not an error — the driver keeps immediate Mode C, and the copy on
        // this screen names the level that opens the board again.
        levelIs(1)
        backend.returns("listJobBoard", page(scheduledRide(inFuture = 4.hours)))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(true, state.gated)
        assertEquals(2, state.minimumLevel, "D5' §4.2's job-board floor")
        assertTrue(state.rows.isEmpty())
        assertFalse(backend.called("listJobBoard"), "a gated driver never reaches the board read")
    }

    @Test
    fun a_level_one_driver_cannot_post_intent_even_on_a_row_it_is_handed() {
        // Belt and braces: the gate is enforced by `JobBoard.canPostIntent` as well as by the
        // screen, so a row that reached a Level-1 driver by any route still refuses the call.
        val verdict = JobBoard().canPostIntent(
            driver = DriverStanding(level = 1),
            ride = scheduledRide(inFuture = 4.hours),
            now = now,
        )

        assertEquals(JobBoardVerdict.Rejected(JobBoardRejection.LEVEL_TOO_LOW), verdict)
    }

    @Test
    fun a_level_that_did_not_answer_is_neither_the_gate_nor_an_empty_board() = runBlocking {
        // The failure US-6A.8 must never produce: telling a Level-3 driver they are Level 1 because
        // reputation timed out. Three states, and the unknown one says it is unknown.
        backend.fails("getDriverLevel", HttpStatusCode.InternalServerError, "internal-error")
        backend.fails("getDriverStats", HttpStatusCode.InternalServerError, "internal-error")

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertNull(state.gated)
        assertTrue(state.isUnavailable)
        assertFalse(state.isEmpty, "'No jobs within 30 km' would be a claim we cannot make")
        assertFalse(backend.called("listJobBoard"))
    }

    @Test
    fun the_board_comes_back_soonest_first_and_within_the_d06_catchment() = runBlocking {
        levelIs(3)
        backend.returns(
            "listJobBoard",
            page(
                scheduledRide(id = JOB_TWO, inFuture = 8.hours),
                scheduledRide(id = JOB_ONE, inFuture = 4.hours),
            ),
        )

        val model = viewModel()
        location.emit(fix())
        val state = model.state.await { it.rows.size == 2 }

        assertEquals(listOf(JOB_ONE, JOB_TWO), state.rows.map { it.id }, "pickup time, soonest first")
        assertEquals(false, state.gated)
        assertEquals(30_000, JobBoard.CATCHMENT_METRES, "D-06's 30 km, and what the app bar prints")
        assertTrue(state.rows.all { it.canPost })
    }

    @Test
    fun posting_intent_marks_the_row_and_is_the_only_call_the_board_makes() = runBlocking {
        levelIs(3)
        backend.returns("listJobBoard", page(scheduledRide(inFuture = 4.hours)))
        backend.returns(
            "postJobBoardIntent",
            JobBoardIntentResponse(intentId = Fixtures.TRIP_ID, scheduledRideId = JOB_ONE),
        )

        val model = viewModel()
        location.emit(fix())
        val loaded = model.state.await { it.rows.isNotEmpty() }

        model.postIntent(loaded.rows.first())
        val state = model.state.await { it.rows.first().posted }

        assertTrue(state.rows.first().posted, "the wireframe's 'Intent posted ✓'")
        assertFalse(state.rows.first().canPost, "a second bid is not a second chance")
        assertTrue(backend.called("postJobBoardIntent"))

        // The whole point of the fence: there is no accept operation on this surface at all.
        assertFalse(backend.calls.any { it.operationId.contains("accept", ignoreCase = true) })
    }

    @Test
    fun a_row_inside_its_t30_window_is_expired_and_refuses_a_bid() = runBlocking {
        // D5' §3.7 — the ride goes live 30 minutes before pickup and dispatch starts choosing. An
        // intent posted after that cannot influence a decision already being made, so the board
        // stops taking it rather than spending a call nothing will read.
        levelIs(3)
        backend.returns("listJobBoard", page(scheduledRide(inFuture = JobBoard.GO_LIVE_LEAD)))

        val model = viewModel()
        location.emit(fix())
        val state = model.state.await { it.rows.isNotEmpty() }

        val row = state.rows.first()
        assertTrue(row.expired)
        assertFalse(row.canPost)
        assertEquals(
            JobBoardVerdict.Rejected(JobBoardRejection.GO_LIVE_WINDOW_PASSED),
            row.verdict,
        )
    }

    @Test
    fun a_row_leaves_the_board_once_its_window_and_its_fade_have_passed() = runBlocking {
        // The DoD line: "job-board rows disappear once their T-30 window passes". The fade is what
        // stops that looking like a tap that lost the driver a job (D2' §SCR-DA-017's expire anim).
        levelIs(3)
        backend.returns("listJobBoard", page(scheduledRide(inFuture = JobBoard.GO_LIVE_LEAD + 1.minutes)))

        val model = viewModel()
        location.emit(fix())
        model.state.await { it.rows.size == 1 }

        now += 1.minutes + JobBoardViewModel.EXPIRY_FADE
        val state = model.state.await { it.rows.isEmpty() }

        assertTrue(state.isEmpty, "and the empty board's own copy takes over")
    }

    @Test
    fun a_dispatched_or_cancelled_row_is_never_biddable() = runBlocking {
        levelIs(3)
        backend.returns(
            "listJobBoard",
            page(
                scheduledRide(id = JOB_ONE, inFuture = 4.hours, status = ScheduledRideStatus.DISPATCHED),
                scheduledRide(id = JOB_TWO, inFuture = 5.hours, status = ScheduledRideStatus.CANCELLED),
            ),
        )

        val model = viewModel()
        location.emit(fix())
        val state = model.state.await { it.rows.size == 2 }

        assertTrue(state.rows.none { it.canPost }, "the ride belongs to ride-svc, or to nobody")
    }

    private fun levelIs(level: Int) {
        backend.returns("getDriverLevel", DriverLevelResponse(level = level, ratingPoints = 310))
        backend.returns("getDriverStats", DriverStatsResponse(acceptanceRate = 0.92, noShows = 1, points = 310))
    }

    private suspend fun viewModel(): JobBoardViewModel {
        val api = backend.mageRideApi()
        return JobBoardViewModel(
            identity = identity(backend, signedInSessions(backend)),
            jobs = JobsRepository(dispatch = api.dispatch),
            location = location,
            clock = { now },
        )
    }
}

package lk.mageride.driver.level

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.JobsRepository
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.dispatch.DriverLevelResponse
import lk.mageride.shared.data.models.dispatch.DriverStatsResponse
import lk.mageride.shared.domain.dispatch.DriverLevelRules
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-019 — the ladder D5' §4.2 actually has, and the threshold an admin can move.
 *
 * The wireframe prints *"510 / 500 pts → Level 4"*. **There is no Level 4**: D5' §4.2 runs 1–3 and
 * levels up with `min(level + 1, 3)`. The layout is the wireframe's and the arithmetic is D5''s, and
 * this is where the two are held apart.
 */
class DriverLevelViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_mid_ladder_driver_progresses_toward_the_next_rung() = runBlocking {
        backend.returns("getDriverLevel", DriverLevelResponse(level = 2, ratingPoints = 250, levelUpThreshold = 500))
        backend.returns("getDriverStats", DriverStatsResponse(acceptanceRate = 0.92, noShows = 1, points = 250))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(2, state.level)
        assertEquals(250, state.points)
        assertEquals(500, state.threshold)
        assertEquals(3, state.nextLevel)
        assertEquals(0.5f, state.progress)
        assertEquals(92, state.acceptancePercent, "US-6A.14, and the wire is 0..1")
        assertEquals(1, state.noShows)
    }

    @Test
    fun the_top_of_the_ladder_is_level_three_and_the_bar_is_full() = runBlocking {
        // D5' §4.2's `min(level + 1, 3)`. The wireframe's "→ Level 4" does not exist, so the copy
        // says so rather than promising a rung dispatch cannot award.
        backend.returns("getDriverLevel", DriverLevelResponse(level = 3, ratingPoints = 10))
        backend.returns("getDriverStats", DriverStatsResponse(acceptanceRate = 1.0, noShows = 0, points = 10))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(DriverLevelRules.MAX_LEVEL, state.level)
        assertNull(state.nextLevel)
        assertTrue(state.atTopLevel)
        assertEquals(1f, state.progress, "a bar frozen at 2% would read as a driver who had stopped")
    }

    @Test
    fun the_level_up_threshold_is_the_servers_and_never_a_baked_five_hundred() = runBlocking {
        // `PUT /v1/admin/drivers/level-config` (US-14.12) can move it, and `DriverLevelRules`'
        // own KDoc warns that a build which baked 500 in would disagree with dispatch the day one
        // does. The screen shows what the server sent.
        backend.returns("getDriverLevel", DriverLevelResponse(level = 1, ratingPoints = 60, levelUpThreshold = 300))
        backend.returns("getDriverStats", DriverStatsResponse(acceptanceRate = 0.4, noShows = 4, points = 60))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(300, state.threshold)
        assertEquals(0.2f, state.progress)
        assertEquals(2, state.nextLevel)
    }

    @Test
    fun a_dead_stats_read_still_leaves_the_level_readable() = runBlocking {
        // The two reads are independent: the level is what gates the Job Board, and a reputation
        // counter that did not answer must not take the badge down with it.
        backend.returns("getDriverLevel", DriverLevelResponse(level = 3, ratingPoints = 120))
        backend.fails("getDriverStats", io.ktor.http.HttpStatusCode.InternalServerError, "internal-error")

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(3, state.level)
        assertEquals(120, state.points, "the level read carries `ratingPoints` too")
        assertNull(state.acceptancePercent)
        assertNull(state.noShows)
    }

    private suspend fun viewModel(): DriverLevelViewModel {
        val api = backend.mageRideApi()
        return DriverLevelViewModel(
            identity = identity(backend, signedInSessions(backend)),
            jobs = JobsRepository(dispatch = api.dispatch),
        )
    }
}

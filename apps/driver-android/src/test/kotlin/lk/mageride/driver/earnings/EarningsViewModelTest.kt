package lk.mageride.driver.earnings

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.util.BusinessCalendar
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-020 — **the dashboard reconciles with query-svc, because it does not do the arithmetic.**
 *
 * The DoD line is *"earnings figures reconcile with query-svc for a seeded driver"*, and the way
 * this screen satisfies it is by printing `EarningsSummary` as sent: gross, the daily fee deducted
 * (US-9.22), penalties, tips and the net. Re-summing the per-trip rows would produce a second total
 * that disagrees the first time a penalty lands between the two reads.
 */
@OptIn(ExperimentalTime::class)
class EarningsViewModelTest {

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
    fun the_card_is_query_svcs_own_arithmetic_and_nothing_is_recomputed() = runBlocking {
        // The wireframe's card: 12 trips · fares received Rs 3,280 · daily fee − Rs 100 · net Rs 3,180.
        backend.returns("getDriverEarnings", summary(grossMinor = 328_000, dailyFeeMinor = 10_000, netMinor = 318_000))
        backend.returns("listEarningSessions", sessions(session(netMinor = 100, endedAt = 1)))

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(328_000, state.grossMinor)
        assertEquals(10_000, state.dailyFeeMinor, "US-9.22's daily fee deducted")
        assertEquals(318_000, state.netMinor)
        assertEquals(12, state.tripCount)

        // The rows sum to Rs 1 and the card still says Rs 3,180. That is the point: the summary is
        // the server's answer, not a total this screen derives from the page it happened to read.
        assertEquals(1, state.trips.size)
        assertEquals(318_000, state.netMinor)
    }

    @Test
    fun changing_the_tab_re_reads_the_window() = runBlocking {
        backend.returns("getDriverEarnings", summary(period = EarningsPeriod.WEEK, netMinor = 1_500_000))
        backend.returns("listEarningSessions", sessions())

        val model = viewModel()
        model.state.await { !it.loading }

        model.select(EarningsPeriod.WEEK)
        val state = model.state.await { it.period == EarningsPeriod.WEEK && !it.loading }

        assertEquals(1_500_000, state.netMinor)
        assertTrue(backend.called("getDriverEarnings"))
        assertTrue(backend.called("listEarningSessions"))
    }

    @Test
    fun an_empty_period_is_a_state_and_not_a_zero_dressed_as_a_day() = runBlocking {
        backend.returns("getDriverEarnings", summary(grossMinor = 0, netMinor = 0, trips = 0))
        backend.returns("listEarningSessions", sessions())

        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertTrue(state.isEmptyPeriod, "D2' §SCR-DA-020's 'empty period'")
        assertTrue(state.trips.isEmpty())
    }

    @Test
    fun the_breakdown_is_newest_first() = runBlocking {
        backend.returns("getDriverEarnings", summary())
        backend.returns(
            "listEarningSessions",
            sessions(
                session(tripId = Fixtures.TRIP_ID, netMinor = 100, endedAt = 1),
                session(tripId = Fixtures.RIDE_ID, netMinor = 200, endedAt = 5),
            ),
        )

        val model = viewModel()
        val state = model.state.await { it.trips.size == 2 }

        assertEquals(listOf(Fixtures.RIDE_ID, Fixtures.TRIP_ID), state.trips.map { it.tripId })
    }

    private fun summary(
        period: EarningsPeriod = EarningsPeriod.TODAY,
        grossMinor: Long = 328_000,
        dailyFeeMinor: Long = 10_000,
        netMinor: Long = 318_000,
        trips: Int = 12,
    ) = EarningsSummary(
        period = period,
        rangeFrom = BusinessCalendar.businessDate(Fixtures.NOW),
        rangeTo = BusinessCalendar.businessDate(Fixtures.NOW),
        grossMinor = grossMinor,
        dailyFeeMinor = dailyFeeMinor,
        netMinor = netMinor,
        trips = trips,
    )

    private fun sessions(vararg rows: SessionEarning) = Page(items = rows.toList())

    private fun session(tripId: String = Fixtures.TRIP_ID, netMinor: Long, endedAt: Int) = SessionEarning(
        tripId = tripId,
        grossMinor = netMinor,
        netMinor = netMinor,
        endedAt = Fixtures.NOW + endedAt.hours,
    )

    private suspend fun viewModel(): EarningsViewModel {
        val api = backend.mageRideApi()
        return EarningsViewModel(
            identity = identity(backend, signedInSessions(backend)),
            earnings = EarningsRepository(query = api.query),
        )
    }
}

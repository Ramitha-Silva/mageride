package lk.mageride.driver.earnings

import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.testing.fixture.Fixtures
import lk.mageride.shared.util.BusinessCalendar
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.hours
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-020's trend, bucketed in **Asia/Colombo**.
 *
 * `?period=` is evaluated in Colombo by query-svc (D-13), so a chart bucketed in the handset's zone
 * would split the server's day across two of its own bars — and near midnight would put a trip on
 * the wrong side of the boundary the card counted it on.
 */
@OptIn(ExperimentalTime::class)
class EarningsBucketsTest {

    private val today = BusinessCalendar.businessDate(Fixtures.NOW)

    @Test
    fun today_is_bucketed_by_the_hours_actually_worked() {
        // `Fixtures.NOW` is 09:45 in Colombo. A trip that ended two hours earlier opens the span at
        // 07 and the current hour closes it, so the bars are 07, 08, 09.
        val buckets = EarningsBuckets.of(
            period = EarningsPeriod.TODAY,
            sessions = listOf(session(netMinor = 50_000, at = Fixtures.NOW - 2.hours)),
            from = today,
            to = today,
            now = Fixtures.NOW,
        )

        assertEquals(listOf("07", "08", "09"), buckets.map { it.label })
        assertEquals(50_000, buckets.first().netMinor)
        assertEquals(0, buckets[1].netMinor, "an idle hour is an empty bar, not a missing one")
        assertTrue(buckets.last().current, "the hour the clock is in")
    }

    @Test
    fun a_day_with_no_trips_is_still_one_bar_for_the_current_hour() {
        val buckets = EarningsBuckets.of(
            period = EarningsPeriod.TODAY,
            sessions = emptyList(),
            from = today,
            to = today,
            now = Fixtures.NOW,
        )

        assertEquals(listOf("09"), buckets.map { it.label })
        assertEquals(0, buckets.single().netMinor)
    }

    @Test
    fun a_week_is_bucketed_by_colombo_day_with_today_highlighted() {
        val from = BusinessCalendar.plusDays(today, -2)

        val buckets = EarningsBuckets.of(
            period = EarningsPeriod.WEEK,
            sessions = listOf(
                session(netMinor = 120_000, at = Fixtures.NOW),
                session(netMinor = 80_000, at = Fixtures.NOW - 24.hours),
            ),
            from = from,
            to = today,
            now = Fixtures.NOW,
        )

        assertEquals(3, buckets.size, "25th, 26th, 27th of July")
        assertEquals(listOf(0L, 80_000L, 120_000L), buckets.map { it.netMinor })
        assertEquals(listOf(false, false, true), buckets.map { it.current })
    }

    @Test
    fun the_day_boundary_is_colombos_and_not_utcs() {
        // 19:00Z is 00:30 on the 28th in Colombo. Bucketed in UTC the trip would land on the 27th —
        // the day the card has already stopped counting.
        val edge = Fixtures.MIDNIGHT_EDGE
        val colomboDay = BusinessCalendar.businessDate(edge)

        val buckets = EarningsBuckets.of(
            period = EarningsPeriod.MONTH,
            sessions = listOf(session(netMinor = 30_000, at = edge)),
            from = colomboDay,
            to = colomboDay,
            now = edge,
        )

        assertEquals(1, buckets.size)
        assertEquals(30_000, buckets.single().netMinor, "the 28th in Colombo, not the 27th in UTC")
        assertEquals("28", buckets.single().label)
    }

    private fun session(netMinor: Long, at: kotlin.time.Instant) = SessionEarning(
        tripId = Fixtures.TRIP_ID,
        grossMinor = netMinor,
        netMinor = netMinor,
        endedAt = at,
    )
}

package lk.mageride.shared.util

import kotlinx.datetime.LocalDate
import kotlinx.datetime.LocalTime
import kotlinx.datetime.TimeZone
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Instant

/**
 * Asia/Colombo date arithmetic (D-13, D-38).
 *
 * Every business date this platform settles is a Colombo calendar date. The failure this guards
 * against is subtle and expensive: at 19:00 UTC it is already tomorrow in Colombo, so a "today"
 * answered from UTC waives a daily fee against the wrong day.
 */
class BusinessCalendarTest {

    @Test
    fun the_zone_is_asia_colombo_at_utc_plus_five_thirty() {
        assertEquals(TimeZone.of("Asia/Colombo"), BusinessCalendar.ZONE)

        val midnightUtc = Instant.parse("2026-07-27T00:00:00Z")
        assertEquals(LocalTime(5, 30), BusinessCalendar.localTime(midnightUtc))
    }

    @Test
    fun the_business_date_rolls_at_colombo_midnight_not_utc_midnight() {
        // 18:30Z on 27 July is 00:00 on the 28th in Colombo.
        val justBefore = Instant.parse("2026-07-27T18:29:00Z")
        val justAfter = Instant.parse("2026-07-27T18:30:00Z")

        assertEquals(LocalDate(2026, 7, 27), BusinessCalendar.businessDate(justBefore))
        assertEquals(LocalDate(2026, 7, 28), BusinessCalendar.businessDate(justAfter))
        assertEquals(LocalDate(2026, 7, 27), BusinessCalendar.businessDate(justAfter, TimeZone.UTC))
    }

    @Test
    fun two_instants_either_side_of_colombo_midnight_are_different_days() {
        val justBefore = Instant.parse("2026-07-27T18:29:00Z")
        val justAfter = Instant.parse("2026-07-27T18:31:00Z")

        assertFalse(BusinessCalendar.isSameBusinessDay(justBefore, justAfter))
        assertTrue(BusinessCalendar.isSameBusinessDay(justBefore, justBefore + kotlin.time.Duration.ZERO))
        assertTrue(BusinessCalendar.isSameBusinessDay(justBefore, justAfter, TimeZone.UTC))
    }

    @Test
    fun the_start_of_a_colombo_day_is_18_30z_the_evening_before() {
        assertEquals(
            Instant.parse("2026-07-26T18:30:00Z"),
            BusinessCalendar.startOfDay(LocalDate(2026, 7, 27)),
        )
    }

    @Test
    fun the_first_of_the_month_is_the_shape_every_period_month_column_takes() {
        // `ck_payments_period_month_first` and friends (C005): a period_month names a month, so it
        // must be its first day, or the UNIQUE admits two rows for one month.
        assertEquals(LocalDate(2026, 7, 1), BusinessCalendar.firstOfMonth(LocalDate(2026, 7, 27)))
        assertEquals(LocalDate(2026, 7, 1), BusinessCalendar.firstOfMonth(LocalDate(2026, 7, 1)))
    }

    @Test
    fun adding_a_month_clamps_to_the_end_of_the_target_month() {
        assertEquals(LocalDate(2026, 2, 28), BusinessCalendar.plusMonths(LocalDate(2026, 1, 31)))
        assertEquals(LocalDate(2028, 2, 29), BusinessCalendar.plusMonths(LocalDate(2028, 1, 31)))
        assertEquals(LocalDate(2026, 7, 5), BusinessCalendar.plusMonths(LocalDate(2026, 6, 5)))
        assertEquals(LocalDate(2027, 1, 15), BusinessCalendar.plusMonths(LocalDate(2026, 12, 15)))
    }

    @Test
    fun adding_days_crosses_a_month_and_a_year_boundary() {
        assertEquals(LocalDate(2026, 8, 1), BusinessCalendar.plusDays(LocalDate(2026, 7, 31)))
        assertEquals(LocalDate(2027, 1, 1), BusinessCalendar.plusDays(LocalDate(2026, 12, 31)))
    }
}

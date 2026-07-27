package lk.mageride.shared.util

import kotlinx.datetime.DateTimeUnit
import kotlinx.datetime.LocalDate
import kotlinx.datetime.LocalTime
import kotlinx.datetime.TimeZone
import kotlinx.datetime.atStartOfDayIn
import kotlinx.datetime.plus
import kotlinx.datetime.toLocalDateTime
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Timestamp

// Asia/Colombo date arithmetic, client side (D-13, D-38).
//
// Every business date this platform settles — the daily-fee `fee_date`, a subscription's
// `next_due`, a billing `period_month` — is an ASIA/COLOMBO calendar date, and every one of them
// is persisted beside the instant it was derived at (D-38). Near midnight the two disagree, which
// is exactly why the pair exists: 2026-07-27T19:00Z is already the 28th in Colombo, and a client
// that answered "today" from UTC would charge a driver's first free trip against the wrong day.
//
// This is the client mirror of MageRide.Shared's `BusinessCalendar` (C002). The SERVER's answer is
// authoritative in every case — a fee row, a due date and a period month are all written
// server-side. What this is for is showing the right day before the round trip, and telling a
// driver which day the app means.

/**
 * The platform's business calendar.
 *
 * Every function takes the zone explicitly and defaults it to [ZONE], so a test can prove a rule
 * without moving the host clock and a future second operating timezone is a parameter rather than
 * a rewrite.
 */
public object BusinessCalendar {

    /**
     * `Asia/Colombo` — the only zone any MageRide business date is evaluated in (D-38).
     *
     * UTC+05:30 with no daylight saving since 2006, so nothing here has to reason about a gap or
     * an overlap. It is still resolved through the tz database rather than hardcoded as an offset:
     * a fixed `+05:30` would silently outlive a rule change that the database would carry.
     */
    public val ZONE: TimeZone = TimeZone.of("Asia/Colombo")

    /** The Colombo calendar date [at] falls on — a `fee_date`, a `period_month`'s day, a due date. */
    public fun businessDate(at: Timestamp, zone: TimeZone = ZONE): BusinessDate = at.toLocalDateTime(zone).date

    /** The Colombo wall-clock time [at] falls on — what the peak and night windows are compared to. */
    public fun localTime(at: Timestamp, zone: TimeZone = ZONE): LocalTime = at.toLocalDateTime(zone).time

    /** Midnight at the start of [date] in Colombo, as an instant. */
    public fun startOfDay(date: BusinessDate, zone: TimeZone = ZONE): Timestamp = date.atStartOfDayIn(zone)

    /** Whether two instants land on the same Colombo day — the D-13 "first trip of the day" test. */
    public fun isSameBusinessDay(first: Timestamp, second: Timestamp, zone: TimeZone = ZONE): Boolean =
        businessDate(first, zone) == businessDate(second, zone)

    /** The first of [date]'s month — the shape every `period_month` column is CHECKed into (C005). */
    public fun firstOfMonth(date: BusinessDate): BusinessDate = LocalDate(date.year, date.month, 1)

    /**
     * [date] plus [months] calendar months, **clamped to the end of the target month**.
     *
     * 31 January plus one month is 28 February (29 in a leap year), not an error and not 3 March.
     * That is kotlinx-datetime's own behaviour and it is the behaviour a monthly billing anchor
     * needs: a subscriber who joined on the 31st is billed on the last day of a short month and is
     * back on the 31st the month after, because each roll is computed from the anchor rather than
     * from the clamped result.
     */
    public fun plusMonths(date: BusinessDate, months: Int = 1): BusinessDate = date.plus(months, DateTimeUnit.MONTH)

    /** [date] plus [days] calendar days. */
    public fun plusDays(date: BusinessDate, days: Int = 1): BusinessDate = date.plus(days, DateTimeUnit.DAY)
}

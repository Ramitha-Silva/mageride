package lk.mageride.driver.earnings

import kotlinx.datetime.toJavaLocalDate
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.util.BusinessCalendar
import java.time.ZoneId
import java.time.temporal.ChronoUnit
import java.util.Locale
import java.time.Instant as JavaInstant

/**
 * One bar of SCR-DA-020's chart.
 *
 * @property label What sits under the bar — an hour (`14`) or a day of the month (`18`). A number,
 *   so it is not translated.
 * @property netMinor What the driver kept in this bucket, minor units.
 * @property current Whether this is the bucket the clock is in. The wireframe highlights one bar;
 *   this is which.
 */
internal data class EarningsBucket(val label: String, val netMinor: Long, val current: Boolean)

/**
 * D2' §SCR-DA-020's *"earnings trend"*, computed from the per-trip rows.
 *
 * **The buckets are Asia/Colombo, always** (D-13, D-38). `?period=today` is evaluated in Colombo by
 * query-svc, so a chart bucketed in the handset's zone would draw the server's day across two of
 * its own bars.
 *
 * **A day is too coarse for a shift and an hour is too fine for a month**, so the grain follows the
 * period: [EarningsPeriod.TODAY] is bucketed by hour across the hours the driver actually worked,
 * and a week or a month by calendar day across the window the summary reports. Both are derived
 * from the same `GET …/sessions` rows the breakdown list is drawn from — there is no second read
 * and no second arithmetic.
 */
internal object EarningsBuckets {

    /** `Asia/Colombo`, from `:shared`'s calendar so there is one definition of the zone. */
    val ZONE: ZoneId = ZoneId.of(BusinessCalendar.ZONE.id)

    /**
     * The bars for [period].
     *
     * @param from First Colombo day in the window — the summary's `rangeFrom`.
     * @param to Last Colombo day in the window — the summary's `rangeTo`.
     * @param now The clock, for deciding which bucket is [EarningsBucket.current].
     */
    fun of(
        period: EarningsPeriod,
        sessions: List<SessionEarning>,
        from: BusinessDate,
        to: BusinessDate,
        now: Timestamp,
    ): List<EarningsBucket> = if (period == EarningsPeriod.TODAY) {
        hourly(sessions, now)
    } else {
        daily(sessions, from, to, now)
    }

    /**
     * One bar per hour worked, from the first trip's hour through the current one.
     *
     * A single bar for a whole day would be a total wearing a chart's clothes; the shift's shape is
     * the only trend a day has. The span always reaches **now**, so an idle afternoon reads as a
     * run of empty bars rather than as a day that ended at lunchtime.
     */
    private fun hourly(sessions: List<SessionEarning>, now: Timestamp): List<EarningsBucket> {
        val currentHour = hourOf(now)
        val hours = sessions.map { hourOf(it.endedAt) }
        val first = (hours.minOrNull() ?: currentHour).coerceAtMost(currentHour)

        val totals = sessions.groupBy { hourOf(it.endedAt) }.mapValues { (_, rows) -> rows.sumOf { it.netMinor } }

        return (first..currentHour).map { hour ->
            EarningsBucket(
                label = String.format(Locale.ROOT, "%02d", hour),
                netMinor = totals[hour] ?: 0L,
                current = hour == currentHour,
            )
        }
    }

    /** One bar per Colombo day in `[from, to]`, with the day the clock is in highlighted. */
    private fun daily(
        sessions: List<SessionEarning>,
        from: BusinessDate,
        to: BusinessDate,
        now: Timestamp,
    ): List<EarningsBucket> {
        val start = from.toJavaLocalDate()
        val end = to.toJavaLocalDate()
        if (end.isBefore(start)) return emptyList()

        val today = zoned(now).toLocalDate()
        val totals = sessions
            .groupBy { zoned(it.endedAt).toLocalDate() }
            .mapValues { (_, rows) -> rows.sumOf { it.netMinor } }

        return (0..ChronoUnit.DAYS.between(start, end)).map { offset ->
            val day = start.plusDays(offset)
            EarningsBucket(
                label = day.dayOfMonth.toString(),
                netMinor = totals[day] ?: 0L,
                current = day == today,
            )
        }
    }

    private fun hourOf(at: Timestamp): Int = zoned(at).hour

    private fun zoned(at: Timestamp) = JavaInstant.ofEpochMilli(at.toEpochMilliseconds()).atZone(ZONE)
}

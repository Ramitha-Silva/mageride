package lk.mageride.driver.sharing

import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.shared.data.models.Timestamp
import java.time.LocalTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.time.ExperimentalTime
import kotlin.time.Instant
import java.time.Instant as JavaInstant

/**
 * SCR-DA-028's **Expiry** — the day a share grant lapses, in Colombo.
 *
 * Pure Kotlin and testable on this host, because two conversions in a row are what makes a grant
 * end on the wrong day:
 *
 * 1. **M3's `DatePickerState` answers UTC midnight of the day that was tapped.** Read in any other
 *    zone that instant is a different date — in `Asia/Colombo`, `+05:30`, it is 05:30 on the same
 *    morning, which is *before* most of the day the driver chose.
 * 2. **A grant should lapse at the END of the chosen day, not at its start.** A driver who picks
 *    30 June means "through the 30th". Sending Colombo midnight would revoke the passenger a whole
 *    day early, and US-4.8's auto-revoke is the server acting on exactly this value.
 *
 * So the picked instant is read as a *date* in UTC — which is the only zone that round-trips what
 * the picker meant — and then closed at 23:59:59.999 in [ScheduleLabels.ZONE], the same Colombo
 * zone every clock in this app resolves from `:shared`'s `BusinessCalendar` (D-38).
 */
@OptIn(ExperimentalTime::class)
internal object ShareExpiry {

    /** The last instant of the day [utcMidnightMillis] names, in Colombo. */
    fun endOfDay(utcMidnightMillis: Long): Timestamp {
        val day = JavaInstant.ofEpochMilli(utcMidnightMillis).atZone(ZoneOffset.UTC).toLocalDate()
        val lapses = day.atTime(LocalTime.MAX).atZone(ScheduleLabels.ZONE).toInstant()
        return Instant.fromEpochMilliseconds(lapses.toEpochMilli())
    }

    /**
     * `30 Jun 2026` — an expiry, printed.
     *
     * The year is carried where `ScheduleLabels.date` drops it: a wallet line is always in the past
     * and its year is obvious, while a grant can be set to lapse in a year's time and *"30 Jun"*
     * would then be genuinely ambiguous. The month abbreviation is ICU's in the driver's language,
     * for the same reason it is there.
     */
    fun label(at: Timestamp, locale: Locale): String = DateTimeFormatter
        .ofPattern(PATTERN, locale)
        .format(JavaInstant.ofEpochMilli(at.toEpochMilliseconds()).atZone(ScheduleLabels.ZONE))

    private const val PATTERN = "d MMM yyyy"
}

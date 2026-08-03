package lk.mageride.driver.jobs

import lk.mageride.driver.ui.Symbols
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.util.BusinessCalendar
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit
import java.util.Locale
import java.time.Instant as JavaInstant

/**
 * Which calendar day a scheduled pickup falls on, relative to now.
 *
 * [Today] and [Tomorrow] are **copy** and are resolved from `strings.xml` by the screen; [On] is
 * **data** — a rendered date, whose month abbreviation comes from the platform's own locale data
 * rather than from three hand-written translations. Splitting the type this way is what keeps
 * `StringResourceTest`'s rule intact: a value identical in all three files is a symbol, and a
 * sentence is not.
 */
internal sealed interface DayLabel {

    /** The pickup is on the current Colombo day. */
    data object Today : DayLabel

    /** The next Colombo day. The wireframe's *"Tomorrow · 11.2 km · Sedan"*. */
    data object Tomorrow : DayLabel

    /**
     * Any other day, already rendered.
     *
     * @property text `18 Jun` — day-of-month and the abbreviated month in the driver's language.
     */
    data class On(val text: String) : DayLabel
}

/**
 * The clock and calendar SCR-DA-017 and SCR-DA-018 print, in **Asia/Colombo**.
 *
 * **Every pickup time is read in Colombo, never in the handset's zone** (D-38). A driver whose phone
 * came back from a trip abroad still on its old zone would otherwise be shown a board sorted
 * correctly and labelled five and a half hours wrong, and would miss the T-30 window on a ride whose
 * card said tomorrow.
 *
 * The wall-clock format is fixed 24-hour (`14:00`) because that is what the wireframe prints and
 * because a clock reading is a number, not a sentence — the same rule `Rs` and `+94` follow (C068).
 * The month abbreviation is the one exception and is genuinely locale data: `java.time` carries
 * Sinhala and Tamil month names, and hand-writing them into `strings.xml` would be re-typing ICU.
 */
internal object ScheduleLabels {

    /** `Asia/Colombo`, resolved from `:shared`'s calendar so there is one definition of the zone. */
    val ZONE: ZoneId = ZoneId.of(BusinessCalendar.ZONE.id)

    /** What separates a pickup from its drop on a card. A symbol, so it is not translated. */
    const val ROUTE_ARROW: String = "→"

    /** What a card prints where the server sent no address. See [Symbols.UNKNOWN]. */
    const val UNKNOWN: String = Symbols.UNKNOWN

    /** `08:30` — the pickup's Colombo wall-clock time. */
    fun time(at: Timestamp): String = TIME.format(zoned(at))

    /**
     * `17 Jun` — the Colombo calendar date [at] falls on, always rendered (Δ C073).
     *
     * [day] is for a list where *"Today"* and *"Tomorrow"* carry information; a ledger is not one —
     * every line is in the past, and a wallet history that said *"Today"* on six rows would have
     * hidden which of them came first.
     */
    fun date(at: Timestamp, locale: Locale): String = DateTimeFormatter.ofPattern(DAY_PATTERN, locale).format(zoned(at))

    /**
     * Which day [at] falls on, seen from [now].
     *
     * Compared as **Colombo dates** rather than as a duration: a pickup nine hours away is
     * "tomorrow" at 22:00 and "today" at 06:00, and only the calendar knows which.
     */
    fun day(at: Timestamp, now: Timestamp, locale: Locale): DayLabel {
        val pickup = zoned(at).toLocalDate()
        val today = zoned(now).toLocalDate()

        return when (ChronoUnit.DAYS.between(today, pickup)) {
            0L -> DayLabel.Today
            1L -> DayLabel.Tomorrow
            else -> DayLabel.On(DateTimeFormatter.ofPattern(DAY_PATTERN, locale).format(zoned(at)))
        }
    }

    /**
     * `Maharagama → Fort`.
     *
     * `Place.address` is server-supplied display text and is **absent on every scheduled ride**:
     * `POST /v1/rides/schedule` takes bare coordinates, so dispatch-svc has no address to echo
     * (`PlaceResponse`'s own remark). The dash is what a card shows rather than a pair of decimal
     * degrees no driver can read — recorded as a spec gap in the C072 handoff.
     */
    fun route(pickup: Place, dropoff: Place): String =
        "${pickup.address ?: UNKNOWN} $ROUTE_ARROW ${dropoff.address ?: UNKNOWN}"

    private fun zoned(at: Timestamp) = JavaInstant.ofEpochMilli(at.toEpochMilliseconds()).atZone(ZONE)

    private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm", Locale.ROOT)

    /** `18 Jun`. Localised, because a month name is one of the few things ICU knows and we do not. */
    private const val DAY_PATTERN = "d MMM"
}

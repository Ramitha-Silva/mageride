package lk.mageride.passenger.ui

import lk.mageride.shared.data.models.BusinessDate
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * `2026-07-06` → `6 Jul` and `Jul 2026` — the two date shapes the wireframes print.
 *
 * **A month name is data the platform localises, not copy this app writes.** Thirteen keys in
 * three `strings.xml` files would be thirty-nine values to keep in step with CLDR, and Sinhala and
 * Tamil month names are already on every Android 8.0 handset. The same argument `MoneyFormat`
 * makes in the other direction: `Rs` is a printed symbol so it is a Kotlin constant, and a month
 * is a translated word so it comes from the locale.
 *
 * **Every date here is already Asia/Colombo** (D-38). `BusinessDate` is a calendar date the server
 * derived in Colombo — `next_due`, `period_month`, `fee_date` — so nothing in this file converts a
 * zone, and nothing may: re-deriving one of these from the handset's clock is wrong for five and a
 * half hours a day. Formatting is all this does.
 *
 * The locale comes from the JVM default, which `MainActivity.attachBaseContext` has already set
 * through `PassengerLocale.wrap` — so a passenger reading the app in Sinhala reads Sinhala months.
 */
internal object DateFormat {

    /** `Jul 2026` — SCR-PA-025b's month column and SCR-PA-025a's period line. */
    fun monthYear(date: BusinessDate): String = format(date, MONTH_YEAR)

    /** `6 Jul` — SCR-PA-025's *"next due"* and the payment row's date. */
    fun dayMonth(date: BusinessDate): String = format(date, DAY_MONTH)

    /**
     * `01:24` — SCR-PA-028's call timer (Δ C084).
     *
     * Minutes and seconds, zero-padded, and **not** localised: these are digits on a running clock
     * rather than a date, and every locale this app ships in prints them the same way. A call that
     * has run past an hour keeps counting in minutes (`61:05`) rather than growing a third field —
     * the wireframe draws two, and a layout that changes width mid-call is worse than a large
     * number.
     */
    fun timer(totalSeconds: Long): String {
        val safe = totalSeconds.coerceAtLeast(0)
        val minutes = safe / SECONDS_PER_MINUTE
        val seconds = safe % SECONDS_PER_MINUTE
        return "${minutes.pad()}:${seconds.pad()}"
    }

    private fun Long.pad(): String = toString().padStart(2, '0')

    /**
     * Formats through `java.time`, reached from the ISO text rather than field by field.
     *
     * `kotlinx.datetime.LocalDate.toString()` is ISO-8601 and `java.time.LocalDate.parse` reads
     * exactly that, so the bridge holds across kotlinx-datetime's accessor renames — which is
     * worth more here than saving an allocation on a list row.
     */
    private fun format(date: BusinessDate, pattern: String): String = java.time.LocalDate.parse(date.toString())
        .format(DateTimeFormatter.ofPattern(pattern, Locale.getDefault()))

    private const val MONTH_YEAR = "MMM yyyy"
    private const val DAY_MONTH = "d MMM"
    private const val SECONDS_PER_MINUTE = 60L
}

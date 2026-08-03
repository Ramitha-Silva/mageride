package lk.mageride.driver.ui

import lk.mageride.shared.data.models.Money
import java.util.Locale

/**
 * Rendering money for a driver — `Rs 1,240`, `Rs 480.50`.
 *
 * **`Money` deliberately does not format itself** (C012): rendering needs a locale and a symbol,
 * and no user-facing string may live in `:shared`. This is the app's half of that split, and it is
 * one function so that the app bar's balance, the offer's fare and the fee chip all group their
 * thousands the same way.
 *
 * **`Rs` is data, not copy.** Three identical values in the three `strings.xml` files is exactly
 * what `StringResourceTest` fails on, and the currency prefix is a proper noun — the same rule
 * `+94` and the language endonyms follow (C068).
 *
 * The grouping is fixed to [Locale.ROOT] rather than the handset's, because the number belongs to
 * a Sri Lankan rupee amount and a driver who set their phone to a locale that groups by lakhs must
 * still read the same figure the receipt shows.
 */
internal object MoneyFormat {

    /** The currency prefix D2' §A prints on every driver-facing amount. */
    const val PREFIX: String = "Rs"

    /**
     * What is drawn where an amount is not known yet — a read in flight, or one that failed.
     *
     * An em dash rather than `Rs 0`: zero is a balance a driver can have and being told they have
     * it when nothing was read is worse than being told nothing. A symbol, so it is not translated
     * (C073); the character itself is [Symbols.UNKNOWN], which C074 made the one place it is
     * spelled.
     */
    const val EMPTY: String = Symbols.UNKNOWN

    /** `48000` → `Rs 480`; `48050` → `Rs 480.50`. Whole rupees lose their `.00`. */
    fun rupees(money: Money): String = rupees(money.amountMinor)

    /** The same, for a bare `…Minor` field that has no [Money] wrapper on the wire. */
    fun rupees(amountMinor: Long): String {
        val negative = amountMinor < 0
        val absolute = if (negative) -amountMinor else amountMinor
        val whole = absolute / MINOR_UNITS
        val cents = absolute % MINOR_UNITS

        val grouped = String.format(Locale.ROOT, "%,d", whole)
        val text = if (cents == 0L) grouped else String.format(Locale.ROOT, "%s.%02d", grouped, cents)
        return if (negative) "$PREFIX -$text" else "$PREFIX $text"
    }

    /**
     * `1000` basis points → `10%`, `1250` → `12.5%`.
     *
     * The voucher ladder's discounts are stored in basis points because a percentage of money has
     * to survive `FareRounding` as an exact rational (C016); a driver reads a percentage. The
     * trailing `.0` is dropped so the common whole-percent tier prints as the wireframe draws it.
     */
    fun percentOfBps(basisPoints: Int): String {
        val whole = basisPoints / BPS_IN_PERCENT
        val fraction = (basisPoints % BPS_IN_PERCENT).toString().padStart(2, '0').trimEnd('0')
        return if (fraction.isEmpty()) "$whole%" else "$whole.$fraction%"
    }

    /** `1240.0` metres → `1.2 km`, and anything under a kilometre → `240 m`. */
    fun distance(metres: Double): String = if (metres >= METRES_IN_KM) {
        String.format(Locale.ROOT, "%.1f km", metres / METRES_IN_KM)
    } else {
        String.format(Locale.ROOT, "%.0f m", metres)
    }

    /**
     * `30000` → `30 km`. A **radius**, not a measured distance.
     *
     * Distinct from [distance] because the two are different kinds of number: a distance is measured
     * and its tenth of a kilometre is information, while the Job Board's catchment is a round figure
     * a spec fixed (D-06's 30 km), and `≤ 30.0 km` reads as a measurement of something.
     */
    fun radius(metres: Int): String = String.format(Locale.ROOT, "%d km", metres / METRES_IN_KM.toInt())

    /** `01:12:40` — SCR-DA-011's live session timer (US-5.6). */
    fun clock(seconds: Long): String {
        val safe = if (seconds < 0) 0 else seconds
        return String.format(
            Locale.ROOT,
            "%02d:%02d:%02d",
            safe / SECONDS_IN_HOUR,
            (safe % SECONDS_IN_HOUR) / SECONDS_IN_MINUTE,
            safe % SECONDS_IN_MINUTE,
        )
    }

    /**
     * `00:42` — SCR-DA-031's call timer (Δ C075).
     *
     * Minutes and seconds, with the hours rolled into the minutes rather than dropped: a call is
     * measured from zero and a `01:12:40` would be [clock]'s shape, which belongs to a *session*
     * that started at a wall-clock time. Nothing on this platform bills a call, so an hour-long one
     * reading `62:15` is honest and needs no third field.
     */
    fun timer(seconds: Long): String {
        val safe = if (seconds < 0) 0 else seconds
        return String.format(Locale.ROOT, "%02d:%02d", safe / SECONDS_IN_MINUTE, safe % SECONDS_IN_MINUTE)
    }

    /** `1:42` — the Directional banner's *"1:42 left"*, hours and minutes only. */
    fun countdown(seconds: Long): String {
        val safe = if (seconds < 0) 0 else seconds
        return String.format(
            Locale.ROOT,
            "%d:%02d",
            safe / SECONDS_IN_HOUR,
            (safe % SECONDS_IN_HOUR) / SECONDS_IN_MINUTE,
        )
    }

    private const val MINOR_UNITS = 100L
    private const val BPS_IN_PERCENT = 100
    private const val METRES_IN_KM = 1_000.0
    private const val SECONDS_IN_HOUR = 3_600L
    private const val SECONDS_IN_MINUTE = 60L
}

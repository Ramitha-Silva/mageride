package lk.mageride.passenger.ui

import lk.mageride.shared.data.models.Money

/**
 * `48000` → `Rs 480`; `48050` → `Rs 480.50`.
 *
 * **Money is integer minor units everywhere** (root CLAUDE.md) and nothing on the wire is ever a
 * decimal, so the only place rupees exist is here, one step before a `Text`. Doing it at the call
 * site would be the arithmetic repeated on every screen that shows a fare — which is most of them.
 *
 * **`Rs` is not translated.** It is the currency symbol printed in D2' §A, and three identical
 * values in the three `strings.xml` files is what `StringResourceTest` reads as a translation
 * nobody did — the same argument `LanguageNames` and `PhoneNumber` make for their constants.
 *
 * The driver app has its own `MoneyFormat` and this is deliberately a second copy rather than a
 * shared one: `:shared` is `commonMain` and has neither `java.util.Locale` nor `String.format`, and
 * the two apps' number groupings are free to diverge. Recorded in the C079 handoff.
 */
internal object MoneyFormat {

    /** The currency prefix D2' §A prints on every passenger-facing amount. */
    const val PREFIX: String = "Rs"

    /** What is drawn where an amount is not known yet — a quote in flight, or one that failed. */
    const val EMPTY: String = "—"

    fun rupees(money: Money): String = rupees(money.amountMinor)

    /** The same, for a bare `…Minor` field with no [Money] wrapper on the wire. */
    fun rupees(amountMinor: Long): String {
        val negative = amountMinor < 0
        val absolute = if (negative) -amountMinor else amountMinor
        val text = group(absolute / MINOR_UNITS) + centsOf(absolute % MINOR_UNITS)
        return if (negative) "$PREFIX -$text" else "$PREFIX $text"
    }

    /** Thousands separators, by hand — `commonMain` has no locale-aware formatter to borrow. */
    private fun group(whole: Long): String = whole.toString()
        .reversed()
        .chunked(GROUP_SIZE)
        .joinToString(",")
        .reversed()

    /** Whole rupees lose their `.00`; anything else keeps two digits. */
    private fun centsOf(cents: Long): String = if (cents == 0L) "" else ".${cents.toString().padStart(2, '0')}"

    private const val MINOR_UNITS = 100L
    private const val GROUP_SIZE = 3
}

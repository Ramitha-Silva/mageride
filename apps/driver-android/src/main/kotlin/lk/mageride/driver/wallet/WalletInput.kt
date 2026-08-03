package lk.mageride.driver.wallet

import lk.mageride.driver.ui.PlatformId
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid

/**
 * What a driver types on SCR-DA-022, SCR-DA-023 and SCR-DA-024, and what it means.
 *
 * Pure Kotlin and testable on this host, for the same reason `CropQuad` is (C069): the two
 * questions these screens ask — *is that a Driver ID?* and *how much is that?* — are the only
 * places a keystroke becomes money, and neither should be answered inside a composable.
 *
 * ### The Driver ID is the platform id, and there is no `DRV-22011`
 *
 * The wireframe prints `DRV-22011` in both entry fields. **No such identifier exists.** Every
 * driver-facing transfer route takes a `Ulid` — `_shared.yaml#/components/schemas/Ulid`, 26–36
 * characters of the Crockford alphabet plus a hyphen — `registry.driver_profiles` carries no
 * human-readable code column, and no route on the platform resolves one form into the other. So
 * the field takes the id the platform uses, checked here against the contract's own pattern so a
 * mistyped one is answered at the keyboard rather than by a `404` after an attested POST.
 *
 * The id is **trimmed and never rewritten**: `Primitives.kt` is explicit that a client which
 * silently reshaped a server-supplied identifier is a worse failure than one that passed it
 * through, and a ULID is upper-case while a UUID is lower-case — case-folding either would break
 * the other. Recorded as a spec gap in the C073 handoff; SCR-DA-029 (C074) is the screen that
 * shows a driver their own id, which is how one gets into the other driver's hands.
 */
internal object WalletInput {

    /** `_shared.yaml#/components/schemas/Ulid` — `minLength`. */
    const val DRIVER_ID_MIN_LENGTH: Int = PlatformId.MIN_LENGTH

    /** The same schema's `maxLength`; a UUID with its hyphens is 36. */
    const val DRIVER_ID_MAX_LENGTH: Int = PlatformId.MAX_LENGTH

    /**
     * The most rupees a field accepts, as a digit count.
     *
     * Nine digits — Rs 999,999,999 — is far above any denomination on sale and far below where
     * `amountMinor` (an `int64`) could overflow. It exists so a driver leaning on the keyboard gets
     * a field that stops rather than an amount the gateway rejects.
     */
    const val MAX_RUPEE_DIGITS: Int = 9

    /**
     * The id as it will be sent — surrounding whitespace removed, nothing else touched.
     *
     * Whitespace only: a paste out of a chat app carries a trailing space and a newline, and
     * neither is part of anybody's identity.
     */
    fun driverId(raw: String): Ulid = PlatformId.of(raw)

    /**
     * Whether [raw] is a well-formed platform id, once trimmed.
     *
     * The pattern moved to [PlatformId] in C074, when SCR-DA-028 needed the same question asked of
     * a *passenger* id. This is still the wallet cluster's door onto it.
     */
    fun isDriverId(raw: String): Boolean = PlatformId.isValid(raw)

    /**
     * What a rupee field keeps of a keystroke — digits, capped at [MAX_RUPEE_DIGITS].
     *
     * Group separators are dropped rather than rejected so a pasted `2,000` behaves; the field
     * renders the grouping itself. Whole rupees only, which is what every wireframe amount is and
     * what both gateways price in.
     */
    fun rupeeDigits(raw: String): String = raw.filter(Char::isDigit).trimStart('0').take(MAX_RUPEE_DIGITS)

    /**
     * [raw] as minor units, or `null` when there is no positive amount in it.
     *
     * `null` rather than zero: "nothing typed yet" and "typed a zero" are the same disabled CTA,
     * and neither is an amount worth sending.
     */
    fun amountMinor(raw: String): Long? = rupeeDigits(raw)
        .takeIf(String::isNotEmpty)
        ?.toLongOrNull()
        ?.takeIf { it > 0 }
        ?.times(MINOR_UNITS)

    /** [amountMinor] as `Money`, for the `:shared` rules that compare against a balance. */
    fun amount(raw: String): Money? = amountMinor(raw)?.let(Money::ofMinor)

    /** A whole-rupee amount back as the digits a field holds — how a voucher tile fills the box. */
    fun rupeesOf(amountMinor: Long): String = (amountMinor / MINOR_UNITS).toString()

    private const val MINOR_UNITS = 100L
}

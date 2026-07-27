package lk.mageride.shared.data.models

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * The only currency MageRide transacts in (`_shared.yaml#/components/schemas/Currency`).
 *
 * Declared `const: LKR` in the contract, so this enum has exactly one entry. It exists as a type
 * rather than a string constant because it is what stops a `currency` field from silently
 * carrying anything else.
 */
@Serializable
public enum class Currency {
    @SerialName("LKR")
    LKR,
}

/**
 * An amount of money, in **integer minor units** (Rs × 100).
 *
 * `48000` is Rs 480.00. This is the platform-wide rule (CLAUDE.md "Money as minor units",
 * D3' §0) and the reason it is a type: a `Double` cannot represent a cent exactly, so a fare
 * computed in floating point disagrees with the ledger the moment it is summed.
 * **A raw `Double` for money is a bug** (C012 fence).
 *
 * On the wire this is `_shared.yaml#/components/schemas/Money` — `{ amountMinor, currency }`.
 * Most D3' payloads spell money as a flat `…Minor` field beside a sibling `currency` instead of
 * nesting this object; those stay flat so the DTOs match the contract byte for byte, and
 * [ofMinor] / [MoneyHolder] bridge them into this type for the domain layers (C015–C016).
 *
 * **Never formats itself.** Rendering "Rs 480.00" needs a locale and a currency symbol in
 * Sinhala, Tamil and English, and no user-facing string may live in this module (C012 DoD) —
 * formatting is the apps' job.
 *
 * @property amountMinor Integer minor units. Non-negative in every contract that carries a
 *   [Money]; the ledger's signed columns (`WalletTransaction.amountMinor`) are deliberately not
 *   modelled as this type.
 * @property currency Always [Currency.LKR]. Forced onto the wire even though it is the default,
 *   because the contract marks it `required`.
 */
@OptIn(ExperimentalSerializationApi::class)
@Serializable
public data class Money(
    val amountMinor: Long,
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
) : Comparable<Money> {

    /** Sum of two amounts in the same currency. */
    public operator fun plus(other: Money): Money = combine(other, amountMinor + other.amountMinor)

    /** Difference of two amounts in the same currency; may go negative (a refund, a debt). */
    public operator fun minus(other: Money): Money = combine(other, amountMinor - other.amountMinor)

    /** Whole multiple of an amount — a per-km rate times a distance, a fare times a count. */
    public operator fun times(factor: Int): Money = copy(amountMinor = amountMinor * factor)

    override fun compareTo(other: Money): Int {
        require(currency == other.currency) { MIXED_CURRENCY }
        return amountMinor.compareTo(other.amountMinor)
    }

    private fun combine(other: Money, result: Long): Money {
        require(currency == other.currency) { MIXED_CURRENCY }
        return copy(amountMinor = result)
    }

    public companion object {
        /**
         * Guard message for the two-currency case.
         *
         * Not user-facing copy: it can only fire if a second [Currency] entry is ever added, and
         * it is a programming error rather than something an app renders.
         */
        private const val MIXED_CURRENCY: String = "Money operands must share a currency"

        /** Rs 0.00. */
        public val ZERO: Money = Money(amountMinor = 0L)

        /** Wraps a flat `…Minor` contract field as LKR money. */
        public fun ofMinor(amountMinor: Long): Money = Money(amountMinor = amountMinor)
    }
}

/**
 * A DTO that carries a flat `amountMinor` + `currency` pair rather than a nested [Money].
 *
 * Most D3' payloads are shaped that way (`FareEstimate`, `PaymentStatus`, `Wallet`, …). Rather
 * than reshape them — which would break the round-trip against the contract examples — each
 * implements this and exposes the same money through one accessor, so C015/C016 never reach for
 * a bare `Long`.
 */
public interface MoneyHolder {
    /** The amount this DTO is about, as a value type. */
    public val money: Money
}

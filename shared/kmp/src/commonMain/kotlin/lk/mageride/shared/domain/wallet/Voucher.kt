package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.VoucherDiscountTier
import lk.mageride.shared.data.models.subscription.VoucherPurchase
import lk.mageride.shared.domain.fare.FareRounding

// Bulk credit vouchers (US-9.19, AL-01, D5' §9.3, `billing.voucher_discount_tiers`).
//
// "Bulk credit vouchers Rs 1k–10k carry a commission % SET PER VOUCHER VALUE (denomination) by
//  Admin, applied ONLY AT PURCHASE and credited directly to the buyer's wallet (e.g., a 10%
//  voucher → pay Rs 900, wallet credited Rs 1,000). A 'reseller's' margin = that per-voucher-value
//  commission, NOT a per-driver or per-transfer commission."
//
// THE DISCOUNT LIVES ENTIRELY IN THE PRICE. The wallet is credited the FULL face value —
// `ck_voucher_credit_full` (C005) enforces `credited_minor = denomination_minor` in the database
// and [VoucherQuote] enforces it here. A voucher that credited less than its denomination would be
// a per-transfer commission wearing a different hat, which is exactly what AL-01 removed.
//
// THE DENOMINATIONS ARE SPEC; THE PERCENTAGES ARE NOT. URD US-9.19 pins Rs 1,000 / 2,000 / 3,000 /
// 5,000 / 10,000 and gives one worked rate (Rs 1,000 at 10%); everything else is Finance's to set
// in SCR-AP-007 (C005 decision 9). So this class holds no default ladder — a client with no tiers
// has nothing to sell, which is the honest answer.

/**
 * What one voucher denomination costs and credits.
 *
 * @property denomination Face value — what the wallet receives.
 * @property discountBps The tier's discount in basis points; `1000` is 10%.
 * @property price What the buyer pays.
 */
public data class VoucherQuote(val denomination: Money, val discountBps: Int, val price: Money) {
    init {
        require(price <= denomination) { "a voucher cannot cost more than it credits" }
        require(price >= Money.ZERO) { "a voucher price cannot be negative" }
    }

    /**
     * What the wallet is credited: **the full face value** (`ck_voucher_credit_full`, US-9.19).
     *
     * Deliberately not a stored field — it is the denomination by definition, and a field could
     * be set to something else.
     */
    public val credited: Money get() = denomination

    /** The buyer's saving — the informal reseller's entire margin (AL-01). */
    public val saving: Money get() = denomination - price
}

/**
 * The voucher tiers on sale.
 *
 * @param tiers What `GET /v1/wallet/voucher-discount-tiers` returned. Inactive tiers are kept so
 *   a receipt for a withdrawn denomination still renders, and excluded from [onSale] and [quote].
 */
public class VoucherCatalogue(tiers: List<VoucherDiscountTier>) {

    /** Every tier, active or not, in the order given. */
    public val tiers: List<VoucherDiscountTier> = tiers.toList()

    private val byDenomination: Map<Long, VoucherDiscountTier> = tiers.associateBy { it.denominationMinor }

    init {
        require(byDenomination.size == tiers.size) { "a catalogue holds one tier per denomination" }
        tiers.forEach {
            require(it.discountBps >= 0) { "a discount cannot be negative, was ${it.discountBps} bps" }
            require(it.discountBps <= FULL_DISCOUNT_BPS) { "a discount cannot exceed 100%, was ${it.discountBps} bps" }
            require(it.denominationMinor > 0) { "a denomination is positive, was ${it.denominationMinor}" }
        }
    }

    /** The denominations a driver may buy right now, cheapest first. */
    public val onSale: List<VoucherQuote>
        get() = tiers.filter { it.active }.sortedBy { it.denominationMinor }.map(::quoteOf)

    /**
     * What [denominationMinor] costs, or `null` when there is no active tier for it.
     *
     * `null` rather than "full price": a denomination Finance has withdrawn is not on sale, and
     * quietly selling it at 0% would be a price nobody set.
     */
    public fun quote(denominationMinor: Long): VoucherQuote? =
        byDenomination[denominationMinor]?.takeIf { it.active }?.let(::quoteOf)

    private fun quoteOf(tier: VoucherDiscountTier): VoucherQuote {
        val discountMinor = FareRounding.basisPointsOfMinor(tier.denominationMinor, tier.discountBps)
        return VoucherQuote(
            denomination = Money.ofMinor(tier.denominationMinor),
            discountBps = tier.discountBps,
            price = Money.ofMinor(tier.denominationMinor - discountMinor),
        )
    }

    public companion object {

        /** 10,000 bps — a 100% discount, the ceiling a tier may not exceed. */
        public const val FULL_DISCOUNT_BPS: Int = 10_000

        /** The `billing.journal_entries.kind` a purchase posts under (C005 CHECK). */
        public const val JOURNAL_KIND: String = "voucher_purchase"

        /**
         * The balanced entry a settled purchase posts (D-09).
         *
         * The **credited** amount is what moves in the ledger — the buyer's wallet gains the full
         * face value and the platform account gives it up. The discount is not a ledger event at
         * all: it is the gap between what the buyer paid the gateway and what MageRide credited,
         * and the gateway leg is reconciled separately (§9.3, "only OnePay/LankaQR gateway
         * settlement reconciliation remains").
         */
        public fun entryFor(purchase: VoucherPurchase, buyerDriverId: Ulid): LedgerEntry = LedgerEntry(
            kind = JOURNAL_KIND,
            idempotencyKey = "$JOURNAL_KIND:${purchase.purchaseId}",
            postings = listOf(
                LedgerPosting(LedgerAccount.PLATFORM, -purchase.creditedMinor),
                LedgerPosting(LedgerAccount.driver(buyerDriverId), purchase.creditedMinor),
            ),
        )
    }
}

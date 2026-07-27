package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.subscription.VoucherDiscountTier
import lk.mageride.shared.data.models.subscription.VoucherPurchase
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull

/**
 * Bulk credit vouchers (US-9.19, AL-01, D5' §9.3).
 *
 * The discount lives entirely in the **price**: the wallet is always credited the full face value
 * (`ck_voucher_credit_full`, C005). A voucher that credited less than its denomination would be a
 * per-transfer commission wearing a different hat, which is exactly what AL-01 removed.
 */
class VoucherTest {

    /** The §20 seed: five denominations, the Rs 1,000 rate pinned by US-9.19 and the rest defaults. */
    private val catalogue = VoucherCatalogue(
        listOf(
            VoucherDiscountTier(denominationMinor = 100_000, discountBps = 1_000, active = true),
            VoucherDiscountTier(denominationMinor = 200_000, discountBps = 1_100, active = true),
            VoucherDiscountTier(denominationMinor = 300_000, discountBps = 1_200, active = true),
            VoucherDiscountTier(denominationMinor = 500_000, discountBps = 1_300, active = true),
            VoucherDiscountTier(denominationMinor = 1_000_000, discountBps = 1_500, active = true),
        ),
    )

    @Test
    fun the_spec_worked_example_comes_out_exactly() {
        // US-9.19 and ADD §9.1 both use it: "a 10% voucher → pay Rs 900, wallet credited Rs 1,000".
        val quote = assertNotNull(catalogue.quote(100_000))

        assertEquals(Money.ofMinor(90_000), quote.price, "pay Rs 900")
        assertEquals(Money.ofMinor(100_000), quote.credited, "receive Rs 1,000")
        assertEquals(Money.ofMinor(10_000), quote.saving)
    }

    @Test
    fun every_seeded_tier_credits_its_full_face_value() {
        catalogue.onSale.forEach { quote ->
            assertEquals(quote.denomination, quote.credited, "${quote.denomination.amountMinor} credits in full")
        }
    }

    @Test
    fun the_five_denominations_are_priced_from_their_own_tier() {
        val expected = mapOf(
            100_000L to 90_000L,
            200_000L to 178_000L,
            300_000L to 264_000L,
            500_000L to 435_000L,
            1_000_000L to 850_000L,
        )

        expected.forEach { (denomination, price) ->
            assertEquals(Money.ofMinor(price), assertNotNull(catalogue.quote(denomination)).price)
        }
        assertEquals(expected.keys.sorted(), catalogue.onSale.map { it.denomination.amountMinor })
    }

    @Test
    fun a_withdrawn_denomination_is_not_quietly_sold_at_full_price() {
        val withInactive = VoucherCatalogue(
            listOf(VoucherDiscountTier(denominationMinor = 100_000, discountBps = 1_000, active = false)),
        )

        assertNull(withInactive.quote(100_000))
        assertEquals(emptyList(), withInactive.onSale)
        assertEquals(1, withInactive.tiers.size, "the tier is kept so a past receipt still renders")
    }

    @Test
    fun a_denomination_with_no_tier_at_all_has_no_price() {
        assertNull(catalogue.quote(400_000))
    }

    @Test
    fun a_fractional_discount_rounds_the_way_every_other_percentage_does() {
        // 12.5% of Rs 1,000 is Rs 125 exactly; the tie case is exercised in `FareRoundingTest`.
        val fine = VoucherCatalogue(
            listOf(VoucherDiscountTier(denominationMinor = 100_000, discountBps = 1_250, active = true)),
        )

        assertEquals(Money.ofMinor(87_500), assertNotNull(fine.quote(100_000)).price)
    }

    @Test
    fun a_catalogue_refuses_nonsense_tiers() {
        assertFailsWith<IllegalArgumentException> {
            VoucherCatalogue(listOf(VoucherDiscountTier(100_000, discountBps = -1, active = true)))
        }
        assertFailsWith<IllegalArgumentException> {
            VoucherCatalogue(listOf(VoucherDiscountTier(100_000, discountBps = 10_001, active = true)))
        }
        assertFailsWith<IllegalArgumentException> {
            VoucherCatalogue(listOf(VoucherDiscountTier(0, discountBps = 1_000, active = true)))
        }
        assertFailsWith<IllegalArgumentException> {
            VoucherCatalogue(
                listOf(
                    VoucherDiscountTier(100_000, discountBps = 1_000, active = true),
                    VoucherDiscountTier(100_000, discountBps = 1_100, active = true),
                ),
            )
        }
    }

    @Test
    fun a_purchase_moves_the_credited_amount_and_not_the_price() {
        // The discount is not a ledger event: the gateway leg is reconciled separately (§9.3), and
        // what moves in `billing.journal_entries` is the full face value.
        val buyer = "01JDRV0000000000000000001"
        val purchase = VoucherPurchase(
            purchaseId = "01JVCH0000000000000000001",
            denominationMinor = 100_000,
            discountBpsApplied = 1_000,
            paidMinor = 90_000,
            creditedMinor = 100_000,
        )

        val entry = VoucherCatalogue.entryFor(purchase, buyer)

        assertEquals(Money.ofMinor(100_000), entry.netFor(LedgerAccount.driver(buyer)))
        assertEquals(Money.ofMinor(-100_000), entry.netFor(LedgerAccount.PLATFORM))
        assertEquals("voucher_purchase:01JVCH0000000000000000001", entry.idempotencyKey)
        assertEquals(0L, entry.postings.sumOf { it.amountMinor })
    }

    @Test
    fun a_quote_cannot_cost_more_than_it_credits() {
        assertFailsWith<IllegalArgumentException> {
            VoucherQuote(
                denomination = Money.ofMinor(100_000),
                discountBps = 0,
                price = Money.ofMinor(110_000),
            )
        }
    }
}

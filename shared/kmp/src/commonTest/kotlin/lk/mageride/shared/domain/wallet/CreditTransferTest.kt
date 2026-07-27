package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.subscription.CreditTransfer
import lk.mageride.shared.data.models.subscription.CreditTransferDirection
import lk.mageride.shared.data.models.subscription.CreditTransferStatus
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Instant

/**
 * The C016 definition of done: **a credit transfer of X debits X and credits X in the projected
 * ledger with zero fee** (AL-01, US-9.13/9.21, D5' §9.3).
 *
 * "Reseller" is not a role, an account or a capability. Any commission on a transfer is a bug —
 * the informal reseller's entire margin is the bulk-voucher purchase discount, taken once at
 * purchase (see `VoucherTest`).
 */
class CreditTransferTest {

    private val sender = "01JDRV0000000000000000001"
    private val recipient = "01JDRV0000000000000000002"

    private fun transfer(
        amountMinor: Long,
        status: CreditTransferStatus = CreditTransferStatus.APPROVED,
        direction: CreditTransferDirection = CreditTransferDirection.DIRECT,
    ) = CreditTransfer(
        transferId = "01JTRF0000000000000000001",
        senderDriverId = sender,
        recipientDriverId = recipient,
        amountMinor = amountMinor,
        direction = direction,
        status = status,
        createdAt = Instant.parse("2026-07-27T06:30:00Z"),
    )

    @Test
    fun a_transfer_of_x_debits_x_and_credits_x_with_no_third_leg() {
        val entry = assertNotNull(CreditTransferRules.entryFor(transfer(250_000)))

        assertEquals(2, entry.postings.size, "two legs and no commission leg")
        assertEquals(0L, entry.postings.sumOf { it.amountMinor }, "the entry balances (D-09)")
        assertEquals(Money.ofMinor(-250_000), entry.netFor(LedgerAccount.driver(sender)))
        assertEquals(Money.ofMinor(250_000), entry.netFor(LedgerAccount.driver(recipient)))
        assertEquals(Money.ofMinor(250_000), entry.amount)
        assertEquals(Money.ZERO, entry.netFor(LedgerAccount.PLATFORM), "the platform takes nothing")
    }

    @Test
    fun the_two_legs_are_equal_at_every_amount() {
        // A percentage commission would show up as a gap at some magnitudes and not at others, so
        // the property is swept rather than sampled: one rupee to one million.
        val amounts = listOf(100L, 999L, 1_000L, 5_050L, 100_000L, 333_333L, 1_000_000L, 100_000_000L)

        amounts.forEach { amountMinor ->
            val entry = assertNotNull(CreditTransferRules.entryFor(transfer(amountMinor)))
            val debited = -entry.netFor(LedgerAccount.driver(sender)).amountMinor
            val credited = entry.netFor(LedgerAccount.driver(recipient)).amountMinor

            assertEquals(amountMinor, debited, "sender debited exactly")
            assertEquals(amountMinor, credited, "recipient credited exactly")
            assertEquals(debited, credited, "and the two are the same figure")
        }
    }

    @Test
    fun the_fee_is_zero_for_every_amount() {
        assertEquals(0, CreditTransferRules.COMMISSION_BPS)
        listOf(1L, 100L, 1_000_000L).forEach {
            assertEquals(Money.ZERO, CreditTransferRules.feeFor(Money.ofMinor(it)))
            assertEquals(Money.ofMinor(it), CreditTransferRules.debitedFromSender(Money.ofMinor(it)))
            assertEquals(Money.ofMinor(it), CreditTransferRules.creditedToRecipient(Money.ofMinor(it)))
        }
    }

    @Test
    fun nothing_posts_until_the_holder_approves() {
        // `ck_credit_transfers_posting` (C005): only an APPROVED transfer may carry a journal entry.
        assertNull(CreditTransferRules.entryFor(transfer(100_000, CreditTransferStatus.PENDING)))
        assertNull(CreditTransferRules.entryFor(transfer(100_000, CreditTransferStatus.REJECTED)))
        assertNotNull(CreditTransferRules.entryFor(transfer(100_000, CreditTransferStatus.APPROVED)))
    }

    @Test
    fun a_direct_send_posts_immediately_and_a_request_waits() {
        assertTrue(CreditTransferRules.postsImmediately(CreditTransferDirection.DIRECT))
        assertFalse(CreditTransferRules.postsImmediately(CreditTransferDirection.REQUESTED))
    }

    @Test
    fun the_idempotency_key_is_derived_from_the_transfer_not_minted() {
        // §0: a money key is composed from the business fact, so a redelivered approval collides
        // instead of crediting twice.
        val entry = assertNotNull(CreditTransferRules.entryFor(transfer(100_000)))

        assertEquals("driver_transfer:01JTRF0000000000000000001", entry.idempotencyKey)
        assertEquals("driver_transfer", entry.kind)
    }

    // ----------------------------------------------------------------------------------------
    // What the driver app checks before sending
    // ----------------------------------------------------------------------------------------

    private fun standing(availableMinor: Long, debtMinor: Long = 0) = WalletStanding(
        userId = sender,
        balance = Money.ofMinor(availableMinor + debtMinor),
        available = Money.ofMinor(availableMinor),
        outstandingDebt = Money.ofMinor(debtMinor),
    )

    @Test
    fun a_transfer_is_checked_against_the_spendable_balance_not_the_raw_one() {
        // A driver holding Rs 3,000 who owes Rs 2,000 can send Rs 1,000 and not Rs 2,500.
        val holding = standing(availableMinor = 100_000, debtMinor = 200_000)
        val intent = CreditTransferIntent(sender, recipient, Money.ofMinor(250_000))

        assertEquals(CreditTransferRejection.INSUFFICIENT_BALANCE, CreditTransferRules.rejectionFor(intent, holding))
        assertTrue(
            CreditTransferRules.canSend(CreditTransferIntent(sender, recipient, Money.ofMinor(100_000)), holding),
        )
    }

    @Test
    fun the_exact_balance_may_be_sent() {
        val holding = standing(availableMinor = 100_000)

        val exact = CreditTransferIntent(sender, recipient, Money.ofMinor(100_000))
        val overByOne = CreditTransferIntent(sender, recipient, Money.ofMinor(100_001))

        assertTrue(CreditTransferRules.canSend(exact, holding))
        assertFalse(CreditTransferRules.canSend(overByOne, holding))
    }

    @Test
    fun a_self_transfer_and_a_non_positive_amount_cannot_even_be_expressed() {
        assertFailsWith<IllegalArgumentException> { CreditTransferIntent(sender, sender, Money.ofMinor(100)) }
        assertFailsWith<IllegalArgumentException> { CreditTransferIntent(sender, recipient, Money.ZERO) }
        assertFailsWith<IllegalArgumentException> {
            CreditTransferIntent(sender, recipient, Money.ofMinor(-100))
        }
    }
}

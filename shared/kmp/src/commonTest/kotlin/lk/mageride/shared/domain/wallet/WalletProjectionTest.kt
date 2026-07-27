package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.Instant

/**
 * The wallet balance, its alerts (US-9.9) and the deduplicated history (US-9A.19).
 */
class WalletProjectionTest {

    private val driver = "01JDRV0000000000000000001"

    private fun wallet(balanceMinor: Long, availableMinor: Long = balanceMinor, debtMinor: Long? = null) = Wallet(
        userId = driver,
        balanceMinor = balanceMinor,
        availableMinor = availableMinor,
        outstandingDebtMinor = debtMinor,
    )

    private fun line(entryId: Ulid, amountMinor: Long, balanceAfterMinor: Long) = WalletTransaction(
        transactionId = "tx-$entryId",
        entryId = entryId,
        kind = "topup",
        amountMinor = amountMinor,
        balanceAfterMinor = balanceAfterMinor,
        occurredAt = Instant.parse("2026-07-27T06:30:00Z"),
    )

    @Test
    fun the_projection_takes_the_servers_figures() {
        val standing = WalletStanding.of(wallet(balanceMinor = 300_00, availableMinor = 100_00, debtMinor = 200_00))

        assertEquals(Money.ofMinor(30_000), standing.balance)
        assertEquals(Money.ofMinor(10_000), standing.available)
        assertEquals(Money.ofMinor(20_000), standing.outstandingDebt)
    }

    @Test
    fun affordability_is_asked_of_the_spendable_balance() {
        // A driver holding Rs 300 who owes Rs 200 can spend Rs 100. Offering them a Rs 250 transfer
        // would be describing money they do not have.
        val standing = WalletStanding.of(wallet(balanceMinor = 30_000, availableMinor = 10_000, debtMinor = 20_000))

        assertTrue(standing.canAfford(Money.ofMinor(10_000)))
        assertFalse(standing.canAfford(Money.ofMinor(10_001)))
        assertEquals(Money.ofMinor(15_000), standing.shortfallFor(Money.ofMinor(25_000)))
        assertEquals(Money.ZERO, standing.shortfallFor(Money.ofMinor(5_000)))
    }

    // ----------------------------------------------------------------------------------------
    // US-9.9 — "< Rs 200 → low-balance push; < Rs 0 → Top Up Required banner"
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_thresholds_are_the_ones_in_the_spec() {
        assertEquals(Money.ofMinor(20_000), WalletRules.DEFAULT_LOW_BALANCE_THRESHOLD)
    }

    @Test
    fun exactly_the_threshold_is_not_low_and_one_minor_unit_below_it_is() {
        assertEquals(WalletAlert.None, WalletRules.alertFor(WalletStanding.of(wallet(20_000))))

        val low = assertIs<WalletAlert.LowBalance>(WalletRules.alertFor(WalletStanding.of(wallet(19_999))))
        assertEquals(Money.ofMinor(20_000), low.threshold)
        assertEquals(Money.ofMinor(1), low.shortfall)
    }

    @Test
    fun a_zero_balance_is_low_but_not_overdrawn() {
        assertIs<WalletAlert.LowBalance>(WalletRules.alertFor(WalletStanding.of(wallet(0))))
        assertFalse(WalletStanding.of(wallet(0)).isOverdrawn)
    }

    @Test
    fun a_negative_balance_raises_the_top_up_banner() {
        val standing = WalletStanding.of(wallet(-5_000))

        val alert = assertIs<WalletAlert.TopUpRequired>(WalletRules.alertFor(standing))
        assertEquals(Money.ofMinor(5_000), alert.owed)
        assertTrue(standing.isOverdrawn)
    }

    @Test
    fun the_threshold_moves_with_the_admin_config() {
        // §9.4 says the threshold is admin-configurable; a build that baked Rs 200 in would
        // disagree with the server's push the day an operator changes it.
        val standing = WalletStanding.of(wallet(30_000))

        assertEquals(WalletAlert.None, WalletRules.alertFor(standing))
        assertIs<WalletAlert.LowBalance>(WalletRules.alertFor(standing, threshold = Money.ofMinor(50_000)))
    }

    @Test
    fun the_alert_reads_the_spendable_balance() {
        // Rs 500 held against Rs 400 of penalty debt leaves Rs 100 to spend, which is below the
        // threshold even though the raw balance is not.
        val standing = WalletStanding.of(wallet(balanceMinor = 50_000, availableMinor = 10_000, debtMinor = 40_000))

        assertIs<WalletAlert.LowBalance>(WalletRules.alertFor(standing))
    }

    // ----------------------------------------------------------------------------------------
    // US-9A.19 — history, deduplicated on the journal entry
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_redelivered_entry_does_not_append_a_second_line() {
        // The ledger event stream is at-least-once (C002 decision 3), and
        // `ux_wallet_tx_account_entry` is the database's own guard. This is the same rule on the
        // device.
        val history = WalletHistory()

        assertTrue(history.append(line("e1", 100_000, 100_000)))
        assertFalse(history.append(line("e1", 100_000, 100_000)), "same entry, redelivered")
        assertTrue(history.append(line("e2", -20_000, 80_000)))

        assertEquals(listOf("e1", "e2"), history.lines.map { it.entryId })
        assertTrue(history.contains("e1"))
    }

    @Test
    fun a_page_reports_how_many_lines_were_new() {
        val history = WalletHistory(listOf(line("e1", 100_000, 100_000)))

        val added = history.appendAll(listOf(line("e1", 100_000, 100_000), line("e2", -20_000, 80_000)))

        assertEquals(1, added)
        assertEquals(2, history.lines.size)
    }

    @Test
    fun a_debit_line_knows_it_is_one() {
        assertTrue(line("e3", -20_000, 80_000).isDebit)
        assertFalse(line("e4", 20_000, 100_000).isDebit)
    }
}

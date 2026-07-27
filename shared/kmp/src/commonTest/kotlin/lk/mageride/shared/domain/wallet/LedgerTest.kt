package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * The double-entry invariant (D-09, D5' §9.1).
 *
 * `billing.assert_balanced()` refuses an unbalanced entry at COMMIT (C005 decision 2); this is the
 * same guarantee stated on the way in, so a projected entry that did not balance could not be
 * constructed at all.
 */
class LedgerTest {

    private val driver = "01JDRV0000000000000000001"

    @Test
    fun an_entry_that_does_not_balance_cannot_be_built() {
        assertFailsWith<IllegalArgumentException> {
            LedgerEntry(
                kind = "topup",
                idempotencyKey = "topup:1",
                postings = listOf(
                    LedgerPosting(LedgerAccount.PLATFORM, -100),
                    LedgerPosting(LedgerAccount.driver(driver), 90),
                ),
            )
        }
    }

    @Test
    fun a_single_legged_entry_cannot_be_built_either() {
        assertFailsWith<IllegalArgumentException> {
            LedgerEntry("adjustment", "adj:1", listOf(LedgerPosting(LedgerAccount.PLATFORM, 0)))
        }
    }

    @Test
    fun a_balanced_entry_reports_its_sides() {
        val entry = LedgerEntry(
            kind = "daily_fee",
            idempotencyKey = "daily_fee:x",
            postings = listOf(
                LedgerPosting(LedgerAccount.driver(driver), -20_000),
                LedgerPosting(LedgerAccount.PLATFORM, 20_000),
            ),
        )

        assertEquals(1, entry.debits.size)
        assertEquals(1, entry.credits.size)
        assertEquals(Money.ofMinor(20_000), entry.amount)
        assertEquals(Money.ofMinor(20_000), entry.debits.single().magnitude)
        assertTrue(entry.debits.single().isDebit)
        assertEquals(Money.ofMinor(-20_000), entry.netFor(LedgerAccount.driver(driver)))
        assertEquals(Money.ZERO, entry.netFor(LedgerAccount.driver("01JDRV0000000000000000009")))
    }

    @Test
    fun a_platform_side_account_carries_no_owner_and_a_driver_account_must() {
        // `ck_accounts_owner_id` (C005 decision 3): the platform and suspense accounts are
        // singletons with no owner, and a driver or fleet account always names one.
        assertFailsWith<IllegalArgumentException> { LedgerAccount(LedgerAccountKind.PLATFORM, driver) }
        assertFailsWith<IllegalArgumentException> { LedgerAccount(LedgerAccountKind.DRIVER) }

        assertEquals(LedgerAccount.PLATFORM, LedgerAccount(LedgerAccountKind.PLATFORM))
        assertTrue(LedgerAccountKind.DRIVER.hasOwner)
        assertTrue(!LedgerAccountKind.SUSPENSE.hasOwner)
    }

    @Test
    fun there_is_no_reseller_account_kind() {
        // AL-01 dropped `owner_type='reseller'`: a reselling driver uses their ordinary driver
        // wallet, which is what makes a per-transfer commission unrepresentable.
        assertEquals(
            setOf("driver", "fleet", "platform", "suspense"),
            LedgerAccountKind.entries.mapTo(mutableSetOf()) { it.wire },
        )
    }
}

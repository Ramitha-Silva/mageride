package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.wallet.Wallet
import lk.mageride.shared.data.models.wallet.WalletTransaction

// The driver wallet, projected (D-09, D-08, US-9.7/9.9, D5' §9.2/§9.4).
//
// THE LEDGER IS THE TRUTH AND THE SERVER HOLDS IT. `billing.journal_entries` plus its balanced
// postings are authoritative; `billing.accounts.balance_minor` is a materialised read model of
// them, and `GET /v1/wallet/{userId}` is a read of that. Nothing here computes a balance from
// scratch — it holds the server's figure and applies the history lines the server sends.
//
// SPENDABLE, NOT NOMINAL. Every affordability question in this package asks about
// [WalletStanding.available] — the balance net of accrued penalty debt (D-05) — because that is
// what the D-08 daily-fee gate actually checks. A driver holding Rs 300 who owes Rs 200 can spend
// Rs 100, and a screen that offered them a Rs 250 transfer would be describing money they do not
// have.

/**
 * A wallet, as the client understands it.
 *
 * @property userId Whose wallet.
 * @property balance The ledger balance.
 * @property available The balance net of [outstandingDebt] — what may actually be spent.
 * @property outstandingDebt Accrued but unsettled cancellation penalties (D-05, AL-16).
 */
public data class WalletStanding(
    val userId: Ulid,
    val balance: Money,
    val available: Money,
    val outstandingDebt: Money = Money.ZERO,
) {

    /** Whether the wallet has gone negative — the "Top Up Required" case (US-9.9). */
    public val isOverdrawn: Boolean get() = available < Money.ZERO

    /** Whether [amount] can be spent right now. */
    public fun canAfford(amount: Money): Boolean = available >= amount

    /** What is still missing to afford [amount]; [Money.ZERO] when it is already affordable. */
    public fun shortfallFor(amount: Money): Money = if (canAfford(amount)) Money.ZERO else amount - available

    public companion object {

        /** Projects `GET /v1/wallet/{userId}` (US-9.7). */
        public fun of(wallet: Wallet): WalletStanding = WalletStanding(
            userId = wallet.userId,
            balance = Money(amountMinor = wallet.balanceMinor, currency = wallet.currency),
            available = Money(amountMinor = wallet.availableMinor, currency = wallet.currency),
            outstandingDebt = Money(amountMinor = wallet.outstandingDebtMinor ?: 0L, currency = wallet.currency),
        )
    }
}

/** What the wallet should be telling the driver right now (US-9.9, D5' §9.4). */
public sealed interface WalletAlert {

    /** Nothing to say. */
    public data object None : WalletAlert

    /**
     * Below the low-balance threshold — a push, and a nudge on the dashboard (US-9.9).
     *
     * @property threshold The threshold that was crossed, so the copy can name it.
     * @property shortfall How far below it the wallet is.
     */
    public data class LowBalance(val threshold: Money, val shortfall: Money) : WalletAlert

    /**
     * Negative balance — the persistent "Top Up Required" banner (US-9.9).
     *
     * A strictly stronger state than [LowBalance] and a different piece of UI, which is why it is
     * a separate case rather than a flag.
     *
     * @property owed How far below zero.
     */
    public data class TopUpRequired(val owed: Money) : WalletAlert
}

/**
 * The wallet rules a driver app renders.
 */
public object WalletRules {

    /** Rs 200 in minor units — the D5' §9.4 default, before an admin moves it. */
    private const val DEFAULT_LOW_BALANCE_THRESHOLD_MINOR: Long = 20_000

    /**
     * The default low-balance threshold, Rs 200 (US-9.9, D5' §9.4).
     *
     * **Admin-configurable** — §9.4 says so explicitly. Pass the server's figure to [alertFor]
     * rather than reading this as a constant; it exists for a client that has not read the config
     * yet, exactly like `DriverLevelRules.D5_DEFAULTS` (C015).
     */
    public val DEFAULT_LOW_BALANCE_THRESHOLD: Money = Money.ofMinor(DEFAULT_LOW_BALANCE_THRESHOLD_MINOR)

    /**
     * What to show for [standing].
     *
     * Boundaries follow §9.4 exactly: `< Rs 200` is low, `< Rs 0` is Top Up Required. A wallet
     * holding exactly the threshold is **not** low, and a wallet holding exactly zero is low but
     * not overdrawn.
     */
    public fun alertFor(standing: WalletStanding, threshold: Money = DEFAULT_LOW_BALANCE_THRESHOLD): WalletAlert =
        when {
            standing.available < Money.ZERO -> WalletAlert.TopUpRequired(Money.ZERO - standing.available)
            standing.available < threshold -> WalletAlert.LowBalance(threshold, threshold - standing.available)
            else -> WalletAlert.None
        }
}

/**
 * A driver's wallet history, deduplicated (US-9A.19).
 *
 * **Deduped on the journal entry, not on the transaction row.** The ledger event stream is
 * at-least-once (C002 decision 3) and `ux_wallet_tx_account_entry` (C005) is the database's own
 * guard; this is the same rule on the device, so a redelivered `wallet.credited` does not append a
 * second Rs 1,000 line to a screen the driver is already looking at.
 *
 * @param initial Lines already held, newest-first or oldest-first — order is preserved as given.
 */
public class WalletHistory(initial: List<WalletTransaction> = emptyList()) {

    private val seenEntryIds: MutableSet<Ulid> = mutableSetOf()
    private val ordered: MutableList<WalletTransaction> = mutableListOf()

    init {
        initial.forEach { append(it) }
    }

    /** The lines, in the order they were appended. */
    public val lines: List<WalletTransaction> get() = ordered.toList()

    /**
     * Appends [line] unless its journal entry has already been seen.
     *
     * @return `true` if it was new.
     */
    public fun append(line: WalletTransaction): Boolean {
        if (!seenEntryIds.add(line.entryId)) return false
        ordered += line
        return true
    }

    /**
     * Appends a page of lines.
     *
     * @return how many were new.
     */
    public fun appendAll(page: List<WalletTransaction>): Int = page.count { append(it) }

    /** Whether this entry is already on screen. */
    public fun contains(entryId: Ulid): Boolean = entryId in seenEntryIds
}

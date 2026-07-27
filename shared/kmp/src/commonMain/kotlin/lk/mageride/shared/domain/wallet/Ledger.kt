package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid

// The double-entry ledger, PROJECTED (D-09, D5' §9.1, `billing.journal_*`).
//
// "Every wallet mutation = a balanced billing.journal_entries (Σ postings = 0, DB trigger);
//  idempotent on idempotency_key. Materialised balance in billing.accounts."
//
// NOTHING HERE POSTS TO A LEDGER. wallet-svc and subscription-svc write `billing.journal_entries`
// inside a transaction whose COMMIT is guarded by `billing.assert_balanced()` (C005 decision 2).
// This is the client's model of what such an entry LOOKS like, and it exists for two jobs:
//
//   1. Explaining a wallet line to a driver — "Rs 1,000 in, from a transfer from D-4471" — from
//      the same shape the server posted.
//   2. Making the zero-commission rule (AL-01) a PROPERTY: a credit transfer's projected entry has
//      exactly two postings, they sum to zero, and their magnitudes are equal. A commission leg
//      would be a third posting, and this type's own invariant is what a test can point at.
//
// THE INVARIANT IS ENFORCED IN `init`, not checked by a caller. A `LedgerEntry` that does not
// balance cannot be constructed at all — which is the same guarantee the database trigger gives,
// stated in the type system on the way in rather than at COMMIT.

/**
 * Which kind of ledger account a posting lands in
 * (`billing.accounts.owner_type` CHECK, C005).
 *
 * There is deliberately **no `reseller`** (AL-01): a driver who has bought bulk credit and passes
 * it on is an ordinary driver with an ordinary driver account.
 *
 * @property wire The value as it appears in the CHECK constraint.
 */
public enum class LedgerAccountKind(public val wire: String) {

    /** A driver's wallet. `owner_id` is their `iam.users.id`. */
    DRIVER("driver"),

    /** A fleet organisation's wallet (AL-03). `owner_id` is the `registry.fleets.id`. */
    FLEET("fleet"),

    /** The platform's own account. A singleton with no owner (`ux_accounts_platform`). */
    PLATFORM("platform"),

    /** The suspense account — negative by construction. Also a singleton. */
    SUSPENSE("suspense"),
    ;

    /** Whether an account of this kind names an owner. Platform-side accounts do not. */
    public val hasOwner: Boolean get() = this == DRIVER || this == FLEET
}

/**
 * One side of a posting: which account.
 *
 * @property kind Whose account it is.
 * @property ownerId The driver or fleet id, or `null` for the two platform-side singletons —
 *   exactly the `ck_accounts_owner_id` rule (C005 decision 3).
 */
public data class LedgerAccount(val kind: LedgerAccountKind, val ownerId: Ulid? = null) {
    init {
        require(kind.hasOwner == (ownerId != null)) {
            "a ${kind.wire} account ${if (kind.hasOwner) "needs" else "must not carry"} an owner id"
        }
    }

    public companion object {

        /** The platform's singleton account. */
        public val PLATFORM: LedgerAccount = LedgerAccount(LedgerAccountKind.PLATFORM)

        /** A driver's wallet account. */
        public fun driver(driverId: Ulid): LedgerAccount = LedgerAccount(LedgerAccountKind.DRIVER, driverId)

        /** A fleet organisation's wallet account. */
        public fun fleet(fleetId: Ulid): LedgerAccount = LedgerAccount(LedgerAccountKind.FLEET, fleetId)
    }
}

/**
 * One leg of an entry (`billing.journal_postings`).
 *
 * @property account Where the money moved.
 * @property amountMinor **Signed** minor units: negative is a debit, positive is a credit. One of
 *   the five columns D3' §0 exempts from the platform's non-negative rule, which is why it is a
 *   `Long` and not a [Money].
 */
public data class LedgerPosting(val account: LedgerAccount, val amountMinor: Long) {

    /** Whether this leg took money out of the account. */
    public val isDebit: Boolean get() = amountMinor < 0

    /** The magnitude, ignoring direction. */
    public val magnitude: Money get() = Money.ofMinor(if (amountMinor < 0) -amountMinor else amountMinor)
}

/**
 * A balanced journal entry (`billing.journal_entries` + its postings).
 *
 * @property kind The `ck_journal_entries_kind` value — `topup`, `daily_fee`, `driver_transfer`,
 *   `voucher_purchase`, … A string rather than an enum because the CHECK grows without a contract
 *   change and a client that rejected a new kind would break on a server deploy.
 * @property idempotencyKey The platform-wide money key (§0). Composed from the business fact,
 *   never from a random value, so a retry collides instead of double-posting. The spellings C004
 *   and C005 pinned are reproduced by the callers in this package.
 * @property postings The legs. At least two, and they sum to zero.
 */
public data class LedgerEntry(val kind: String, val idempotencyKey: String, val postings: List<LedgerPosting>) {
    init {
        require(postings.size >= 2) { "a journal entry has at least two postings, got ${postings.size}" }
        require(postings.sumOf { it.amountMinor } == 0L) {
            "postings must sum to zero (D-09); got ${postings.sumOf { it.amountMinor }}"
        }
    }

    /** The legs that took money out. */
    public val debits: List<LedgerPosting> get() = postings.filter { it.isDebit }

    /** The legs that put money in. */
    public val credits: List<LedgerPosting> get() = postings.filterNot { it.isDebit }

    /** The size of the movement — the sum of the credit side. */
    public val amount: Money get() = Money.ofMinor(credits.sumOf { it.amountMinor })

    /** The net effect on one account, zero if it is not party to this entry. */
    public fun netFor(account: LedgerAccount): Money =
        Money.ofMinor(postings.filter { it.account == account }.sumOf { it.amountMinor })
}

package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.CreditTransfer
import lk.mageride.shared.data.models.subscription.CreditTransferDirection
import lk.mageride.shared.data.models.subscription.CreditTransferStatus

// Driver-to-driver credit transfer (US-9.10–9.17/9.20/9.21, AL-01, AL-34, D5' §9.3).
//
// "A driver requests credit from another driver BY DRIVER ID → that driver approves in the Driver
//  App (or proactively sends by Driver ID) → DEBIT SENDER THE EXACT AMOUNT, CREDIT RECIPIENT THE
//  SAME EXACT AMOUNT — NO COMMISSION."
//
// THIS IS A C016 FENCE AND IT IS THE WHOLE POINT OF THIS FILE. "Reseller" is not a role, an
// account or a capability (AL-01): it is any driver who bought bulk credit at the voucher discount
// and passes it on. Their entire margin is that purchase discount ([VoucherCatalogue]), taken once
// at purchase. A per-transfer commission is not a configuration this platform has — it is a bug,
// and [CreditTransferRules.entryFor] is shaped so that writing one would mean adding a third
// posting to an entry whose invariant forbids it.
//
// BY DRIVER ID ONLY (AL-34). The QR-scan path was removed and SCR-DA/DI-023 has no scan tile;
// nothing here takes a scanned payload.

/**
 * A transfer about to be raised.
 *
 * @property senderDriverId The driver whose wallet is debited.
 * @property recipientDriverId The driver whose wallet is credited.
 * @property amount How much moves. **The same figure on both legs** (AL-01).
 */
public data class CreditTransferIntent(val senderDriverId: Ulid, val recipientDriverId: Ulid, val amount: Money) {
    init {
        // `ck_credit_transfers_not_self` (C005) rejects this server-side too; catching it here
        // saves a round trip and lets the driver app say so at the keyboard.
        require(senderDriverId != recipientDriverId) { "a driver cannot transfer credit to themselves" }
        require(amount > Money.ZERO) { "a transfer moves a positive amount, was ${amount.amountMinor}" }
    }
}

/** Why a transfer cannot be sent yet. */
public enum class CreditTransferRejection {

    /** The sender does not have it. Checked against the spendable balance, not the raw one. */
    INSUFFICIENT_BALANCE,

    /** Nothing to send. */
    NON_POSITIVE_AMOUNT,

    /** Sender and recipient are the same driver (`ck_credit_transfers_not_self`). */
    SELF_TRANSFER,
}

/**
 * D5' §9.3, as arithmetic.
 *
 * Everything here is a pure function of a transfer the server already described. The client raises
 * a transfer through `POST /v1/subscriptions/credit-transfer/request` (a pull, needing approval) or
 * `POST /v1/transfers/driver` (a push, posting immediately) and reads the result back; what this
 * object is for is knowing what those calls will do to a balance before they are made.
 */
public object CreditTransferRules {

    /**
     * The commission on a driver-to-driver transfer. **Zero. Always.** (AL-01)
     *
     * A constant rather than a config value on purpose: there is no admin setting behind it and no
     * spec that varies it. The bulk-voucher tier is the only place a discount or margin exists on
     * this platform (`billing.voucher_discount_tiers`), and it applies **at purchase**.
     */
    public const val COMMISSION_BPS: Int = 0

    /** The `billing.journal_entries.kind` a transfer posts under (C005 CHECK). */
    public const val JOURNAL_KIND: String = "driver_transfer"

    /** What the sender is debited: the amount, exactly. */
    public fun debitedFromSender(amount: Money): Money = amount

    /** What the recipient is credited: the amount, exactly — the same figure (AL-01). */
    public fun creditedToRecipient(amount: Money): Money = amount

    /** The fee taken out of a transfer of [amount]. Zero for every amount (AL-01). */
    @Suppress("UNUSED_PARAMETER")
    public fun feeFor(amount: Money): Money = Money.ZERO

    /** Whether [direction] posts on creation, or waits for the holder's approval (US-9.10). */
    public fun postsImmediately(direction: CreditTransferDirection): Boolean =
        direction == CreditTransferDirection.DIRECT

    /**
     * Why [intent] cannot be sent from [standing], or `null` when it can.
     *
     * Checked against [WalletStanding.available] rather than the raw balance: an outstanding
     * penalty is money already owed, and letting a driver transfer it away would move the debt
     * rather than settle it.
     */
    public fun rejectionFor(intent: CreditTransferIntent, standing: WalletStanding): CreditTransferRejection? = when {
        intent.senderDriverId == intent.recipientDriverId -> CreditTransferRejection.SELF_TRANSFER
        intent.amount <= Money.ZERO -> CreditTransferRejection.NON_POSITIVE_AMOUNT
        intent.amount > standing.available -> CreditTransferRejection.INSUFFICIENT_BALANCE
        else -> null
    }

    /** Shorthand for `rejectionFor(...) == null`, for a Send button's enabled flag. */
    public fun canSend(intent: CreditTransferIntent, standing: WalletStanding): Boolean =
        rejectionFor(intent, standing) == null

    /**
     * The balanced entry a transfer posts — **two legs, equal and opposite, and nothing else**.
     *
     * `null` unless the transfer is [CreditTransferStatus.APPROVED]: a PENDING request has moved
     * no money and a REJECTED one never will, which is `ck_credit_transfers_posting` (C005) said
     * in Kotlin.
     *
     * The idempotency key is composed from the transfer id, following the platform rule that a
     * money key is derived from the business fact rather than minted (§0) — a redelivered approval
     * collides here instead of crediting twice.
     */
    public fun entryFor(transfer: CreditTransfer): LedgerEntry? {
        if (transfer.status != CreditTransferStatus.APPROVED) return null
        return LedgerEntry(
            kind = JOURNAL_KIND,
            idempotencyKey = "$JOURNAL_KIND:${transfer.transferId}",
            postings = listOf(
                LedgerPosting(LedgerAccount.driver(transfer.senderDriverId), -transfer.amountMinor),
                LedgerPosting(LedgerAccount.driver(transfer.recipientDriverId), transfer.amountMinor),
            ),
        )
    }
}

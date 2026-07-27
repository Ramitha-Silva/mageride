package lk.mageride.shared.data.models.wallet

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.ProviderCallbackStatus
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.subscription.VoucherDiscountTier

// wallet-svc — driver wallet balance, top-up, ledger view, credit transfer history.
// Source: backend/contracts/wallet.yaml (D3' "wallet-svc — balance, top-up, ledger",
// ADD Appendix C).
//
// Balances are a PROJECTION OF THE DOUBLE-ENTRY LEDGER (D-09): billing.journal_entries plus its
// balanced postings are the truth, and every entry is asserted balanced by a database trigger at
// COMMIT (C005 decision 2).
//
// REMOVED, do not re-add: POST /v1/wallet/topup/bank-transfer and the admin bank-transfer pair
// (AL-05 — bank transfer is NOT a top-up method), and POST /v1/wallet/topup/card (consolidated
// into the OnePay route; card payment IS the OnePay rail).

/**
 * Which side of a credit transfer a row is, from the reading driver's point of view.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class TransferDirection(public val wire: String) {
    @SerialName("sent")
    SENT("sent"),

    @SerialName("received")
    RECEIVED("received"),
}

/**
 * The `?direction=` filter on `GET /v1/wallet/{driverId}/transfers`.
 *
 * @property wire The value as it appears in the query.
 */
@Serializable
public enum class TransferDirectionFilter(public val wire: String) {
    @SerialName("sent")
    SENT("sent"),

    @SerialName("received")
    RECEIVED("received"),

    @SerialName("all")
    ALL("all"),
}

/** Where a credit transfer stands (`billing.credit_transfers.status` CHECK, C005). */
@Serializable
public enum class TransferStatus {
    PENDING,
    APPROVED,
    REJECTED,
}

/** Where a wallet top-up stands. */
@Serializable
public enum class TopupState {
    Pending,
    Succeeded,
    Failed,
}

/**
 * A driver's wallet (`wallet.yaml#/components/schemas/Wallet`, US-9.7). Read-only.
 *
 * [availableMinor] is the balance **net of outstanding accrued debt** — a cross-trip cancellation
 * penalty (D-05) — and it is what the daily-fee gate actually checks, not [balanceMinor].
 *
 * @property userId The wallet's owner.
 * @property balanceMinor Ledger balance, minor units.
 * @property availableMinor Balance net of accrued debt, minor units.
 * @property outstandingDebtMinor Accrued but unsettled penalties, minor units.
 * @property currency Always LKR.
 * @property updatedAt When the projection last moved.
 */
@Serializable
public data class Wallet(
    val userId: Ulid,
    val balanceMinor: Long,
    val availableMinor: Long,
    val outstandingDebtMinor: Long? = null,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val updatedAt: Timestamp? = null,
) : MoneyHolder {
    /** The spendable balance — what the daily-fee gate checks. */
    override val money: Money get() = Money(amountMinor = availableMinor, currency = currency)
}

/**
 * One line of wallet history (`wallet.yaml#/components/schemas/WalletTransaction`, US-9A.19).
 *
 * Deduped per `(account, entry)` (`ux_wallet_tx_account_entry`, C005): the ledger event stream is
 * at-least-once (C002 decision 3), so a redelivered entry must not append a second line.
 *
 * @property transactionId The history line.
 * @property entryId The `billing.journal_entries` row it projects — the dedupe key.
 * @property kind Ledger entry kind, e.g. `topup`, `daily_fee`, `tip_payout`. A free-text machine
 *   key, not an enum: `billing.journal_entries.kind` grows without a contract change.
 * @property amountMinor **Signed** — a debit is negative. This is one of the ledger columns D3' §0
 *   exempts from the non-negative rule, which is why it is not a [Money].
 * @property currency Always LKR.
 * @property balanceAfterMinor Running balance after this line, minor units.
 * @property reference The ride, transfer or purchase this line came from.
 * @property occurredAt When the entry posted.
 */
@Serializable
public data class WalletTransaction(
    val transactionId: Ulid,
    val entryId: Ulid,
    val kind: String,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val balanceAfterMinor: Long,
    val reference: String? = null,
    val occurredAt: Timestamp,
) {
    /** Whether this line took money out of the wallet. */
    public val isDebit: Boolean get() = amountMinor < 0
}

/**
 * One credit transfer as the sending or receiving driver sees it
 * (`wallet.yaml#/components/schemas/TransferRow`, US-9A.11).
 *
 * @property transferId The transfer.
 * @property counterpartyDriverId The other driver.
 * @property counterpartyName Their name.
 * @property amountMinor The amount, minor units. **Exact value, no commission** (AL-01).
 * @property currency Always LKR.
 * @property direction Sent or received, from the reading driver's point of view.
 * @property status Where it stands.
 * @property createdAt When it was raised.
 */
@Serializable
public data class TransferRow(
    val transferId: Ulid,
    val counterpartyDriverId: Ulid,
    val counterpartyName: String? = null,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val direction: TransferDirection,
    val status: TransferStatus,
    val createdAt: Timestamp,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/wallet/credit-transfer/initiate` (US-9A.12).
 *
 * By **Driver ID**, **exact value, no commission** (AL-01). The wallet-side entry point for the
 * same operation subscription-svc exposes as `POST /v1/transfers/driver`; both write
 * `billing.credit_transfers` with `direction='DIRECT'`, and both spellings are in D3' Part 2.
 *
 * @property recipientDriverId Who receives the credit.
 * @property amountMinor How much, minor units.
 */
@Serializable
public data class InitiateWalletCreditTransferRequest(val recipientDriverId: Ulid, val amountMinor: Long)

/**
 * `POST /v1/wallet/topup/onepay` (US-9.18).
 *
 * The wallet is credited **only** on the webhook, with a balanced double-entry journal credit
 * (D-09), which then emits `wallet.credited` and invalidates the dispatch balance cache (D-08).
 *
 * @property amountMinor How much to add, minor units.
 * @property returnUrl Where OnePay should send the user back to.
 */
@Serializable
public data class OnepayTopupRequest(val amountMinor: Long, val returnUrl: String? = null)

/**
 * `POST /v1/wallet/topup/lankaqr` (US-9.18, AL-15).
 *
 * @property amountMinor How much to add, minor units.
 */
@Serializable
public data class LankaqrTopupRequest(val amountMinor: Long)

/**
 * An initiated top-up (`wallet.yaml#/components/schemas/Topup`).
 *
 * @property topupId The top-up.
 * @property state Where it stands.
 * @property amountMinor How much, minor units.
 * @property currency Always LKR.
 * @property redirectUrl OnePay hosted page.
 * @property sessionToken The OnePay session this attempt belongs to.
 * @property paymentLink LankaQR "Pay" deep link into the bank app (AL-15).
 * @property qrPayload LankaQR fallback when the deep link does not resolve.
 */
@Serializable
public data class Topup(
    val topupId: Ulid,
    val state: TopupState,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val redirectUrl: String? = null,
    val sessionToken: String? = null,
    val paymentLink: String? = null,
    val qrPayload: String? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * A gateway confirmation for a wallet top-up
 * (`wallet.yaml#/components/schemas/TopupCallback`).
 *
 * HMAC-signed, **idempotent on [providerTransactionId]** (R-19) — a redelivery credits nothing
 * twice.
 *
 * @property providerTransactionId The dedupe key.
 * @property topupId The top-up this settles.
 * @property status What the provider is reporting.
 * @property amountMinor The amount moved, minor units.
 * @property currency Always LKR.
 * @property raw The provider's own envelope, preserved for reconciliation.
 */
@Serializable
public data class TopupCallback(
    val providerTransactionId: String,
    val topupId: Ulid? = null,
    val status: ProviderCallbackStatus,
    val amountMinor: Long? = null,
    val currency: Currency? = null,
    val raw: JsonObject? = null,
)

/**
 * One voucher discount tier with its usage, as the Admin Portal sees it (US-9A.15).
 *
 * On the wire this is `allOf(VoucherDiscountTier, { purchaseCount, purchasedValueMinor })`,
 * flattened. The usage columns are what make the informal reseller margin visible to Finance.
 *
 * @property denominationMinor Voucher face value, minor units.
 * @property discountBps Basis points; `1000` is 10%.
 * @property active Whether the tier is on sale.
 * @property updatedAt When it was last changed.
 * @property purchaseCount How many vouchers of this denomination have been bought.
 * @property purchasedValueMinor Their total face value, minor units.
 */
@Serializable
public data class VoucherDiscountTierUsage(
    val denominationMinor: Long,
    val discountBps: Int,
    val active: Boolean,
    val updatedAt: Timestamp? = null,
    val purchaseCount: Int? = null,
    val purchasedValueMinor: Long? = null,
) {
    /** The tier without its usage columns — the shape both write surfaces take. */
    public fun toTier(): VoucherDiscountTier = VoucherDiscountTier(
        denominationMinor = denominationMinor,
        discountBps = discountBps,
        active = active,
        updatedAt = updatedAt,
    )
}

/**
 * `GET /v1/wallet/admin/voucher-discount-tiers` — 200.
 *
 * @property tiers The tiers, each with its purchase usage.
 */
@Serializable
public data class VoucherDiscountTierUsageList(val tiers: List<VoucherDiscountTierUsage> = emptyList())

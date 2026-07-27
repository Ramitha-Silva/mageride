package lk.mageride.shared.data.models.subscription

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.PhoneMasked
import lk.mageride.shared.data.models.ProviderCallbackStatus
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType

// subscription-svc — daily platform fee, vouchers, driver credit transfer, Mode B subscriptions.
// Source: backend/contracts/subscription.yaml (D3' "subscription-svc — daily fee, vouchers,
// credit transfer", Δ 2026-06-21 Mode B, Δ 2026-07-18 AL-49, ADD Appendix C).
//
// DAILY FEE (D-08/D-13). Seven tiers by vehicle type, Mode A free. THE FIRST TRIP OF THE DAY IS
// FREE; the fee is deducted before the second, idempotently keyed (driverId, vehicleId, feeDate)
// in ASIA/COLOMBO. `truck` and `mini_truck` have NO SEEDED RATE — Finance must configure one
// before a delivery vehicle can go online (C005).
//
// AL-01: the credit-transfer endpoints are DRIVER-APP APIs. "Reseller" is not a role, account or
// capability — any driver holding credit transfers it BY DRIVER ID, moving the EXACT VALUE WITH
// NO COMMISSION. The reseller's entire margin is the bulk-voucher purchase discount.
// AL-34: the credit-request QR-scan path is REMOVED — the request is Driver-ID-only.
//
// MODE B SUBSCRIPTIONS (Epic 23, AL-24). Payment routes TO THE FLEET OWNER AS A PASS-THROUGH,
// NOT PLATFORM REVENUE — subscription.payments must never post to billing.journal_entries. The
// pay sheet's payTo block is served ONLY from a `verified` payout profile (AL-49).

/**
 * Whether a Colombo day's platform fee has been taken (`billing.daily_fee_charges` view of it).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class DailyFeeDayStatus(public val wire: String) {
    @SerialName("PAID")
    PAID("PAID"),

    @SerialName("UNPAID")
    UNPAID("UNPAID"),
}

/**
 * How a daily-fee charge row was written (`billing.daily_fee_charges.status` CHECK, C005).
 *
 * The first trip of the day writes a [WAIVED_FIRST_TRIP] row carrying **zero** amount
 * (`ck_daily_fee_charges_waiver`) — "no row" keeps meaning "not charged yet today".
 */
@Serializable
public enum class DailyFeeChargeStatus {
    PAID,
    WAIVED_FIRST_TRIP,
}

/**
 * Whether a credit transfer was pushed or pulled
 * (`billing.credit_transfers.direction` CHECK, C005).
 *
 * [REQUESTED] needs the holder's approval; [DIRECT] posts immediately.
 */
@Serializable
public enum class CreditTransferDirection {
    REQUESTED,
    DIRECT,
}

/**
 * Where a credit transfer stands (`billing.credit_transfers.status` CHECK, C005).
 *
 * Only an [APPROVED] transfer may carry a journal entry (`ck_credit_transfers_posting`).
 */
@Serializable
public enum class CreditTransferStatus {
    PENDING,
    APPROVED,
    REJECTED,
}

/**
 * How a bulk voucher is paid for (`subscription.yaml` `POST /v1/vouchers/purchase`).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class VoucherPayMethod(public val wire: String) {
    @SerialName("onepay")
    ONEPAY("onepay"),

    @SerialName("lankaqr")
    LANKAQR("lankaqr"),
}

/**
 * Whether a Mode B vehicle charges its subscribers
 * (`subscription.subscriptions.billing` CHECK, AL-24).
 *
 * The UI label is **"Service payment"** (AL-51); the API name is unchanged for stability.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriptionBilling(public val wire: String) {
    @SerialName("paid")
    PAID("paid"),

    @SerialName("free")
    FREE("free"),
}

/**
 * When a Mode B subscription renews (`subscription.subscriptions.cycle` CHECK, C005).
 *
 * [JOIN_ANNIVERSARY] requires a `joinDay` (`ck_subscriptions_cycle`).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriptionCycle(public val wire: String) {
    @SerialName("month_first")
    MONTH_FIRST("month_first"),

    @SerialName("join_anniversary")
    JOIN_ANNIVERSARY("join_anniversary"),
}

/**
 * A Mode B subscription's lifecycle state (`subscription.subscriptions.status` CHECK, C005).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriptionStatus(public val wire: String) {
    @SerialName("active")
    ACTIVE("active"),

    @SerialName("paused")
    PAUSED("paused"),

    @SerialName("cancelled")
    CANCELLED("cancelled"),
}

/**
 * A roster row's entitlement state (`subscription.grants.status` CHECK, C005).
 *
 * An [UNSUBSCRIBED] row stays on the roster, muted, until the owner deletes it (US-4.12).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriberStatus(public val wire: String) {
    @SerialName("active")
    ACTIVE("active"),

    @SerialName("unsubscribed")
    UNSUBSCRIBED("unsubscribed"),
}

/**
 * A subscriber's standing for the current month, as the owner's roster shows it.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriberMonthStatus(public val wire: String) {
    @SerialName("paid")
    PAID("paid"),

    @SerialName("unpaid")
    UNPAID("unpaid"),

    @SerialName("pending_verification")
    PENDING_VERIFICATION("pending_verification"),
}

/**
 * How a passenger pays a Mode B monthly fare (`subscription.payments.method` CHECK, C005).
 *
 * [CASH] is the only method with no gateway leg at all — the owner marks it received.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriptionPayMethod(public val wire: String) {
    @SerialName("lankaqr_deeplink")
    LANKAQR_DEEPLINK("lankaqr_deeplink"),

    @SerialName("lankaqr_scan")
    LANKAQR_SCAN("lankaqr_scan"),

    @SerialName("onepay")
    ONEPAY("onepay"),

    @SerialName("online_transfer")
    ONLINE_TRANSFER("online_transfer"),

    @SerialName("cash")
    CASH("cash"),
}

/**
 * Where a Mode B subscription payment stands (`subscription.payments.status` CHECK, C005).
 *
 * [PENDING_VERIFICATION] is where an uploaded transfer slip waits for the owner's confirmation;
 * until then the month is unpaid.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SubscriptionPaymentStatus(public val wire: String) {
    @SerialName("initiated")
    INITIATED("initiated"),

    @SerialName("pending_verification")
    PENDING_VERIFICATION("pending_verification"),

    @SerialName("paid")
    PAID("paid"),

    @SerialName("failed")
    FAILED("failed"),
}

// ---------------------------------------------------------------------------------------------
// Daily fee (D-08/D-13)
// ---------------------------------------------------------------------------------------------

/**
 * One tier of the daily platform fee (`subscription.yaml#/components/schemas/DailyFeeRate`).
 *
 * @property vehicleType Which type the rate applies to.
 * @property dailyFeeMinor The fee, minor units. **Zero for Mode A** — bus and train journeys
 *   carry no platform fee.
 * @property mode The operating mode the tier belongs to.
 * @property currency Always LKR.
 */
@Serializable
public data class DailyFeeRate(
    val vehicleType: VehicleType,
    val dailyFeeMinor: Long,
    val mode: ServiceMode,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = dailyFeeMinor, currency = currency)
}

/**
 * `GET /v1/fees/rates` and both sides of `PUT /v1/admin/fees/rates` — one shape.
 *
 * @property items The configured tiers.
 */
@Serializable
public data class DailyFeeRateList(val items: List<DailyFeeRate> = emptyList())

/**
 * `GET /v1/fees/{driverId}/today` — 200 (US-9.1/9.7).
 *
 * @property vehicleType The vehicle the driver is working today.
 * @property dailyRateMinor The tier's rate, minor units.
 * @property status Whether today's fee has been taken.
 * @property deductedMinor What has actually been deducted, minor units.
 * @property tripsToday Completed trips this Colombo day.
 * @property firstTripFree Whether the free first trip is still unspent.
 * @property feeDate The **Asia/Colombo** business date (D-13).
 * @property feeDateTzAt The instant [feeDate] was derived at (D-38) — near midnight the two
 *   disagree, and this is what tells the driver which day the app means.
 */
@Serializable
public data class TodaysDailyFee(
    val vehicleType: VehicleType,
    val dailyRateMinor: Long,
    val status: DailyFeeDayStatus,
    val deductedMinor: Long? = null,
    val tripsToday: Int,
    val firstTripFree: Boolean,
    val feeDate: BusinessDate,
    val feeDateTzAt: Timestamp? = null,
)

/**
 * One charged or waived Colombo day (`subscription.yaml#/components/schemas/DailyFeeCharge`).
 *
 * @property driverId The charged driver.
 * @property vehicleId The vehicle worked.
 * @property feeDate The Asia/Colombo business date.
 * @property feeDateTzAt The instant [feeDate] was derived at (D-38).
 * @property amountMinor What was taken, minor units. **Always zero** when [status] is
 *   [DailyFeeChargeStatus.WAIVED_FIRST_TRIP] (`ck_daily_fee_charges_waiver`).
 * @property currency Always LKR.
 * @property tripsThatDay Trips completed on that day.
 * @property status Charged or waived.
 * @property chargedAt When the deduction posted.
 */
@Serializable
public data class DailyFeeCharge(
    val driverId: Ulid,
    val vehicleId: Ulid,
    val feeDate: BusinessDate,
    val feeDateTzAt: Timestamp? = null,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val tripsThatDay: Int? = null,
    val status: DailyFeeChargeStatus,
    val chargedAt: Timestamp? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/internal/fees/{driverId}/charge-before-trip` (D-08/D-13). Internal, mTLS only.
 *
 * Idempotent on `(driverId, vehicleId, feeDate)` — the primary key of
 * `billing.daily_fee_charges` — so a redelivery is a no-op upsert rather than a second deduction.
 * An insufficient balance is `402 insufficient-wallet`, which surfaces to the driver as a failed
 * accept (US-9.1).
 *
 * @property vehicleId The vehicle about to be worked.
 * @property rideId The ride being accepted.
 */
@Serializable
public data class ChargeDailyFeeRequest(val vehicleId: Ulid, val rideId: Ulid? = null)

// ---------------------------------------------------------------------------------------------
// Driver credit transfer & vouchers (AL-01, AL-34)
// ---------------------------------------------------------------------------------------------

/**
 * A driver-to-driver credit movement
 * (`subscription.yaml#/components/schemas/CreditTransfer`, `billing.credit_transfers`).
 *
 * The sender is debited the **exact amount** and the recipient credited the **exact amount** —
 * **no commission leg** (AL-01). Self-transfer is rejected (`ck_credit_transfers_not_self`).
 *
 * @property transferId The transfer.
 * @property senderDriverId Who pays.
 * @property recipientDriverId Who receives.
 * @property amountMinor The amount, minor units — identical on both legs.
 * @property currency Always LKR.
 * @property direction Requested (needs approval) or direct (posts immediately).
 * @property status Where it stands.
 * @property createdAt When it was raised.
 */
@Serializable
public data class CreditTransfer(
    val transferId: Ulid,
    val senderDriverId: Ulid,
    val recipientDriverId: Ulid,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val direction: CreditTransferDirection,
    val status: CreditTransferStatus,
    val createdAt: Timestamp,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/subscriptions/credit-transfer/request` (US-9.10).
 *
 * **Driver ID only** — the QR-scan path was removed by AL-34 and SCR-DA/DI-023 has no scan tile.
 * Nothing moves until the holder approves.
 *
 * @property holderDriverId The driver being asked for credit.
 * @property amountMinor How much, minor units.
 */
@Serializable
public data class RequestCreditTransferRequest(val holderDriverId: Ulid, val amountMinor: Long)

/**
 * `POST /v1/transfers/driver` (US-9.20/9.21) — the push counterpart of the request/approve pair.
 *
 * Writes `direction='DIRECT'`, `status='APPROVED'` and posts immediately.
 *
 * @property recipientDriverId Who receives the credit.
 * @property amountMinor How much, minor units.
 */
@Serializable
public data class SendCreditToDriverRequest(val recipientDriverId: Ulid, val amountMinor: Long)

/**
 * One bulk-voucher discount tier.
 *
 * `subscription.yaml` and `wallet.yaml` declare this schema identically — both spellings of the
 * admin write are in D3' Part 2 and C007 landed both, so one Kotlin type serves both surfaces
 * (see the C007 handoff: one should be retired).
 *
 * The discount is **per voucher value**, not per driver and not per transfer (AL-01): it is the
 * informal reseller's entire margin.
 *
 * @property denominationMinor Voucher face value, minor units — `100000` is Rs 1,000.
 * @property discountBps Basis points; `1000` is 10% — pay Rs 900, receive Rs 1,000 of credit.
 * @property active Whether the tier is on sale.
 * @property updatedAt When it was last changed.
 */
@Serializable
public data class VoucherDiscountTier(
    val denominationMinor: Long,
    val discountBps: Int,
    val active: Boolean,
    val updatedAt: Timestamp? = null,
)

/**
 * The `{ tiers }` envelope both voucher-tier surfaces use, on request and on response.
 *
 * @property tiers The configured tiers.
 */
@Serializable
public data class VoucherDiscountTierList(val tiers: List<VoucherDiscountTier> = emptyList())

/**
 * `POST /v1/vouchers/purchase` (US-9.19).
 *
 * @property denominationMinor Rs 1,000 / 2,000 / 3,000 / 5,000 / 10,000, in minor units.
 * @property method How the purchase is paid for.
 */
@Serializable
public data class PurchaseVoucherRequest(val denominationMinor: Long, val method: VoucherPayMethod)

/**
 * `POST /v1/vouchers/purchase` — 201 (`subscription.yaml#/components/schemas/VoucherPurchase`).
 *
 * The buyer pays [paidMinor] and **their own** wallet is credited the full [denominationMinor]
 * (`ck_voucher_credit_full`, C005). There is no redeem code and no second party.
 *
 * @property purchaseId The purchase.
 * @property denominationMinor Face value, minor units.
 * @property discountBpsApplied The tier that applied, basis points.
 * @property paidMinor What the buyer actually pays, minor units.
 * @property creditedMinor Always equals [denominationMinor] — the discount lives entirely in
 *   [paidMinor].
 * @property currency Always LKR.
 * @property redirectUrl OnePay hosted page, when that method was chosen.
 * @property qrPayload LankaQR payload, when that method was chosen.
 */
@Serializable
public data class VoucherPurchase(
    val purchaseId: Ulid,
    val denominationMinor: Long,
    val discountBpsApplied: Int,
    val paidMinor: Long,
    val creditedMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val redirectUrl: String? = null,
    val qrPayload: String? = null,
) : MoneyHolder {
    /** What the buyer pays — the discount is the gap between this and [creditedMinor]. */
    override val money: Money get() = Money(amountMinor = paidMinor, currency = currency)
}

// ---------------------------------------------------------------------------------------------
// Mode B access requests & subscriptions (Epic 23)
// ---------------------------------------------------------------------------------------------

/**
 * A passenger's request to see a Mode B vehicle
 * (`subscription.yaml#/components/schemas/AccessRequest`).
 *
 * Requests and grants are **per vehicle, never per fleet**.
 *
 * @property requestId The request.
 * @property vehicleId The vehicle asked about.
 * @property passengerId Who asked.
 * @property passengerName Their name, for the owner's decision screen.
 * @property passengerMobileMasked Role-masked MSISDN.
 * @property status Where the decision stands.
 * @property createdAt When it was raised.
 */
@Serializable
public data class AccessRequest(
    val requestId: Ulid,
    val vehicleId: Ulid,
    val passengerId: Ulid,
    val passengerName: String? = null,
    val passengerMobileMasked: PhoneMasked? = null,
    val status: AccessRequestStatus,
    val createdAt: Timestamp,
)

/**
 * `POST /v1/mode-b/{vehicleId}/access-requests`.
 *
 * @property note Passenger-written message to the owner, at most 500 characters.
 */
@Serializable
public data class RequestModeBAccessRequest(val note: String? = null)

/**
 * `POST /v1/mode-b/access-requests/{requestId}/reject` and the fleet-portal proxy of it.
 *
 * @property reason Owner-written, at most 500 characters.
 */
@Serializable
public data class RejectAccessRequest(val reason: String? = null)

/**
 * `POST /v1/mode-b/access-requests/{requestId}/accept` — 200.
 *
 * Creates the grant **and starts the subscription** in one transaction: a Paid vehicle's
 * subscription inherits the vehicle's default monthly fare, overridable per subscriber afterwards.
 *
 * @property requestId The accepted request.
 * @property grantId The created entitlement.
 * @property subscriptionId The started subscription.
 */
@Serializable
public data class AccessRequestAccepted(val requestId: Ulid, val grantId: Ulid, val subscriptionId: Ulid)

/**
 * A passenger's Mode B subscription (`subscription.yaml#/components/schemas/Subscription`).
 *
 * @property subscriptionId The subscription.
 * @property vehicleId The vehicle subscribed to.
 * @property passengerId The subscriber.
 * @property billing Free or Paid ("Service payment", AL-51).
 * @property monthlyFareMinor Absent on a Free subscription (`ck_subscriptions_fare`).
 * @property currency Always LKR.
 * @property cycle When it renews.
 * @property joinDay Required when [cycle] is [SubscriptionCycle.JOIN_ANNIVERSARY]; 1–31.
 * @property nextDue The next Asia/Colombo due date.
 * @property nextDueTzAt The instant [nextDue] was derived at (D-38).
 * @property status Active, paused or cancelled.
 */
@Serializable
public data class Subscription(
    val subscriptionId: Ulid,
    val vehicleId: Ulid,
    val passengerId: Ulid,
    val billing: SubscriptionBilling,
    val monthlyFareMinor: Long? = null,
    val currency: Currency? = null,
    val cycle: SubscriptionCycle,
    val joinDay: Int? = null,
    val nextDue: BusinessDate? = null,
    val nextDueTzAt: Timestamp? = null,
    val status: SubscriptionStatus,
)

/**
 * One row of a Mode B vehicle's subscriber roster
 * (`subscription.yaml#/components/schemas/SubscriberRow`).
 *
 * `fleet.yaml` declares the same schema for the Fleet Portal's org-scoped proxy of this roster.
 *
 * @property subscriberId The roster row.
 * @property passengerId The subscriber.
 * @property name Their name.
 * @property mobileMasked Role-masked MSISDN.
 * @property billing Free or Paid.
 * @property monthlyFareMinor This subscriber's fare, minor units — may override the vehicle default.
 * @property currency Always LKR.
 * @property cycle When their month rolls over.
 * @property thisMonthStatus Their standing for the current month.
 * @property muted `true` once the passenger unsubscribes; the row survives until the owner
 *   deletes it (US-4.12).
 * @property status Active or unsubscribed.
 */
@Serializable
public data class SubscriberRow(
    val subscriberId: Ulid,
    val passengerId: Ulid,
    val name: String? = null,
    val mobileMasked: PhoneMasked? = null,
    val billing: SubscriptionBilling,
    val monthlyFareMinor: Long? = null,
    val currency: Currency? = null,
    val cycle: SubscriptionCycle? = null,
    val thisMonthStatus: SubscriberMonthStatus? = null,
    val muted: Boolean,
    val status: SubscriberStatus,
)

/**
 * Where the passenger sends the money (`subscription.yaml#/components/schemas/PayTo`, AL-49).
 *
 * Served **only** from a `verified` org payout profile. If the org's profile has reverted to
 * `pending_verification`, collection continues against the **last verified snapshot** — never
 * against unverified edits.
 *
 * @property lankaqrImageUrl Signed URL of the owner's bank-app QR, for the LankaQR methods.
 * @property bank Bank name, for `online_transfer`.
 * @property branch Branch name.
 * @property accountNo Account number.
 * @property accountHolderName Account holder.
 */
@Serializable
public data class PayTo(
    val lankaqrImageUrl: String? = null,
    val bank: String? = null,
    val branch: String? = null,
    val accountNo: String? = null,
    val accountHolderName: String? = null,
)

/**
 * One Mode B monthly payment (`subscription.yaml#/components/schemas/SubscriptionPayment`).
 *
 * The money is a **pass-through to the fleet owner** and is never posted to the platform ledger
 * (C005).
 *
 * @property paymentId The payment.
 * @property subscriptionId What it pays for.
 * @property method How.
 * @property amountMinor How much, minor units.
 * @property currency Always LKR.
 * @property status Where it stands.
 * @property periodMonth Always the first of the month (`ck_payments_period_month_first`).
 * @property periodMonthTzAt The instant [periodMonth] was derived at (D-38).
 * @property payTo The owner's collection details, on an initiated payment.
 * @property redirectUrl OnePay hosted page.
 * @property qrPayload LankaQR payload.
 * @property slipUrl Signed URL of the uploaded transfer screenshot.
 * @property paidAt When it was confirmed.
 */
@Serializable
public data class SubscriptionPayment(
    val paymentId: Ulid,
    val subscriptionId: Ulid,
    val method: SubscriptionPayMethod,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val status: SubscriptionPaymentStatus,
    val periodMonth: BusinessDate,
    val periodMonthTzAt: Timestamp? = null,
    val payTo: PayTo? = null,
    val redirectUrl: String? = null,
    val qrPayload: String? = null,
    val slipUrl: String? = null,
    val paidAt: Timestamp? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/mode-b/subscriptions/{subscriptionId}/pay` (item 16e, AL-49).
 *
 * @property method How the passenger wants to pay.
 * @property periodMonth First of the month being paid for; defaults to the next due period.
 */
@Serializable
public data class PayModeBSubscriptionRequest(val method: SubscriptionPayMethod, val periodMonth: BusinessDate? = null)

/**
 * `PUT /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/fare` (item 16f).
 *
 * Overrides the vehicle's default for this subscriber only. A Free vehicle carries no fare at all
 * (`ck_subscriptions_fare`, C005).
 *
 * @property monthlyFareMinor The override, minor units.
 */
@Serializable
public data class SetSubscriberFareRequest(val monthlyFareMinor: Long)

/**
 * `POST /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/mark-cash` (item 16f).
 *
 * Records a `cash` payment row as paid and advances the next due date.
 *
 * @property amountMinor What the owner received, minor units.
 * @property periodMonth Which month it covers; defaults to the next due period.
 */
@Serializable
public data class MarkSubscriberCashPaidRequest(val amountMinor: Long, val periodMonth: BusinessDate? = null)

/**
 * A gateway confirmation for a Mode B subscription payment
 * (`subscription.yaml#/components/schemas/SubscriptionProviderCallback`).
 *
 * HMAC-signed, idempotent on [providerTransactionId] (R-19). The credit is a **pass-through to
 * the owner** — no platform ledger entry is written (C005).
 *
 * @property providerTransactionId The dedupe key.
 * @property paymentId The subscription payment this settles.
 * @property status What the provider is reporting.
 * @property amountMinor The amount moved, minor units.
 * @property currency Always LKR.
 * @property raw The provider's own envelope, preserved for reconciliation.
 */
@Serializable
public data class SubscriptionProviderCallback(
    val providerTransactionId: String,
    val paymentId: Ulid? = null,
    val status: ProviderCallbackStatus,
    val amountMinor: Long? = null,
    val currency: Currency? = null,
    val raw: JsonObject? = null,
)

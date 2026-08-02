package lk.mageride.shared.data.models.fare

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.MoneyHolder
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.ProviderCallbackStatus
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// fare-svc — estimate, final calculation, payment state machine, refunds.
// Source: backend/contracts/fare.yaml (D3' "fare-svc — estimate, calculate, pay",
// Δ 2026-06-21 item 18 AL-22, Δ 2026-07-05 #2 AL-47, ADD Appendix C).
//
// MONEY IS INTEGER MINOR UNITS EVERYWHERE (`…Minor` + `currency: LKR`). See Money.
//
// PAYMENT HARD RULE (D3' header): there is NO JUSPAY anywhere. The gateways are OnePay,
// LankaQR / Commercial Bank IPG, cash, COD and driver-QR.
//
// DRIVER-QR SETTLEMENT (AL-47) is an ATTESTATION PAIR, not a gateway flow: the passenger scans
// the driver's QR, pays bank-to-bank outside the platform, then claims it
// (PaymentState.QrClaimedByPassenger); the driver confirms receipt
// (PaymentState.DriverConfirmedQR, terminal) or either party disputes. NO WALLET MOVEMENT, ZERO
// COMMISSION. A gateway-verified PaymentState.Succeeded stays OnePay-only (D-10).

/**
 * The payment method chosen **at settlement time** (`fares.ride_payments.method` CHECK, C005).
 *
 * Deliberately wider than the booking-time method: [SCAN_DRIVER_QR] is a settlement-time choice
 * and `cod` is a booking-time one, so the two columns have different domains (C004 note (f)).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class PaymentMethod(public val wire: String) {
    @SerialName("cash")
    CASH("cash"),

    @SerialName("lankaqr")
    LANKAQR("lankaqr"),

    @SerialName("onepay")
    ONEPAY("onepay"),

    @SerialName("scan_driver_qr")
    SCAN_DRIVER_QR("scan_driver_qr"),

    /**
     * The passenger wallet (Δ AL-57) — where card acceptance survives.
     *
     * OnePay collects on the **top-up**, where MageRide is the payee; the fare itself is one
     * balanced `trip_payment` entry, passenger wallet → driver wallet, terminal on the spot.
     */
    @SerialName("wallet")
    WALLET("wallet"),

    /** Cash on delivery — package only (P-08). Settlement-time only; booking-time carries `cod`. */
    @SerialName("cod")
    COD("cod"),
}

/**
 * What is being priced on `GET /v1/fare/estimate`.
 *
 * @property wire The value as it appears in the `?kind=` query.
 */
@Serializable
public enum class FareEstimateKind(public val wire: String) {
    @SerialName("passenger")
    PASSENGER("passenger"),

    @SerialName("package")
    PACKAGE("package"),
}

/**
 * Whether a refund returns all of a payment, part of it, or reverses an overpayment
 * (`fares.refunds.kind` CHECK, C005).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class RefundKind(public val wire: String) {
    @SerialName("full")
    FULL("full"),

    @SerialName("partial")
    PARTIAL("partial"),

    @SerialName("overpaid_reversal")
    OVERPAID_REVERSAL("overpaid_reversal"),
}

/** Where a refund has got to (`fares.refunds.status` CHECK, C005). */
@Serializable
public enum class RefundStatus {
    Requested,
    Submitted,
    Succeeded,
    Failed,
}

/**
 * How a fare was arrived at (`fare.yaml#/components/schemas/FareBreakdown`).
 *
 * **Only the total is shown in the UI** (US-8.4); this is for support and receipts. The surcharge
 * windows are evaluated in **Asia/Colombo** and may wrap midnight (D5' §2, D-38).
 *
 * @property firstKmMinor The first-kilometre charge, minor units.
 * @property perKmMinor The per-kilometre rate, minor units.
 * @property distanceKm Distance priced. On a final fare this is the Kalman-filtered track (E-04),
 *   not the raw GPS polyline, so jitter does not inflate the fare.
 * @property peakSurchargePct Peak-window uplift, percent.
 * @property nightSurchargePct Night-window uplift, percent.
 */
@Serializable
public data class FareBreakdown(
    val firstKmMinor: Long,
    val perKmMinor: Long,
    val distanceKm: Double,
    val peakSurchargePct: Int? = null,
    val nightSurchargePct: Int? = null,
)

/**
 * `GET /v1/fare/estimate` — 200 (US-8.9).
 *
 * @property fareEstimateToken Opaque and **binds the quoted price**: `POST /v1/rides/request`
 *   requires it and rejects a stale or forged one with `400 invalid-fare-token`.
 * @property amountMinor Total, minor units.
 * @property currency Always LKR.
 * @property breakdown How the total was built.
 */
@Serializable
public data class FareEstimateResponse(
    val fareEstimateToken: String,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val breakdown: FareBreakdown,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/fare/calculate`. Internal, mTLS only — ride-svc calls it on complete.
 *
 * @property rideId The completed ride.
 * @property distanceKm Kalman-filtered distance; recomputed server-side when omitted.
 * @property durationSec Trip duration in seconds.
 */
@Serializable
public data class CalculateFinalFareRequest(
    val rideId: Ulid,
    val distanceKm: Double? = null,
    val durationSec: Int? = null,
)

/**
 * `POST /v1/fare/calculate` — 200. Writes `fares.ride_payments` in [PaymentState.Initiated].
 *
 * @property paymentId The created payment row.
 * @property amountMinor What the passenger owes, minor units.
 * @property currency Always LKR.
 * @property breakdown How the total was built.
 */
@Serializable
public data class FinalFareResponse(
    val paymentId: Ulid,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val breakdown: FareBreakdown,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * `POST /v1/fare/pay` (D-10).
 *
 * A proxy booking routes the charge to the **payer** — the booker, not the rider (P-04). A driver
 * with no OnePay merchant binding is `402 merchant-not-onboarded` (D-11).
 *
 * @property rideId The ride being paid for.
 * @property method How. OnePay adds a 5% surcharge (US-8.11), stated back as
 *   [PaymentInitiation.surchargeMinor] so the passenger sees it before committing.
 * @property tipMinor Optional gratuity (E-10); posts as a `tip_payout` journal kind.
 */
@Serializable
public data class InitiatePaymentRequest(val rideId: Ulid, val method: PaymentMethod, val tipMinor: Long? = null)

/**
 * The wallet block of a [PaymentInitiation]. Present only for `method: wallet` (Δ AL-57).
 *
 * The passenger wallet is where card acceptance survives: OnePay collects on the **top-up**, where
 * MageRide is the payee, and the fare itself is one balanced `trip_payment` entry from the
 * passenger's wallet to the driver's.
 *
 * @property balanceAfterMinor The passenger's balance once the fare has been debited.
 */
@Serializable
public data class WalletInitiation(val balanceAfterMinor: Long? = null)

/**
 * The driver-QR block of a [PaymentInitiation]. Present only for `method: scan_driver_qr`
 * (Δ AL-59).
 *
 * The driver's **own** bank-app LankaQR from their verified payout profile. There is no callback
 * and no gateway: the money never passes through MageRide, so settlement is AL-47's attestation
 * pair — the passenger claims, the driver confirms.
 *
 * @property qrImageUrl Short-lived signed URL to the driver's QR image.
 */
@Serializable
public data class DriverQrInitiation(val qrImageUrl: String? = null)

/**
 * `POST /v1/fare/pay` — 200 (`fare.yaml#/components/schemas/PaymentInitiation`).
 *
 * The response carries **exactly one** method-specific block; `cash` carries neither and settles
 * on the driver's confirmation.
 *
 * @property paymentId The payment being driven.
 * @property state Where the machine is.
 * @property method How it is being paid.
 * @property amountMinor The fare, minor units.
 * @property surchargeMinor OnePay adds 5% (US-8.11); zero for every other method.
 * @property currency Always LKR.
 * @property wallet Present only for [PaymentMethod.WALLET] (Δ AL-57).
 * @property driverQr Present only for [PaymentMethod.SCAN_DRIVER_QR] (Δ AL-59) — the driver's own
 *   bank-app LankaQR, as a short-lived signed URL. There is no callback: the money never passes
 *   through MageRide, so settlement is AL-47 attestation.
 */
@Serializable
public data class PaymentInitiation(
    val paymentId: Ulid,
    val state: PaymentState,
    val method: PaymentMethod,
    val amountMinor: Long,
    val surchargeMinor: Long? = null,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val wallet: WalletInitiation? = null,
    val driverQr: DriverQrInitiation? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

/**
 * The 200 of every payment read and of the driver-QR pair
 * (`fare.yaml#/components/schemas/PaymentStatus`).
 *
 * @property paymentId The payment.
 * @property rideId The ride it settles.
 * @property state Where the machine is. A terminal state releases the driver's earning (R-05).
 * @property method How it is being paid.
 * @property amountMinor The fare, minor units.
 * @property surchargeMinor Gateway surcharge, minor units.
 * @property tipMinor Gratuity, minor units (E-10).
 * @property currency Always LKR.
 * @property settledAt When the payment reached its terminal state.
 */
@Serializable
public data class PaymentStatus(
    val paymentId: Ulid,
    val rideId: Ulid,
    val state: PaymentState,
    val method: PaymentMethod,
    val amountMinor: Long,
    val surchargeMinor: Long? = null,
    val tipMinor: Long? = null,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
    val settledAt: Timestamp? = null,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

// ---------------------------------------------------------------------------------------------
// Driver-QR attestation settlement (AL-47)
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/fare/pay/scan-driver-qr` (AL-22, AL-47).
 *
 * Records `method='scan_driver_qr'`. Since AL-47 this **no longer waits for a webhook** — it
 * leads into the claim/confirm pair below, because the money moves bank-to-bank outside the
 * platform and no gateway can confirm it.
 *
 * @property rideId The ride being settled.
 * @property qrPayload Decoded contents of the driver's printed, on-screen or sticker QR.
 */
@Serializable
public data class ScanDriverQrRequest(val rideId: Ulid, val qrPayload: String)

/**
 * `POST /v1/fare/pay/driver-qr/claim` (AL-47, US-26.1).
 *
 * Moves the payment to [PaymentState.QrClaimedByPassenger] and prompts the driver, re-pushing at
 * +5 minutes if unconfirmed.
 *
 * @property rideId The ride being settled.
 * @property receiptArtifactId The bank app's receipt screenshot, stored as
 *   `rides.proof_artifacts(kind='qr_receipt')`. **This is what a dispute is adjudicated on.**
 */
@Serializable
public data class ClaimDriverQrRequest(val rideId: Ulid, val receiptArtifactId: Ulid? = null)

/**
 * `POST /v1/fare/pay/driver-qr/confirm` (AL-47).
 *
 * Moves the payment to [PaymentState.DriverConfirmedQR], which is **terminal** — the earning
 * posts (R-05). Valid with or without a prior passenger claim, because the driver's bank app is
 * the only party that actually saw the money.
 *
 * @property rideId The ride being settled.
 */
@Serializable
public data class ConfirmDriverQrRequest(val rideId: Ulid)

/**
 * `POST /v1/fare/pay/driver-qr/dispute` (AL-47).
 *
 * Opens a ticket routed Support → Finance. **No wallet movement** — the platform takes no
 * commission on this path and holds none of the money, so there is nothing to reverse.
 *
 * @property rideId The disputed ride.
 * @property note Free text from whichever party raised it, at most 2000 characters.
 */
@Serializable
public data class DisputeDriverQrRequest(val rideId: Ulid, val note: String? = null)

// ---------------------------------------------------------------------------------------------
// Provider callbacks and refunds
// ---------------------------------------------------------------------------------------------

/**
 * A gateway confirmation callback (`fare.yaml#/components/schemas/ProviderCallback`).
 *
 * HMAC-signed and verified before the body is parsed (D6' §7.1/§7.2). **Idempotent on
 * [providerTransactionId]** (R-19) — a redelivery is a no-op, which is why the operation carries
 * no `Idempotency-Key`: an external gateway cannot send our header.
 *
 * @property providerTransactionId The dedupe key.
 * @property paymentId The payment this settles, when the provider echoes it.
 * @property status What the provider is reporting.
 * @property amountMinor The amount the provider moved, minor units.
 * @property currency Always LKR.
 * @property raw The provider's own envelope, preserved verbatim for reconciliation.
 */
@Serializable
public data class ProviderCallback(
    val providerTransactionId: String,
    val paymentId: Ulid? = null,
    val status: ProviderCallbackStatus,
    val amountMinor: Long? = null,
    val currency: Currency? = null,
    val raw: JsonObject? = null,
)

/**
 * `POST /v1/admin/fare/refund` (E-05). **Finance only.**
 *
 * Writes `fares.refunds` and a balanced `billing.journal_entries` pair — the gateway round-trip
 * and the ledger effect are separate rows by design (C005). Audited (D-35).
 *
 * @property paymentId The payment to refund.
 * @property kind Full, partial, or an overpayment reversal.
 * @property amountMinor How much, minor units.
 * @property currency Always LKR.
 * @property reasonCode A stable machine key, at most 60 characters — not display copy.
 */
@Serializable
public data class RefundFareRequest(
    val paymentId: Ulid,
    val kind: RefundKind,
    val amountMinor: Long,
    val currency: Currency? = null,
    val reasonCode: String,
)

/**
 * `POST /v1/admin/fare/refund` — 201.
 *
 * @property refundId The created refund.
 * @property status Where the refund is.
 * @property amountMinor How much, minor units.
 * @property currency Always LKR.
 */
@Serializable
public data class RefundResponse(
    val refundId: Ulid,
    val status: RefundStatus,
    val amountMinor: Long,
    @OptIn(ExperimentalSerializationApi::class)
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val currency: Currency = Currency.LKR,
) : MoneyHolder {
    override val money: Money get() = Money(amountMinor = amountMinor, currency = currency)
}

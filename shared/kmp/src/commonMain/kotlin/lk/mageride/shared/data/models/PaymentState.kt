package lk.mageride.shared.data.models

import kotlinx.serialization.Serializable

/**
 * The 14 states of the ride payment machine (D-10, §11.8, plus the AL-47 driver-QR pair;
 * `_shared.yaml#/components/schemas/PaymentState`).
 *
 * **This list is the `ck_ride_payments_state` CHECK constraint, verbatim**
 * (`1002__fares_ride_payments.sql`, C005) — same values, same spelling, same count (C012 fence,
 * DoD).
 *
 * The base machine runs `Initiated → Pending → Succeeded | Failed | Retried | FellBackToCash`,
 * plus the cash-on-delivery and driver-QR terminals. **The driver's earning posts only on a
 * terminal state** (R-05), and `POST /v1/internal/rides/{rideId}/payment-settled` on ride-svc is
 * how that terminal reaches the ride aggregate.
 *
 * **Driver-QR settlement (AL-47) is an attestation pair, not a gateway flow.** The passenger
 * scans the driver's QR and pays bank-to-bank outside the platform, claims it
 * ([QrClaimedByPassenger]), and the driver confirms receipt ([DriverConfirmedQR], terminal). No
 * wallet movement, zero commission — a gateway-verified [Succeeded] stays OnePay-only.
 *
 * > `PartiallyRefunded` survives here because the C005 CHECK landed the **union** of the base §9
 * > DDL and the AL-47 rewrite: the rewrite silently dropped it while adding the QR pair, but
 * > §19, ADD §9.1 and `fares.refunds.kind='partial'` (E-05) all still require it. A micro-change-set
 * > against §25 is recorded in the C005 handoff.
 */
@Serializable
public enum class PaymentState {
    /** Row created by `POST /v1/fare/calculate`; nothing has been attempted yet. */
    Initiated,

    /** A gateway round-trip is outstanding. The client polls, and also gets a push. */
    Pending,

    /** Terminal: gateway-verified success. OnePay / LankaQR only (D-10). */
    Succeeded,

    /** The gateway declined or errored. The passenger may retry or fall back to cash. */
    Failed,

    /** A replacement attempt was created; `retry_of_payment_id` chains them (D-10). */
    Retried,

    /** Terminal: a failing digital payment was switched to cash in the vehicle (US-8.15). */
    FellBackToCash,

    /** Package booked `paymentMethod: cod`; the driver collects on delivery (P-08). */
    CashOnDelivery,

    /** Terminal: the driver confirmed the cash-on-delivery amount was collected (P-08). */
    CashOnDeliveryCollected,

    /** A provider callback arrived after the ride had already settled in cash (§11.14). */
    Overpaid,

    /** Terminal: fully refunded by Finance (E-05). */
    Refunded,

    /** Terminal: partially refunded by Finance — see the note on this enum. */
    PartiallyRefunded,

    /** Terminal-with-followup: either party opened a dispute; refunds are Finance-only. */
    Disputed,

    /** AL-47: the passenger says they paid by scanning the driver's QR; awaiting confirmation. */
    QrClaimedByPassenger,

    /** Terminal: the driver confirmed the QR payment arrived (AL-47). The earning posts. */
    DriverConfirmedQR,
    ;

    /** Whether the payment can still move. A terminal state is what releases the earning (R-05). */
    public val isTerminal: Boolean get() = this in TERMINAL

    /**
     * Whether this state settles the money outside the platform (cash, COD, driver QR).
     *
     * The platform takes no commission and holds none of the money on these paths, which is why
     * an AL-47 dispute has nothing to reverse.
     */
    public val isOffPlatformSettlement: Boolean get() = this in OFF_PLATFORM

    public companion object {
        private val TERMINAL: Set<PaymentState> = setOf(
            Succeeded,
            FellBackToCash,
            CashOnDeliveryCollected,
            Refunded,
            PartiallyRefunded,
            Disputed,
            DriverConfirmedQR,
        )

        private val OFF_PLATFORM: Set<PaymentState> = setOf(
            FellBackToCash,
            CashOnDelivery,
            CashOnDeliveryCollected,
            QrClaimedByPassenger,
            DriverConfirmedQR,
        )
    }
}

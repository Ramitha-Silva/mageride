package lk.mageride.passenger.ride

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.iam.DefaultPaymentMethod
import lk.mageride.shared.data.models.ride.RidePaymentMethod

/**
 * The payment rails that survive AL-57 and AL-59, and the reason two of them died.
 *
 * **`onepay` and platform-`lankaqr` are no longer ride methods.** AL-57: OnePay has one merchant
 * account per merchant, so a card fare could only ever land in MageRide's own account — card
 * acceptance survives one step earlier, as a **wallet top-up** where MageRide legitimately is the
 * payee, and the fare is paid with `wallet`. AL-59: the `lankaqr` rail pointed at
 * `LankaQr__MerchantId`, the *platform's* merchant, and collapses into `scan_driver_qr` — the
 * **driver's own** bank QR, settled by AL-47 attestation because money into somebody else's bank
 * produces no platform webhook.
 *
 * **There is therefore no surcharge on any surviving rail.** The +5 % existed to recover OnePay's
 * ~3 % on the ride; nothing in this file can carry one, which is the Definition-of-Done line
 * *"no surcharge is ever displayed on a ride"* made structural.
 *
 * **`ride.yaml`'s booking-time enum has not caught up** — it still declares
 * `[cash, lankaqr, onepay, cod]` while `fare.yaml`'s settlement-time `PaymentMethod` is
 * `[cash, wallet, scan_driver_qr, cod]`. See [bookingValueOf] for how a booking is expressed until
 * it does, and the C080 handoff for the micro-change-set it needs.
 */
internal object PaymentRails {

    /**
     * What SCR-PA-016 offers for a **passenger ride**, in the wireframe's order.
     *
     * Cash first because it is the default and the one that always works.
     */
    val RIDE: List<PaymentMethod> = listOf(
        PaymentMethod.CASH,
        PaymentMethod.WALLET,
        PaymentMethod.SCAN_DRIVER_QR,
    )

    /** A parcel adds COD (US-20.8) — the recipient pays the driver on delivery. */
    val PACKAGE: List<PaymentMethod> = RIDE + PaymentMethod.COD

    /** The rails this app will never offer again. Named so a reader knows the omission is a decision. */
    val RETIRED: Set<PaymentMethod> = setOf(PaymentMethod.ONEPAY, PaymentMethod.LANKAQR)

    /**
     * What SCR-PA-027's **Default payment** row offers (US-22.4, AL-14) — Δ C083.
     *
     * **Two of [RIDE]'s three, and the missing one is the contract's own exclusion.**
     * `iam.yaml#/components/schemas/DefaultPaymentMethod` says in as many words that *"the
     * driver-QR method is a **settlement** choice made during a ride, not a stored preference"* —
     * it needs a driver, a QR image and an amount, none of which exist when a passenger is sitting
     * in Settings. So a *preference* is cash or the wallet, and the third rail is chosen on
     * SCR-PA-016 with the fare on screen.
     *
     * The wireframe draws **Cash / LankaQR / OnePay** here, which predates the 2026-08-01
     * payment-custody change set exactly as SCR-PA-016's does; AL-57 and AL-59 retired both of
     * those rails and [RETIRED] is where they went. Recorded in the C083 handoff.
     */
    val PREFERABLE: List<PaymentMethod> = listOf(PaymentMethod.CASH, PaymentMethod.WALLET)

    /** A stored wire value back to its rail, or `null` when this build has never heard of it. */
    fun fromWire(wire: String): PaymentMethod? = PaymentMethod.entries.firstOrNull { it.wire == wire }

    /**
     * The `iam.users.default_payment_method` value for a chosen rail, or `null` when the column
     * cannot express it.
     *
     * **`wallet` is the `null`, and it is the AL-57 gap.** The contract's enum is still
     * `[cash, lankaqr, onepay]` and the `CHECK` behind it likewise, so the rail that *replaced*
     * `onepay` has no value to be stored as. A wallet default is therefore held on the device and
     * does not follow the passenger to a second handset until `iam.yaml` and C003's constraint
     * gain `wallet`. Recorded in the C083 handoff as the micro-change-set this needs.
     */
    fun storedValueOf(method: PaymentMethod): DefaultPaymentMethod? =
        if (method == PaymentMethod.CASH) DefaultPaymentMethod.CASH else null

    /**
     * What a profile's stored default means **today**.
     *
     * A row still carrying `lankaqr` or `onepay` was written before AL-57/AL-59 and names a rail
     * this app can no longer offer, so it reads as Cash — the platform default and the one that
     * always works. Pre-selecting a retired rail would put a booking on a method SCR-PA-016 does
     * not even draw.
     */
    fun fromStored(stored: DefaultPaymentMethod?): PaymentMethod = when (stored) {
        DefaultPaymentMethod.CASH, null -> PaymentMethod.CASH

        // Both retired. Every arm answers Cash today, and the exhaustive `when` is the point: when
        // `wallet` is added to the enum this function is where it lands, and the compiler says so.
        DefaultPaymentMethod.LANKAQR, DefaultPaymentMethod.ONEPAY -> PaymentMethod.CASH
    }

    @StringRes
    fun label(method: PaymentMethod): Int = when (method) {
        PaymentMethod.CASH -> R.string.payment_cash

        PaymentMethod.WALLET -> R.string.payment_wallet

        PaymentMethod.SCAN_DRIVER_QR -> R.string.payment_driver_qr

        PaymentMethod.COD -> R.string.payment_cod

        // Unreachable through RIDE/PACKAGE, but `PaymentMethod` still declares them because
        // `:shared` types the whole `fares.ride_payments.method` domain, including historical rows.
        PaymentMethod.ONEPAY, PaymentMethod.LANKAQR -> R.string.payment_retired
    }

    /** The one-line explanation under each rail. */
    @StringRes
    fun caption(method: PaymentMethod): Int = when (method) {
        PaymentMethod.CASH -> R.string.payment_cash_caption
        PaymentMethod.WALLET -> R.string.payment_wallet_caption
        PaymentMethod.SCAN_DRIVER_QR -> R.string.payment_driver_qr_caption
        PaymentMethod.COD -> R.string.payment_cod_caption
        PaymentMethod.ONEPAY, PaymentMethod.LANKAQR -> R.string.payment_retired
    }

    /**
     * The booking-time value for a chosen settlement rail.
     *
     * **A gap, expressed as honestly as it can be.** `RideRequest.paymentMethod` is typed against
     * `ride.yaml`'s enum, which has no `wallet` and no `scan_driver_qr` — the AL-57/AL-59 change
     * set updated `fares.ride_payments.method` and left the booking column behind. A booking
     * therefore records `cod` for a parcel and `cash` for everything else, and the real rail is
     * chosen on SCR-PA-016 *after* the ride, which is where D-10 starts anyway.
     *
     * This is not a silent downgrade: nothing about the ride depends on the booking-time value,
     * `fare-svc` takes the settlement method from `POST /v1/fare/pay`, and the passenger picks it
     * again on a screen that shows them the amount. What it costs is the *intent* — dispatch
     * cannot know a passenger meant to pay by wallet. Recorded in the C080 handoff.
     */
    fun bookingValueOf(method: PaymentMethod): RidePaymentMethod =
        if (method == PaymentMethod.COD) RidePaymentMethod.COD else RidePaymentMethod.CASH
}

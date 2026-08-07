package lk.mageride.passenger.ride

import lk.mageride.shared.data.models.fare.PaymentMethod

/**
 * The rail SCR-PA-016 confirmed, on its way to SCR-PA-017 (Δ C098).
 *
 * ### The defect this closes
 *
 * **The chosen rail never reached SCR-PA-017.** `PaymentMethodViewModel` recorded `confirmed`, the
 * screen called `onConfirmed(method)`, and `PassengerNavHost`'s arm **discarded the argument** and
 * navigated with the ride id alone — while `PayFareViewModel.method` defaulted to
 * [PaymentMethod.SCAN_DRIVER_QR] and `setMethod` had no production caller. A passenger who chose
 * **Cash** or **Wallet** therefore landed on a screen that had already posted
 * `POST /v1/fare/pay {method: scan_driver_qr}` and was asking them to scan a QR.
 *
 * Found while building the iOS twin (C098) and fixed on both platforms in the same session;
 * `apps/passenger-ios/PassengerApp/Ride/PaymentSelection.swift` is the same type for the same reason.
 *
 * ### Why a holder rather than a navigation argument
 *
 * `PassengerRoute.PayFare` is `ride/{rideId}/pay` and the pattern is **diffed against the iOS route
 * table** — `NavigationShellTests` on that side types this file's paths out and compares — so a
 * query parameter added here alone would fail a build on the other side of a comparison both apps
 * deliberately keep. Same shape as [PackageOtps], and the same shape `BookingDraft` takes for a
 * booking assembled across six screens.
 *
 * Process-lifetime, like both of those. A settled ride's rail is not something a later screen should
 * be able to read, which is what [forget] is for.
 */
internal class PaymentSelection {

    private val chosen = mutableMapOf<String, PaymentMethod>()

    /** SCR-PA-016's Confirm. */
    fun choose(rideId: String, method: PaymentMethod) {
        chosen[rideId] = method
    }

    /**
     * What SCR-PA-017 should settle on.
     *
     * **Falls back to the driver's QR, which is what SCR-PA-017's cell draws.** A passenger reaches
     * that screen without passing through SCR-PA-016 in exactly one way — a cold start straight onto
     * a `PaymentPending` ride — and the QR path is the one that asks before it acts: it renders a
     * scan panel and settles nothing until the passenger scans or claims. Cash would mark a fare
     * settled that nobody had handed over, and the wallet would move money on a screen the passenger
     * had not chosen a rail on.
     */
    fun railFor(rideId: String): PaymentMethod = chosen[rideId] ?: PaymentMethod.SCAN_DRIVER_QR

    /** Forgets a ride's answer, once it is settled. */
    fun forget(rideId: String) {
        chosen.remove(rideId)
    }
}

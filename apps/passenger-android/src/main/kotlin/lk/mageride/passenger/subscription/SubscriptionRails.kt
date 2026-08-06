package lk.mageride.passenger.subscription

import androidx.annotation.StringRes
import lk.mageride.passenger.R
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod

/**
 * How a Mode B month can be paid, after AL-59 — and why OnePay is not on the list.
 *
 * **The money is the fleet owner's, not MageRide's** (AL-24, §18b). `payTo` is the owner's own
 * bank account or their bank-app LankaQR image (AL-49), and OnePay has **one merchant account per
 * merchant** — routing a subscription through it would land a passenger's payment in MageRide's
 * account and make a pass-through into platform revenue with a manual payout behind it. So AL-59
 * removed the rail, deleted `POST /v1/mode-b/pay/onepay/webhook` from the contract, and left
 * `subscription.payments.method` declaring `onepay` only for rows written before it.
 *
 * **Nothing here carries a surcharge.** The wireframe's `OnePay · +5 %` went with the rail it
 * belonged to; the four below all move the face value of the fare and no more. Same outcome
 * C080's `PaymentRails` reached on the ride side, for the same reason.
 *
 * The two halves of the list behave completely differently and [SubscriptionPayMethod] does not
 * say so — `:shared`'s `ModeBPaymentRules` does, and this cluster asks it rather than re-deciding:
 * the LankaQR pair hand off to a bank app, while `online_transfer` and `cash` settle on the
 * **owner's** human confirmation and sit unpaid until it comes.
 */
internal object SubscriptionRails {

    /**
     * What SCR-PA-025a offers, in the wireframe's order.
     *
     * Cash is last because it is the only one that cannot be completed in the app: the passenger
     * hands money to a collector and waits for the owner to mark it received (US-23.6).
     */
    val METHODS: List<SubscriptionPayMethod> = listOf(
        SubscriptionPayMethod.LANKAQR_DEEPLINK,
        SubscriptionPayMethod.LANKAQR_SCAN,
        SubscriptionPayMethod.ONLINE_TRANSFER,
        SubscriptionPayMethod.CASH,
    )

    /** The rail this app will never offer again. Named so a reader knows the omission is a decision. */
    val RETIRED: Set<SubscriptionPayMethod> = setOf(SubscriptionPayMethod.ONEPAY)

    /** The row's title. */
    @StringRes
    fun label(method: SubscriptionPayMethod): Int = when (method) {
        SubscriptionPayMethod.LANKAQR_DEEPLINK -> R.string.subscription_pay_lankaqr_deeplink

        SubscriptionPayMethod.LANKAQR_SCAN -> R.string.subscription_pay_lankaqr_scan

        SubscriptionPayMethod.ONLINE_TRANSFER -> R.string.subscription_pay_transfer

        SubscriptionPayMethod.CASH -> R.string.subscription_pay_cash

        // Unreachable through METHODS. `SubscriptionPayMethod` still declares it because `:shared`
        // types the whole `subscription.payments.method` domain, and SCR-PA-025b renders history
        // rows that predate AL-59.
        SubscriptionPayMethod.ONEPAY -> R.string.subscription_pay_retired
    }

    /** The one-line explanation under it. */
    @StringRes
    fun caption(method: SubscriptionPayMethod): Int = when (method) {
        SubscriptionPayMethod.LANKAQR_DEEPLINK -> R.string.subscription_pay_lankaqr_deeplink_caption
        SubscriptionPayMethod.LANKAQR_SCAN -> R.string.subscription_pay_lankaqr_scan_caption
        SubscriptionPayMethod.ONLINE_TRANSFER -> R.string.subscription_pay_transfer_caption
        SubscriptionPayMethod.CASH -> R.string.subscription_pay_cash_caption
        SubscriptionPayMethod.ONEPAY -> R.string.subscription_pay_retired
    }
}

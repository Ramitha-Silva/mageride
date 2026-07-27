package lk.mageride.shared.domain.subscription

import lk.mageride.shared.data.models.subscription.PayTo
import lk.mageride.shared.data.models.subscription.SubscriberMonthStatus
import lk.mageride.shared.data.models.subscription.SubscriptionPayMethod
import lk.mageride.shared.data.models.subscription.SubscriptionPayment
import lk.mageride.shared.data.models.subscription.SubscriptionPaymentStatus
import lk.mageride.shared.domain.fare.FarePaymentAction
import lk.mageride.shared.domain.fare.PaymentMethods

// How a Mode B subscriber pays (BR-23.10, AL-24, AL-49, `subscription.payments`).
//
// "Passenger pays via LankaQR deep-link / LankaQR scan / OnePay / online transfer; payments route
//  TO THE FLEET OWNER (pass-through, not platform revenue). Online transfer requires a slip
//  screenshot → status `pending_verification` until the OWNER CONFIRMS → `paid`. Cash is handed to
//  a collector and only the OWNER MARKS IT RECEIVED in the portal → `paid`."
//
// TWO OF THE FIVE METHODS HAVE NO GATEWAY AT ALL. `online_transfer` and `cash` settle on a human
// decision by the fleet owner, which is why `pending_verification` is a first-class status and why
// a subscriber's month can be "paid by them, not yet confirmed by the owner". Modelling that as
// simply unpaid would tell a subscriber who has already transferred the money that they still owe
// it.
//
// EVERY METHOD NEEDS A VERIFIED PAYOUT PROFILE (AL-49, BR-31.1). `payTo` — the transfer details
// and the owner's bank-app LankaQR image — is served ONLY from a `verified` org payout profile,
// and always from the LATEST VERIFIED row, never from an unverified edit. Without one there is
// nowhere to send the money, whichever method the subscriber picked.
//
// NOTHING HERE PRODUCES A LEDGER ENTRY, and that is the fence: the platform holds none of this
// money (§18b).

/** What the subscriber's pay sheet does next. */
public sealed interface ModeBPaymentStep {

    /**
     * Open the owner's bank-app LankaQR image so the subscriber can scan it
     * ([SubscriptionPayMethod.LANKAQR_SCAN]).
     *
     * @property imageUrl Signed URL of the image the owner uploaded with their payout profile.
     */
    public data class ShowOwnerLankaQr(val imageUrl: String) : ModeBPaymentStep

    /**
     * Hand off to a gateway — the LankaQR deep link or OnePay's hosted page.
     *
     * @property action Which hand-off, resolved by the same AL-15 rule the fare side uses.
     */
    public data class GatewayHandoff(val action: FarePaymentAction) : ModeBPaymentStep

    /**
     * Transfer to the owner's account and upload the slip
     * ([SubscriptionPayMethod.ONLINE_TRANSFER]).
     *
     * @property payTo The account to send to — bank, branch, number and holder name.
     */
    public data class TransferAndUploadSlip(val payTo: PayTo) : ModeBPaymentStep

    /** Hand cash to the collector; the owner marks it received ([SubscriptionPayMethod.CASH]). */
    public data object HandToCollector : ModeBPaymentStep

    /**
     * The owner cannot be paid this way right now.
     *
     * Either the org has no verified payout profile (AL-49 — `409 payout-profile-not-verified`) or
     * the server sent no hand-off for the chosen method. Either way the subscriber must be told
     * rather than shown an empty sheet.
     */
    public data object Unavailable : ModeBPaymentStep
}

/**
 * BR-23.10's rules, as predicates.
 */
public object ModeBPaymentRules {

    /** Whether [method] requires the subscriber to upload a transfer slip (BR-23.10). */
    public fun requiresSlip(method: SubscriptionPayMethod): Boolean = method == SubscriptionPayMethod.ONLINE_TRANSFER

    /**
     * Whether [method] settles on the **owner's** confirmation rather than a gateway callback.
     *
     * `online_transfer` (owner confirms the slip) and `cash` (owner marks it received). Both sit in
     * `pending_verification` — or in nothing at all, for cash — until a human acts.
     */
    public fun requiresOwnerConfirmation(method: SubscriptionPayMethod): Boolean =
        method == SubscriptionPayMethod.ONLINE_TRANSFER || method == SubscriptionPayMethod.CASH

    /**
     * Whether [method] is driven by a payment gateway.
     *
     * The two LankaQR methods and OnePay. `lankaqr_scan` is the owner's *own* bank QR — the bank
     * moves the money, not MageRide — but it still resolves through the bank app, so it is grouped
     * with the hand-offs rather than with the human-settled pair.
     */
    public fun isGatewayDriven(method: SubscriptionPayMethod): Boolean = !requiresOwnerConfirmation(method)

    /**
     * What the pay sheet should do.
     *
     * @param method The subscriber's choice.
     * @param payment The initiated payment, when one has been created. `payTo` on it is the
     *   authoritative collection detail and is present only for a verified profile (AL-49).
     * @param bankAppAvailable Whether a bank app can open a LankaQR deep link on this handset
     *   (AL-15).
     */
    public fun stepFor(
        method: SubscriptionPayMethod,
        payment: SubscriptionPayment?,
        bankAppAvailable: Boolean = true,
    ): ModeBPaymentStep {
        val payTo = payment?.payTo
        return when (method) {
            SubscriptionPayMethod.CASH -> ModeBPaymentStep.HandToCollector

            SubscriptionPayMethod.ONLINE_TRANSFER ->
                if (payTo == null || payTo.accountNo.isNullOrBlank()) {
                    ModeBPaymentStep.Unavailable
                } else {
                    ModeBPaymentStep.TransferAndUploadSlip(payTo)
                }

            SubscriptionPayMethod.LANKAQR_SCAN ->
                payTo?.lankaqrImageUrl
                    ?.takeIf { it.isNotBlank() }
                    ?.let(ModeBPaymentStep::ShowOwnerLankaQr)
                    ?: ModeBPaymentStep.Unavailable

            // `subscription.yaml`'s SubscriptionPayment has no `paymentLink` field of its own the
            // way `fare.yaml`'s LankaqrInitiation does, so the deep link rides in `redirectUrl` and
            // `qrPayload` stays the AL-15 fallback. See the C016 handoff.
            SubscriptionPayMethod.LANKAQR_DEEPLINK -> handoff(
                PaymentMethods.lankaQrAction(payment?.redirectUrl, payment?.qrPayload, bankAppAvailable),
            )

            SubscriptionPayMethod.ONEPAY -> handoff(
                payment?.redirectUrl
                    ?.takeIf { it.isNotBlank() }
                    ?.let(FarePaymentAction::OpenOnepay)
                    ?: FarePaymentAction.Unavailable,
            )
        }
    }

    /**
     * Where the subscriber's month stands, from their latest payment for it (BR-23.10).
     *
     * A month with no payment row at all is [SubscriberMonthStatus.UNPAID] — the owner's roster
     * shows the same thing.
     */
    public fun monthStatus(payment: SubscriptionPayment?): SubscriberMonthStatus = when (payment?.status) {
        SubscriptionPaymentStatus.PAID -> SubscriberMonthStatus.PAID

        SubscriptionPaymentStatus.PENDING_VERIFICATION -> SubscriberMonthStatus.PENDING_VERIFICATION

        // `initiated` and `failed` both mean the owner has not been paid. An initiated gateway
        // payment that never completed is not a promise of money.
        else -> SubscriberMonthStatus.UNPAID
    }

    /**
     * Whether the owner has an action outstanding on this payment (BR-23.10, SCR-FP-012).
     *
     * Exactly the `pending_verification` slips — cash never creates a row until the owner marks it,
     * so there is nothing for them to confirm until they do.
     */
    public fun awaitsOwnerAction(payment: SubscriptionPayment): Boolean =
        payment.status == SubscriptionPaymentStatus.PENDING_VERIFICATION

    private fun handoff(action: FarePaymentAction): ModeBPaymentStep = if (action == FarePaymentAction.Unavailable) {
        ModeBPaymentStep.Unavailable
    } else {
        ModeBPaymentStep.GatewayHandoff(action)
    }
}

package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod

// What each payment method costs, who pays it, and what the app has to put on screen.
//
// Sources: D5' §8.1 (methods + the OnePay surcharge), P-04 (proxy payer routing), E-10 (tip),
// P-08 (COD is package-only), AL-15 (LankaQR is a DEEP LINK first, a QR only as fallback), AL-22
// (the passenger SCANS THE DRIVER'S QR — the app renders no MageRide QR), AL-47 (driver-QR is an
// attestation pair, not a gateway flow).
//
// THE SURCHARGE IS QUOTED BY THE SERVER, NOT COMPUTED HERE. `PaymentInitiation.surchargeMinor`
// carries the authoritative figure; [surchargeMinor] mirrors the §8.1 formula so a passenger can
// be shown "+5% (Rs 24)" on the method picker BEFORE committing to the round trip that produces it
// (US-8.11 requires exactly that disclosure). If the two ever disagree the server's number is the
// one charged.

/**
 * Who is charged, on a proxy booking (P-04).
 *
 * On a normal booking the booker *is* the rider and the distinction does not arise; the routing
 * rule still answers, and answers [RIDER].
 */
public enum class PayerRole {

    /** The person taking the ride — they hand cash to the driver, or scan the driver's QR. */
    RIDER,

    /** The person who made the booking — their account is charged (P-04). */
    BOOKER,
}

/**
 * What the pay sheet must actually do for the chosen method.
 *
 * A sealed set rather than a `when` inside a screen, because two of these are C016 fences and a
 * fence that lives in a UI file is a fence that gets edited:
 * - there is **no `ShowMageRideQr`** and there never will be (AL-22). The passenger scans the
 *   *driver's* QR with the camera; the app renders no QR of its own.
 * - [ShowLankaQrFallback] is reachable only when no bank app can open the deep link (AL-15). The
 *   deep link is the primary path, the QR is the fallback, and the ordering is the rule.
 */
public sealed interface FarePaymentAction {

    /** Cash in the vehicle, the platform default. The driver confirms collection (§8.1). */
    public data object CollectCash : FarePaymentAction

    /**
     * The fare came out of the passenger's wallet and is already settled (Δ AL-57).
     *
     * One balanced `trip_payment` entry, passenger wallet → driver wallet, terminal on the spot —
     * so the sheet has nothing to open and nothing to wait for.
     */
    public data object SettledFromWallet : FarePaymentAction

    /**
     * Open the bank app on a LankaQR "Pay" deep link (AL-15, US-8.10a).
     *
     * @property url The deep link the server minted.
     */
    public data class OpenBankApp(val url: String) : FarePaymentAction

    /**
     * Render the LankaQR payload as a scannable code — **fallback only** (AL-15).
     *
     * @property payload The EMVCo payload.
     */
    public data class ShowLankaQrFallback(val payload: String) : FarePaymentAction

    /**
     * Open OnePay's hosted page. Card and OnePay wallet are both choices made *there*.
     *
     * @property redirectUrl The hosted page.
     */
    public data class OpenOnepay(val redirectUrl: String) : FarePaymentAction

    /**
     * Open the camera and scan the **driver's** printed, on-screen or sticker QR (AL-22).
     *
     * Settles by attestation, not by webhook (AL-47): the money moves bank-to-bank and no gateway
     * ever tells the platform it did. See [DriverQrAttestation].
     */
    public data object ScanDriverQr : FarePaymentAction

    /** A package booked `cod`: nothing to do now, the driver collects on delivery (P-08). */
    public data object CollectOnDelivery : FarePaymentAction

    /**
     * The server did not send what this method needs.
     *
     * A OnePay initiation with no redirect URL, or a LankaQR one with neither link nor payload.
     * Surfaced rather than crashed: the passenger has already completed a ride and must be able to
     * fall back to cash (US-8.15).
     */
    public data object Unavailable : FarePaymentAction
}

/**
 * The method rules of D5' §8.1, P-04 and P-08.
 *
 * One small function per rule, deliberately: the surcharge, the payer, the offer gate and the
 * presentation are four independent questions with four different specs behind them, and a screen
 * asks one at a time. Folding them into a single `policyFor(method)` would return a bag whose
 * fields no caller could trace back to a line of spec.
 */
@Suppress("TooManyFunctions")
public object PaymentMethods {

    /**
     * OnePay's surcharge, whole percent (US-8.11, D5' §8.1 `surchargeMinor = round(fare*5/100)`).
     *
     * The only method that carries one. LankaQR explicitly does not ("no surcharge"), cash and COD
     * have no gateway, and driver-QR is bank-to-bank with **zero commission** (AL-47).
     */
    public const val ONEPAY_SURCHARGE_PCT: Int = 5

    /**
     * What [method] adds to a fare of [fareMinor], in minor units.
     *
     * Rounded once, half-to-even, exactly as §1.3 requires of every `*pct/100` product — the same
     * [FareRounding] the fare itself goes through, so a Rs 495 fare's 5% is Rs 24.75 → 2475 minor
     * units and not "about Rs 25".
     */
    public fun surchargeMinor(method: PaymentMethod, fareMinor: Long): Long =
        if (method == PaymentMethod.ONEPAY) FareRounding.percentOfMinor(fareMinor, ONEPAY_SURCHARGE_PCT) else 0L

    /** [surchargeMinor] as money. */
    public fun surcharge(method: PaymentMethod, fare: Money): Money =
        Money.ofMinor(surchargeMinor(method, fare.amountMinor))

    /**
     * What the passenger owes in total: fare + gateway surcharge + tip + any carried penalty.
     *
     * @param fare The ride's fare.
     * @param method How it is being paid.
     * @param tip Optional gratuity (E-10). Credited **directly to the driver's wallet** as a
     *   `tip_payout` journal kind, so it is not part of the fare and carries no surcharge — a
     *   passenger tipping Rs 100 through OnePay must not be charged Rs 105 for the privilege.
     * @param carriedPenalty The Rs 50 cross-trip cancellation debt settled on this ride (D-05,
     *   AL-16, D5' §7.1). Likewise not surchargeable: it is a transfer to the driver who was
     *   stood up, not a gateway service.
     */
    public fun amountDue(
        fare: Money,
        method: PaymentMethod,
        tip: Money = Money.ZERO,
        carriedPenalty: Money = Money.ZERO,
    ): Money = fare + surcharge(method, fare) + tip + carriedPenalty

    /**
     * Who is charged (P-04).
     *
     * "Cash ⇒ rider pays driver; LankaQR/OnePay ⇒ booker charged (`payer_role`, **regardless of
     * who is at pickup**)." The distinction is whether money changes hands in the vehicle or comes
     * out of an account: the person at the kerb has the cash, the person who booked has the card.
     *
     * **[PaymentMethod.SCAN_DRIVER_QR] postdates P-04** (it arrives with AL-22/AL-47) and no spec
     * routes it. Modelled as [PayerRole.RIDER] because it is physically a kerbside act — the payer
     * has to be standing in front of the driver's QR to scan it — which is the same reasoning P-04
     * applies to cash. Recorded in the C016 handoff.
     */
    public fun payerRole(method: PaymentMethod): PayerRole = when (method) {
        PaymentMethod.CASH, PaymentMethod.SCAN_DRIVER_QR -> PayerRole.RIDER

        // Δ AL-57 — the wallet is the booker's account, so it routes as the two retired gateway
        // rails did. `cod` is collected from whoever takes delivery, which is P-08's own rule and
        // the reason the booking-time overload below already answers RIDER for it.
        PaymentMethod.WALLET, PaymentMethod.LANKAQR, PaymentMethod.ONEPAY -> PayerRole.BOOKER

        PaymentMethod.COD -> PayerRole.RIDER
    }

    /** The booking-time spelling of [payerRole]. `cod` is collected from whoever takes delivery. */
    public fun payerRole(method: RidePaymentMethod): PayerRole = when (method) {
        RidePaymentMethod.CASH, RidePaymentMethod.COD -> PayerRole.RIDER
        RidePaymentMethod.LANKAQR, RidePaymentMethod.ONEPAY -> PayerRole.BOOKER
    }

    /**
     * Whether [method] may be offered for a ride of [kind].
     *
     * `cod` is **package-only** (P-08) — there is nothing to collect on delivery when the thing
     * delivered is a passenger. Everything else is offered for both.
     */
    public fun isOfferedFor(method: RidePaymentMethod, kind: RideKind): Boolean =
        method != RidePaymentMethod.COD || kind == RideKind.PACKAGE

    /** The settlement-time methods available for a ride booked as [method]. */
    public fun settlementMethodsFor(method: RidePaymentMethod): Set<PaymentMethod> = when (method) {
        // A COD package settles on delivery and nowhere else (P-08).
        RidePaymentMethod.COD -> emptySet()

        // Anything else may still end in cash (US-8.15) or by scanning the driver's QR (AL-22),
        // whichever way it was booked — the booking-time choice is a preference, not a lock.
        // Δ AL-57/AL-59: the two platform-merchant rails are retired, and card acceptance moved
        // to the wallet. A ride booked any way may still end in cash (US-8.15), out of the
        // passenger's wallet, or by scanning the driver's own QR (AL-22) — the booking-time
        // choice is a preference, not a lock.
        else -> setOf(PaymentMethod.CASH, PaymentMethod.WALLET, PaymentMethod.SCAN_DRIVER_QR)
    }

    /**
     * What the pay sheet does next, given what the server sent back.
     *
     * **No `bankAppAvailable` parameter any more.** AL-15's deep-link-then-QR rule applied to the
     * platform-merchant LankaQR ride rail, which AL-57 retired; the rule itself is unchanged and
     * still governs the wallet **top-up** sheet, which calls [lankaQrAction] directly. Keeping a
     * parameter no branch reads would have suggested this function still made that choice.
     *
     * @param initiation The `POST /v1/fare/pay` response.
     */
    public fun actionFor(initiation: PaymentInitiation): FarePaymentAction = when (initiation.method) {
        PaymentMethod.CASH, PaymentMethod.COD -> FarePaymentAction.CollectCash

        PaymentMethod.SCAN_DRIVER_QR -> FarePaymentAction.ScanDriverQr

        // Δ AL-57 — the wallet fare is one balanced ledger entry, terminal on the spot. There
        // is no gateway round trip to hand off to and nothing for the sheet to open.
        PaymentMethod.WALLET -> FarePaymentAction.SettledFromWallet

        // Δ AL-57/AL-59 — both platform-merchant ride rails are retired, and `fare.yaml`'s
        // `PaymentMethod` is now `[cash, wallet, scan_driver_qr, cod]`. fare-svc cannot answer
        // with either any more, so there is no block to act on and no sheet to open.
        //
        // The two values SURVIVE in this enum on purpose: `ride.yaml`'s booking-time
        // `RidePaymentMethod` and `iam.yaml`'s `DefaultPaymentMethod` still declare them, so
        // the platform is mid-migration and removing them here would make three contracts
        // disagree three ways instead of two. Recorded as a finding in the MCS-02 handoff.
        PaymentMethod.ONEPAY, PaymentMethod.LANKAQR -> FarePaymentAction.Unavailable
    }

    /**
     * The AL-15 rule on its own, for the wallet top-up sheet, which faces the same choice.
     *
     * @param paymentLink The "Pay" deep link, when the server sent one.
     * @param qrPayload The scannable fallback, when the server sent one.
     * @param bankAppAvailable Whether a bank app can open the deep link on this handset.
     */
    public fun lankaQrAction(paymentLink: String?, qrPayload: String?, bankAppAvailable: Boolean): FarePaymentAction =
        when {
            bankAppAvailable && !paymentLink.isNullOrBlank() -> FarePaymentAction.OpenBankApp(paymentLink)

            !qrPayload.isNullOrBlank() -> FarePaymentAction.ShowLankaQrFallback(qrPayload)

            // No bank app and no payload: the deep link would go nowhere. Better to say so than to
            // hand the passenger a link that opens a browser error.
            else -> FarePaymentAction.Unavailable
        }
}

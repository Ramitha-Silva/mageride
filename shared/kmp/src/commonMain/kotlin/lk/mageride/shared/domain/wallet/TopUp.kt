package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupState
import lk.mageride.shared.domain.fare.FarePaymentAction
import lk.mageride.shared.domain.fare.PaymentMethods

// Wallet top-up (US-9.18, AL-05, AL-15, D5' §9.3).
//
// "Bank-transfer top-ups removed — top-up = OnePay card / OnePay wallet / LankaQR only (+ bulk
//  credit vouchers)." (D5' §9.3, AL-01/AL-05)
//
// THIS IS A C016 FENCE AND A DEFINITION-OF-DONE ITEM: no bank-transfer top-up path exists anywhere
// in this module. [TopupMethod] has three entries and there is no fourth to add — `wallet.yaml`
// carries no `POST /v1/wallet/topup/bank-transfer` (C012 removed it and said so), `WalletApi` has
// no such call, and `MoneyDomainHygieneTest` fails the build if the words reappear in this
// package's source.
//
// WHAT IS *NOT* A BANK TRANSFER: the Mode B subscription method `online_transfer`
// (domain/subscription). That is a passenger paying a FLEET OWNER directly, with a slip the owner
// verifies — pass-through money that never touches a MageRide wallet or the platform ledger
// (AL-24, §18b). AL-05 is about topping up a DRIVER'S WALLET, which only the two gateway rails do.
//
// THE FOURTH WAY IN IS A VOUCHER. A bulk-credit voucher purchase credits the buyer's own wallet
// (US-9.19) and is modelled in [VoucherCatalogue] — it is a purchase, not a top-up rail, which is
// why it is not a [TopupMethod].

/**
 * How a driver may add money to their wallet.
 *
 * Three entries, and the list is closed (AL-05). The two OnePay entries are one endpoint —
 * `POST /v1/wallet/topup/onepay` — because the card-versus-wallet choice happens on OnePay's own
 * hosted page; they are separate here because the driver app offers them as separate tiles and a
 * tile has to know which one it is.
 */
public enum class TopupMethod {

    /** A card, through OnePay's hosted page. */
    ONEPAY_CARD,

    /** A OnePay wallet balance, through the same hosted page. */
    ONEPAY_WALLET,

    /** A LankaQR "Pay" deep link into the driver's own bank app (AL-15). */
    LANKAQR,
    ;

    /** Whether this method goes through the OnePay rail. */
    public val isOnepay: Boolean get() = this != LANKAQR
}

/** Which `WalletApi` call a [TopupMethod] uses. */
public enum class TopupRoute {

    /** `POST /v1/wallet/topup/onepay`. */
    ONEPAY,

    /** `POST /v1/wallet/topup/lankaqr`. */
    LANKAQR,
}

/**
 * The top-up rules a driver app renders.
 */
public object TopupRules {

    /** Which endpoint [method] goes to. */
    public fun routeFor(method: TopupMethod): TopupRoute =
        if (method.isOnepay) TopupRoute.ONEPAY else TopupRoute.LANKAQR

    /**
     * What the top-up sheet does next, given the server's response.
     *
     * Reuses the fare side's AL-15 rule ([PaymentMethods.lankaQrAction]) rather than restating it:
     * "deep link first, QR only when no bank app can open it" is one rule, and two copies of it
     * would eventually disagree.
     *
     * @param topup The `POST /v1/wallet/topup/onepay` or `.../lankaqr` response.
     * @param method The method the driver chose.
     * @param bankAppAvailable Whether a bank app can open a LankaQR deep link on this handset.
     */
    public fun actionFor(topup: Topup, method: TopupMethod, bankAppAvailable: Boolean = true): FarePaymentAction =
        when {
            method.isOnepay ->
                topup.redirectUrl
                    ?.takeIf { it.isNotBlank() }
                    ?.let(FarePaymentAction::OpenOnepay)
                    ?: FarePaymentAction.Unavailable

            else -> PaymentMethods.lankaQrAction(topup.paymentLink, topup.qrPayload, bankAppAvailable)
        }

    /**
     * The balanced entry a **settled** top-up posts (D-09).
     *
     * `topup` moves money from the platform account into the driver's wallet, and it posts **only
     * on the gateway webhook** — never when the app initiates one (C012's `OnepayTopupRequest`
     * says so). A [Topup] the client is still holding in `Pending` therefore projects nothing.
     */
    public fun entryFor(topup: Topup, driverId: Ulid): LedgerEntry? {
        if (topup.state != TopupState.Succeeded) return null
        return LedgerEntry(
            kind = JOURNAL_KIND,
            idempotencyKey = "$JOURNAL_KIND:${topup.topupId}",
            postings = listOf(
                LedgerPosting(LedgerAccount.PLATFORM, -topup.amountMinor),
                LedgerPosting(LedgerAccount.driver(driverId), topup.amountMinor),
            ),
        )
    }

    /** The `billing.journal_entries.kind` a top-up posts under (C005 CHECK). */
    public const val JOURNAL_KIND: String = "topup"

    /** What the wallet is worth after a settled top-up of [amount]. */
    public fun balanceAfter(standing: WalletStanding, amount: Money): Money = standing.available + amount
}

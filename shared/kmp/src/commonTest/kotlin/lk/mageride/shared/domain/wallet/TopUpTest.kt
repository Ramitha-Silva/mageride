package lk.mageride.shared.domain.wallet

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupState
import lk.mageride.shared.domain.fare.FarePaymentAction
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The C016 definition of done: **no bank-transfer top-up path exists anywhere in the module**
 * (AL-05, D5' §9.3).
 *
 * "Bank-transfer top-ups removed — top-up = OnePay card / OnePay wallet / LankaQR only (+ bulk
 * credit vouchers)." The runtime half of that claim is here; the source-level half — that the words
 * do not reappear anywhere in the money packages — is `MoneyDomainHygieneTest`.
 */
class TopUpTest {

    private val driver = "01JDRV0000000000000000001"

    private fun topup(
        state: TopupState = TopupState.Pending,
        redirectUrl: String? = null,
        paymentLink: String? = null,
        qrPayload: String? = null,
    ) = Topup(
        topupId = "01JTOP0000000000000000001",
        state = state,
        amountMinor = 100_000,
        redirectUrl = redirectUrl,
        paymentLink = paymentLink,
        qrPayload = qrPayload,
    )

    @Test
    fun there_are_exactly_three_top_up_methods_and_none_of_them_is_a_bank_transfer() {
        assertEquals(
            setOf(TopupMethod.ONEPAY_CARD, TopupMethod.ONEPAY_WALLET, TopupMethod.LANKAQR),
            TopupMethod.entries.toSet(),
        )
    }

    @Test
    fun the_two_onepay_tiles_are_one_endpoint() {
        // The card-versus-wallet choice happens on OnePay's own hosted page; C012 consolidated
        // `POST /v1/wallet/topup/card` into the OnePay route and said so.
        assertEquals(TopupRoute.ONEPAY, TopupRules.routeFor(TopupMethod.ONEPAY_CARD))
        assertEquals(TopupRoute.ONEPAY, TopupRules.routeFor(TopupMethod.ONEPAY_WALLET))
        assertEquals(TopupRoute.LANKAQR, TopupRules.routeFor(TopupMethod.LANKAQR))

        assertTrue(TopupMethod.ONEPAY_CARD.isOnepay)
        assertFalse(TopupMethod.LANKAQR.isOnepay)
    }

    @Test
    fun lankaqr_top_up_follows_the_same_al_15_rule_as_the_fare_side() {
        val both = topup(paymentLink = "lankaqr://pay?t=1", qrPayload = "0002010102")

        assertEquals(
            FarePaymentAction.OpenBankApp("lankaqr://pay?t=1"),
            TopupRules.actionFor(both, TopupMethod.LANKAQR, bankAppAvailable = true),
        )
        assertEquals(
            FarePaymentAction.ShowLankaQrFallback("0002010102"),
            TopupRules.actionFor(both, TopupMethod.LANKAQR, bankAppAvailable = false),
        )
    }

    @Test
    fun a_onepay_top_up_opens_its_hosted_page_and_reports_an_empty_one() {
        assertEquals(
            FarePaymentAction.OpenOnepay("https://onepay.lk/t/1"),
            TopupRules.actionFor(topup(redirectUrl = "https://onepay.lk/t/1"), TopupMethod.ONEPAY_CARD),
        )
        assertEquals(
            FarePaymentAction.Unavailable,
            TopupRules.actionFor(topup(), TopupMethod.ONEPAY_WALLET),
        )
    }

    @Test
    fun nothing_is_credited_until_the_gateway_settles() {
        // C012: "The wallet is credited ONLY on the webhook." A pending top-up projects no entry,
        // so an app that optimistically added the money would have nothing to add it from.
        assertNull(TopupRules.entryFor(topup(TopupState.Pending), driver))
        assertNull(TopupRules.entryFor(topup(TopupState.Failed), driver))

        val entry = assertNotNull(TopupRules.entryFor(topup(TopupState.Succeeded), driver))
        assertEquals(Money.ofMinor(100_000), entry.netFor(LedgerAccount.driver(driver)))
        assertEquals(Money.ofMinor(-100_000), entry.netFor(LedgerAccount.PLATFORM))
        assertEquals("topup:01JTOP0000000000000000001", entry.idempotencyKey)
    }

    @Test
    fun a_settled_top_up_raises_the_spendable_balance() {
        val standing = WalletStanding(
            userId = driver,
            balance = Money.ofMinor(5_000),
            available = Money.ofMinor(5_000),
        )

        assertEquals(Money.ofMinor(105_000), TopupRules.balanceAfter(standing, Money.ofMinor(100_000)))
    }
}

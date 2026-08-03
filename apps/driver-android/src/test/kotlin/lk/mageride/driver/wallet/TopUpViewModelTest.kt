package lk.mageride.driver.wallet

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.subscription.VoucherDiscountTierList
import lk.mageride.shared.data.models.subscription.VoucherPayMethod
import lk.mageride.shared.data.models.subscription.VoucherPurchase
import lk.mageride.shared.data.models.wallet.Topup
import lk.mageride.shared.data.models.wallet.TopupState
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-022 — the three rails, the voucher ladder, and the callback the wallet is credited on.
 *
 * The DoD case is here: *"a Rs 1,000 voucher at a 10 % tier shows 'pay Rs 900, get Rs 1,000' and
 * credits correctly"*.
 */
@OptIn(ExperimentalTime::class)
class TopUpViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val handoff = FakePaymentHandoff()

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns(
            "listVoucherDiscountTiers",
            VoucherDiscountTierList(listOf(tier(ONE_THOUSAND, discountBps = 1_000), tier(500_000L, 1_500))),
        )
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_thousand_rupee_voucher_at_ten_percent_is_pay_nine_hundred_get_a_thousand() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.selectVoucher(ONE_THOUSAND)
        val state = model.state.value

        assertEquals(90_000L, state.payableMinor, "Rs 900 — the discount lives entirely in the price")
        assertEquals(ONE_THOUSAND, state.creditedMinor, "Rs 1,000 — `ck_voucher_credit_full`")
        assertEquals("1000", state.amount, "the tile fills the field with the FACE value it is buying")
        assertEquals(1_000, state.quote?.discountBps)
    }

    @Test
    fun buying_that_voucher_goes_to_the_purchase_route_and_credits_the_face_value() = runBlocking {
        backend.returns("purchaseVoucher", purchase(paidMinor = 90_000L, creditedMinor = ONE_THOUSAND))

        val model = viewModel()
        model.state.await { !it.loading }
        model.selectVoucher(ONE_THOUSAND)
        model.pay()

        val receipt = model.state.await { it.receipt != null }.receipt
        assertEquals(90_000L, receipt?.paidMinor)
        assertEquals(ONE_THOUSAND, receipt?.creditedMinor)
        // subscription-svc posts the credit on the gateway's confirmation and offers no read to
        // poll, so the receipt is an acknowledgement rather than a balance.
        assertFalse(receipt!!.settled)

        // And crucially NOT a top-up of the discounted price: that would credit Rs 900 on the
        // webhook and Rs 1,000 on the purchase — Rs 1,900 for a Rs 1,000 voucher.
        assertFalse(backend.called("topupWithOnepay"))
        val body = MageRideJson.parseToJsonElement(backend.lastCall("purchaseVoucher").body).toString()
        assertTrue(body.contains("\"denominationMinor\":$ONE_THOUSAND"), body)
        assertTrue(body.contains(VoucherPayMethod.ONEPAY.wire), body)
    }

    @Test
    fun typing_an_amount_clears_the_tile_so_the_cta_never_prices_two_things() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.selectVoucher(ONE_THOUSAND)
        model.onAmountChange("2500")

        val state = model.state.value
        assertNull(state.voucherDenominationMinor)
        assertNull(state.quote)
        assertEquals(250_000L, state.payableMinor)
        assertEquals(250_000L, state.creditedMinor, "a plain top-up credits exactly what is paid")
    }

    @Test
    fun a_plain_top_up_is_one_call_and_the_wallet_is_credited_on_the_callback() = runBlocking {
        backend.returns("topupWithOnepay", topup(state = TopupState.Pending, redirectUrl = "https://onepay/x"))

        val model = viewModel()
        model.state.await { !it.loading }
        model.onAmountChange("2000")
        model.pay()

        val pending = model.state.await { it.pending != null }.pending
        assertEquals(200_000L, pending?.amountMinor)
        assertEquals(listOf("https://onepay/x"), handoff.opened, "the driver is sent to the hosted page")
        assertNull(model.state.value.receipt, "nothing is credited until the webhook lands")
    }

    @Test
    fun lankaqr_takes_the_other_endpoint_and_the_deep_link_first() = runBlocking {
        backend.returns(
            "topupWithLankaqr",
            topup(state = TopupState.Pending, paymentLink = "bank://pay/1", qrPayload = "00020101"),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        model.selectMethod(lk.mageride.shared.domain.wallet.TopupMethod.LANKAQR)
        model.onAmountChange("2000")
        model.pay()

        model.state.await { it.pending != null }
        assertTrue(backend.called("topupWithLankaqr"))
        assertFalse(backend.called("topupWithOnepay"), "LankaQR is a different rail, not a OnePay flavour")
        // AL-15: the deep link is the primary path and the code is only the fallback.
        assertEquals(listOf("bank://pay/1"), handoff.opened)
        assertNull(model.state.value.fallbackQr)
    }

    @Test
    fun the_qr_is_shown_only_when_no_bank_app_could_open_the_link() = runBlocking {
        // Tried, not asked: package-visibility filtering hides an app this one has not declared a
        // `<queries>` entry for, and a LankaQR link's scheme is the issuing bank's. See PaymentHandoff.
        handoff.handled = false
        backend.returns(
            "topupWithLankaqr",
            topup(state = TopupState.Pending, paymentLink = "bank://pay/1", qrPayload = "00020101"),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        model.selectMethod(lk.mageride.shared.domain.wallet.TopupMethod.LANKAQR)
        model.onAmountChange("2000")
        model.pay()

        assertEquals("00020101", model.state.await { it.fallbackQr != null }.fallbackQr)
    }

    @Test
    fun a_session_that_settles_becomes_the_receipt() = runBlocking {
        backend.returns("topupWithOnepay", topup(state = TopupState.Pending, redirectUrl = "https://onepay/x"))
        backend.returns("getTopup", topup(state = TopupState.Succeeded))

        val model = viewModel()
        model.state.await { !it.loading }
        model.onAmountChange("2000")
        model.pay()
        model.state.await { it.pending != null }

        model.resumeFromGateway()

        val receipt = model.state.await { it.receipt != null }.receipt
        assertEquals(200_000L, receipt?.paidMinor)
        assertEquals(200_000L, receipt?.creditedMinor)
        assertTrue(receipt!!.settled, "the webhook has landed and the ledger has moved")
        assertNull(model.state.value.pending)
    }

    @Test
    fun a_failed_session_keeps_the_figure_so_the_retry_is_one_tap() = runBlocking {
        // The wireframe's "Failed → retry".
        backend.returns("topupWithOnepay", topup(state = TopupState.Pending, redirectUrl = "https://onepay/x"))
        backend.returns("getTopup", topup(state = TopupState.Failed))

        val model = viewModel()
        model.state.await { !it.loading }
        model.onAmountChange("2000")
        model.pay()
        model.state.await { it.pending != null }

        model.resumeFromGateway()

        val state = model.state.await { it.error != null }
        assertEquals("2000", state.amount)
        assertNull(state.receipt)
        assertNull(state.pending)
    }

    @Test
    fun a_window_that_closes_on_a_pending_session_says_the_credit_is_coming() = runBlocking {
        // D6' §7.1's 90-second window. A late webhook is not a failed payment, and telling a driver
        // who has paid that nothing happened is worse than saying it is on its way.
        backend.returns("topupWithOnepay", topup(state = TopupState.Pending, redirectUrl = "https://onepay/x"))
        backend.returns("getTopup", topup(state = TopupState.Pending))

        val model = viewModel(pollInterval = 1.milliseconds, pendingWindow = 3.milliseconds)
        model.state.await { !it.loading }
        model.onAmountChange("2000")
        model.pay()
        model.state.await { it.pending != null }

        model.resumeFromGateway()

        val state = model.state.await { it.pending?.timedOut == true }
        assertNull(state.error, "a slow webhook is not an error")
        assertNull(state.receipt)
        assertFalse(state.awaitingGateway)
    }

    @Test
    fun a_refused_amount_becomes_copy_and_never_a_pending_session() = runBlocking {
        backend.fails("topupWithOnepay", HttpStatusCode.BadRequest, "invalid-amount")

        val model = viewModel()
        model.state.await { !it.loading }
        model.onAmountChange("1")
        model.pay()

        val state = model.state.await { it.error != null }
        assertNull(state.pending)
        assertFalse(state.submitting)
        assertTrue(handoff.opened.isEmpty(), "there was nothing to open")
    }

    @Test
    fun a_catalogue_with_nothing_on_sale_offers_nothing_rather_than_a_rate_nobody_set() = runBlocking {
        backend.returns("listVoucherDiscountTiers", VoucherDiscountTierList(listOf(tier(ONE_THOUSAND, 1_000, false))))

        val state = viewModel().state.await { !it.loading }

        assertTrue(state.vouchers.isEmpty(), "an inactive tier is not on sale")
        assertNull(state.quote)
    }

    private fun topup(
        state: TopupState,
        redirectUrl: String? = null,
        paymentLink: String? = null,
        qrPayload: String? = null,
    ) = Topup(
        topupId = Fixtures.TRANSACTION_ID,
        state = state,
        amountMinor = 200_000L,
        redirectUrl = redirectUrl,
        paymentLink = paymentLink,
        qrPayload = qrPayload,
    )

    private fun purchase(paidMinor: Long, creditedMinor: Long) = VoucherPurchase(
        purchaseId = Fixtures.TRANSACTION_ID,
        denominationMinor = creditedMinor,
        discountBpsApplied = 1_000,
        paidMinor = paidMinor,
        creditedMinor = creditedMinor,
        currency = Currency.LKR,
        redirectUrl = "https://onepay/voucher",
    )

    private fun viewModel(
        pollInterval: kotlin.time.Duration = 1.milliseconds,
        pendingWindow: kotlin.time.Duration = 5_000.milliseconds,
    ): TopUpViewModel {
        val api = backend.mageRideApi()
        // Owned: the gateway poll is a `while (…) { delay(…) }`, and a test that ends mid-window
        // would leave it waking up inside the next class's `Dispatchers.resetMain()`.
        return main.own(
            TopUpViewModel(
                topUps = TopUpRepository(wallet = api.wallet, subscription = api.subscription),
                handoff = handoff,
                pollInterval = pollInterval,
                pendingWindow = pendingWindow,
            ),
        )
    }
}

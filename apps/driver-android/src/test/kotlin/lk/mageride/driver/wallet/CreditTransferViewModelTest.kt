package lk.mageride.driver.wallet

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.wallet.TransferDirection
import lk.mageride.shared.data.models.wallet.TransferRow
import lk.mageride.shared.data.models.wallet.TransferStatus
import lk.mageride.shared.domain.wallet.CreditTransferRejection
import lk.mageride.shared.domain.wallet.CreditTransferRules
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-DA-024 — the approval inbox and the direct send.
 *
 * The DoD case is here: *"a transfer of Rs 500 shows Rs 500 debited and Rs 500 received with no
 * fee"*. AL-01 is not a rate of zero; it is the absence of a leg any journal kind could carry.
 */
class CreditTransferViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()

    private val requestId = "01JTRANSFER00000000000001"

    /** The signed-in driver, as `DriverIdentity` resolves it. Set by [viewModel]. */
    private var signedInDriverId: String? = null

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("getWallet", wallet(balanceMinor = 200_000L))
        backend.returns("listPendingWalletCreditTransfers", onePage<TransferRow>())
        backend.returns("listWalletTransfers", onePage<TransferRow>())
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun five_hundred_sent_is_five_hundred_received_with_nothing_taken_off() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.onRecipientIdChange(OTHER_DRIVER_ID)
        model.onAmountChange("500")

        val state = model.state.value
        assertEquals(Money.ofMinor(50_000L), state.debited)
        assertEquals(Money.ofMinor(50_000L), state.credited)
        assertEquals(state.debited, state.credited, "AL-01 — the exact value, on both legs")
        // Not a configured rate of zero: there is no journal kind a fee could post under, and
        // `entryFor` produces two postings that sum to zero.
        assertEquals(Money.ZERO, CreditTransferRules.feeFor(Money.ofMinor(50_000L)))
        assertEquals(0, CreditTransferRules.COMMISSION_BPS)
    }

    @Test
    fun sending_posts_the_amount_and_nothing_else() = runBlocking {
        backend.returns(
            "initiateWalletCreditTransfer",
            transfer(requestId, 50_000L, TransferDirection.SENT, TransferStatus.APPROVED),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        model.onRecipientIdChange(OTHER_DRIVER_ID)
        model.onAmountChange("500")

        assertTrue(model.canSend())
        model.send()

        val sent = model.state.await { it.sent != null }
        assertEquals(50_000L, sent.sent?.amountMinor)
        assertEquals("", sent.recipientId, "the form clears so a second tap is not a second transfer")

        val body = MageRideJson.parseToJsonElement(backend.lastCall("initiateWalletCreditTransfer").body).toString()
        assertTrue(body.contains("\"amountMinor\":50000"), body)
        assertTrue(body.contains(OTHER_DRIVER_ID), body)
        assertFalse(body.contains("commission"), "there is no such field, and there is no such concept")
    }

    @Test
    fun a_send_beyond_the_spendable_balance_is_refused_before_the_round_trip() = runBlocking {
        // Checked against `available`, not the raw balance: an outstanding penalty (D-05) is money
        // already owed, and letting a driver transfer it away would move the debt rather than settle it.
        backend.returns("getWallet", wallet(balanceMinor = 30_000L, debtMinor = 20_000L))

        val model = viewModel()
        model.state.await { !it.loading }
        model.onRecipientIdChange(OTHER_DRIVER_ID)
        model.onAmountChange("250")

        assertEquals(CreditTransferRejection.INSUFFICIENT_BALANCE, model.rejectionForSend())
        assertFalse(model.canSend())

        model.onAmountChange("100")
        assertNull(model.rejectionForSend(), "Rs 100 is exactly what is left after the debt")
        assertTrue(model.canSend())
    }

    @Test
    fun a_driver_cannot_send_credit_to_themselves() = runBlocking {
        // `ck_credit_transfers_not_self` refuses it server-side; catching it here saves a round trip
        // and lets the screen say so at the keyboard.
        val model = viewModel()
        model.state.await { !it.loading }
        model.onRecipientIdChange(assertNotNull(signedInDriverId))
        model.onAmountChange("500")

        assertEquals(CreditTransferRejection.SELF_TRANSFER, model.rejectionForSend())
        assertFalse(model.canSend())
    }

    @Test
    fun a_malformed_driver_id_never_reaches_the_gateway() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }
        model.onRecipientIdChange("DRV-90431")
        model.onAmountChange("500")

        assertTrue(model.state.value.recipientIdRejected)
        assertFalse(model.canSend())

        model.send()
        assertFalse(backend.called("initiateWalletCreditTransfer"))
    }

    @Test
    fun approving_a_request_moves_it_out_of_the_inbox_and_into_the_history() = runBlocking {
        backend.returns("listPendingWalletCreditTransfers", onePage(transfer(requestId, ONE_THOUSAND)))
        backend.returns(
            "approveWalletCreditTransfer",
            transfer(requestId, ONE_THOUSAND, TransferDirection.SENT, TransferStatus.APPROVED),
        )

        val model = viewModel()
        model.state.await { it.incoming.isNotEmpty() }

        model.approve(requestId)

        val state = model.state.await { it.incoming.isEmpty() }
        assertEquals(listOf(requestId), state.history.map { it.transferId })
        assertNull(state.busyTransferId)
    }

    @Test
    fun declining_posts_nothing_and_writes_no_history_line() = runBlocking {
        // US-9.12 — a rejected request never carries a journal entry (`ck_credit_transfers_posting`).
        backend.returns("listPendingWalletCreditTransfers", onePage(transfer(requestId, ONE_THOUSAND)))
        backend.returns(
            "rejectWalletCreditTransfer",
            transfer(requestId, ONE_THOUSAND, status = TransferStatus.REJECTED),
        )

        val model = viewModel()
        model.state.await { it.incoming.isNotEmpty() }

        model.reject(requestId)

        val state = model.state.await { it.incoming.isEmpty() }
        assertTrue(state.history.isEmpty(), "nothing moved, so there is nothing to show")
    }

    @Test
    fun an_approval_refused_for_want_of_balance_leaves_the_request_in_the_inbox() = runBlocking {
        // The server checks again at approval time, which is exactly when a holder's balance may
        // have moved since the request was raised.
        backend.returns("listPendingWalletCreditTransfers", onePage(transfer(requestId, ONE_THOUSAND)))
        backend.fails("approveWalletCreditTransfer", HttpStatusCode.PaymentRequired, "insufficient-wallet")

        val model = viewModel()
        model.state.await { it.incoming.isNotEmpty() }

        model.approve(requestId)

        val state = model.state.await { it.error != null }
        assertEquals(listOf(requestId), state.incoming.map { it.transferId }, "still there to be looked at")
        assertNull(state.busyTransferId)
    }

    @Test
    fun the_inbox_is_read_rather_than_waited_for_on_a_push() = runBlocking {
        // D2' says the requests "arrive via push" and no such notification type exists —
        // `NotificationCatalogue` declares twenty-six and none is a credit transfer. A list that
        // only filled on a push would be permanently empty.
        backend.returns("listPendingWalletCreditTransfers", onePage(transfer(requestId, ONE_THOUSAND)))

        val model = viewModel()
        model.state.await { it.incoming.isNotEmpty() }

        assertTrue(backend.called("listPendingWalletCreditTransfers"))
    }

    private suspend fun viewModel(): CreditTransferViewModel {
        val api = backend.mageRideApi()
        val identity = identity(backend, signedInSessions(backend))
        signedInDriverId = identity.driverId
        return main.own(
            CreditTransferViewModel(
                identity = identity,
                transfers = CreditTransferRepository(wallet = api.wallet),
                wallet = WalletRepository(wallet = api.wallet, subscription = api.subscription),
            ),
        )
    }
}

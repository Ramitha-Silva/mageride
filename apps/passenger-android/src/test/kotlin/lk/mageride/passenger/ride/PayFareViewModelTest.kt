package lk.mageride.passenger.ride

import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes

/**
 * SCR-PA-017, and the Definition-of-Done line *"the QR flow reaches Confirmed ✓ on
 * DriverConfirmedQR and offers support after 5 unconfirmed minutes"*.
 *
 * **This is the whole of AL-47.** The passenger pays into the driver's own bank, so **no callback
 * ever reaches fare-svc** — there is no gateway to ask. The only oracle is the two parties, and
 * the states below are that conversation: `QrClaimedByPassenger` is the passenger's half,
 * `DriverConfirmedQR` is the driver's, and it is terminal because the earning posts on it (R-05).
 */
class PayFareViewModelTest {

    private val main = MainDispatcher()
    private val rides = FakeRideRepository()
    private var clock: Timestamp = Fixtures.NOW

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_driver_qr_rail_brings_back_the_drivers_own_qr_and_settles_nothing() = runBlocking {
        // AL-59: the QR is the DRIVER's, served as a signed URL from their payout profile. The
        // app renders none of its own (AL-22), and nothing has settled — money into somebody
        // else's bank produces no platform event.
        val model = viewModel()
        val state = model.state.await { it.qrImageUrl != null }

        assertEquals(listOf(PaymentMethod.SCAN_DRIVER_QR), rides.payments)
        assertFalse(state.confirmed)
        assertEquals(PaymentState.Initiated, state.paymentState)
    }

    @Test
    fun a_scanned_payload_goes_to_the_server_exactly_as_read() = runBlocking {
        // It is the driver's bank merchant string. This app does not parse it — interpreting
        // somebody else's bank payload is not a claim it is in a position to make.
        val model = viewModel()
        model.state.await { it.paymentId != null }

        model.onQrScanned("00020101021229...LK")
        model.state.await { !it.busy }

        assertEquals(listOf("00020101021229...LK"), rides.scans)
    }

    @Test
    fun claiming_then_a_driver_confirm_reaches_confirmed() = runBlocking {
        // The DoD line. `QrClaimedByPassenger` is the wait, NOT the settlement — treating it as
        // settled would tell a passenger their driver had confirmed before the driver was asked.
        rides.statusStates = ArrayDeque(listOf(PaymentState.QrClaimedByPassenger, PaymentState.DriverConfirmedQR))
        val model = viewModel()
        model.state.await { it.paymentId != null }

        model.claimPaid()
        val claimed = model.state.await { it.claimed }
        assertFalse(claimed.confirmed, "a claim is not a confirmation")

        val done = model.state.await { it.confirmed }
        assertEquals(PaymentState.DriverConfirmedQR, done.paymentState)
        assertEquals(FakeRideRepository.RIDE_ID, rides.claims.single().first)
        assertNull(rides.claims.single().second, "no receipt was attached on this path")
    }

    @Test
    fun a_receipt_screenshot_travels_with_the_claim() = runBlocking {
        // "This is what a dispute is adjudicated on" — there is no gateway record to fall back to,
        // so the passenger's own receipt is the entire evidence trail.
        rides.statusStates = ArrayDeque(listOf(PaymentState.DriverConfirmedQR))
        val model = viewModel()
        model.state.await { it.paymentId != null }

        model.claimPaid(receiptArtifactId = ARTIFACT_ID)
        model.state.await { it.claimed }

        assertEquals(ARTIFACT_ID, rides.claims.single().second)
    }

    @Test
    fun five_unconfirmed_minutes_offers_support() = runBlocking {
        // AL-47 re-pushes the driver at +5 min; past that the passenger is offered Support, which
        // routes to the Finance dispute queue. No money moves either way — the platform holds none.
        val state = PayFareState(
            claimed = true,
            paymentState = PaymentState.QrClaimedByPassenger,
            secondsWaiting = PayFareState.UNCONFIRMED_SECONDS,
        )

        assertTrue(state.offerSupport)
        assertFalse(state.copy(secondsWaiting = PayFareState.UNCONFIRMED_SECONDS - 1).offerSupport)
        // And never once it has settled: a confirmed payment has nothing to get help about.
        assertFalse(state.copy(paymentState = PaymentState.DriverConfirmedQR).offerSupport)
    }

    @Test
    fun the_wait_is_measured_from_the_claim_and_not_from_the_screen_opening() = runBlocking {
        // A passenger who sat on this screen for four minutes deciding has not been waiting for a
        // driver for four minutes, and offering them support immediately would be wrong.
        rides.statusStates = ArrayDeque(listOf(PaymentState.QrClaimedByPassenger))
        val model = viewModel()
        model.state.await { it.paymentId != null }
        clock += 4.minutes

        model.claimPaid()
        val claimed = model.state.await { it.claimed }

        assertTrue(claimed.secondsWaiting < PayFareState.UNCONFIRMED_SECONDS)
        assertFalse(claimed.offerSupport)
    }

    @Test
    fun a_wallet_payment_is_settled_the_moment_it_returns() = runBlocking {
        // AL-57 — one balanced `trip_payment` journal entry, passenger wallet to driver wallet,
        // inside one transaction. No gateway, no `Pending`, nothing to poll.
        val model = viewModel()
        model.state.await { it.paymentId != null }

        model.setMethod(PaymentMethod.WALLET)
        val state = model.state.await { it.paymentState == PaymentState.Succeeded }

        assertTrue(state.confirmed)
        assertTrue(PaymentMethod.WALLET in rides.payments)
    }

    @Test
    fun switching_to_cash_keeps_the_payment_rather_than_starting_a_new_one() = runBlocking {
        // US-8.15 — "without losing history". `FellBackToCash` is a transition on the same
        // `fares.ride_payments` row, which is what makes a later reconciliation possible.
        val model = viewModel()
        model.state.await { it.paymentId != null }

        model.switchToCash()
        val state = model.state.await { it.paymentState == PaymentState.FellBackToCash }

        assertEquals(listOf(FakeRideRepository.PAYMENT_ID), rides.cashFallbacks)
        assertTrue(state.confirmed, "cash in the vehicle is a settled ride")
        assertNull(state.error)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        PayFareViewModel(
            rideId = FakeRideRepository.RIDE_ID,
            rides = rides,
            now = { clock },
            // The attestation pair is what is under test, not how long a passenger waits for it.
            pollInterval = POLL,
        ),
    )

    private companion object {
        const val ARTIFACT_ID = "01JART00000000000000000001"

        /** Fast enough that the AL-47 pair completes inside the assertion's own timeout. */
        val POLL = 10.milliseconds
    }
}

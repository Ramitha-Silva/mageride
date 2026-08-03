package lk.mageride.driver.home

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.domain.dispatch.OfferOutcome
import lk.mageride.shared.domain.dispatch.OfferSession
import lk.mageride.shared.domain.dispatch.OfferSessionState
import lk.mageride.shared.domain.dispatch.RideOffer
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.Clock
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.ExperimentalTime

/**
 * SCR-DA-014 — the countdown, the two ways of losing, and what winning produces.
 *
 * The DoD line: *"the offer countdown expires exactly at 15 s and the screen returns to standby"*.
 * The **fifteen** is asserted against the constant and the deadline arithmetic in `OfferInboxTest`;
 * what is asserted here is the behaviour at zero, which is what a driver actually experiences —
 * driven on a deliberately short window so the test does not spend a real fifteen seconds proving
 * a `Duration` comparison.
 */
@OptIn(ExperimentalTime::class)
class OfferViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val offers = OfferSession(api = { backend.mageRideApi().ride })

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_countdown_reaching_zero_drops_the_offer_and_returns_the_driver_to_standby() = runBlocking {
        val model = viewModel()
        offers.onOfferPushed(offer(window = SHORT_WINDOW))

        model.state.await { it.isLive }
        model.state.await { it.outcome == OfferOutcome.Expired }

        assertEquals(OfferSessionState.Idle, offers.state.value, "the slot is free for the next round")
        assertTrue(
            !backend.called("declineRideOffer"),
            "an expired offer is never sent — the server has already released the driver, and a " +
                "decline would be a 410 for nothing",
        )
    }

    @Test
    fun accepting_answers_won_with_the_whole_ride() = runBlocking {
        // R-02's atomic accept. `AcceptRideOfferResponse` carries the full aggregate so the ride
        // screen needs no second read, and the version came from the enrichment read (R-14).
        val model = viewModel()
        offers.onOfferPushed(offer())
        model.state.await { it.isLive }

        model.accept()
        val outcome = model.state.await { it.outcome != null }.outcome

        assertIs<OfferOutcome.Won>(outcome)
        assertTrue(backend.called("acceptRideOffer"))
    }

    @Test
    fun another_driver_winning_is_reported_as_taken_and_never_as_expired() = runBlocking {
        // The two are kept apart all the way from `409 offer-already-accepted` / `410
        // offer-expired`: one says somebody was faster, the other says nobody was, and a driver
        // app that showed "too slow" for a ride nobody took would be lying about their own
        // acceptance rate.
        backend.fails("acceptRideOffer", HttpStatusCode.Conflict, "offer-already-accepted")

        val model = viewModel()
        offers.onOfferPushed(offer())
        model.state.await { it.isLive }

        model.accept()
        assertEquals(OfferOutcome.Taken, model.state.await { it.outcome != null }.outcome)
    }

    @Test
    fun a_wallet_short_of_the_daily_fee_is_its_own_ending() = runBlocking {
        // `402 insufficient-wallet` is D-08's gate, not a dispatch failure — the offer is lost, but
        // the reason is a balance the driver can top up (US-9.1).
        backend.fails("acceptRideOffer", HttpStatusCode.PaymentRequired, "insufficient-wallet")

        val model = viewModel()
        offers.onOfferPushed(offer())
        model.state.await { it.isLive }

        model.accept()
        assertEquals(OfferOutcome.WalletBlocked, model.state.await { it.outcome != null }.outcome)
    }

    @Test
    fun rejecting_declines_and_frees_the_slot() = runBlocking {
        val model = viewModel()
        offers.onOfferPushed(offer())
        model.state.await { it.isLive }

        model.reject()
        assertEquals(OfferOutcome.Declined, model.state.await { it.outcome != null }.outcome)
        assertEquals(OfferSessionState.Idle, offers.state.value)
        assertTrue(backend.called("declineRideOffer"))
    }

    private fun offer(window: kotlin.time.Duration = RideOffer.TTL): RideOffer = RideOffer(
        offerId = OFFER_ID,
        rideId = Fixtures.RIDE_ID,
        driverId = Fixtures.DRIVER_ID,
        expiresAt = now() + window,
        fareEstimateMinor = Fixtures.FARE.amountMinor,
    )

    private fun now(): Timestamp = Clock.System.now()

    private fun viewModel(): OfferViewModel = OfferViewModel(
        offers = offers,
        rides = ActiveRideRepository(ride = backend.mageRideApi().ride, fare = backend.mageRideApi().fare),
        tick = TICK,
    )

    private companion object {
        const val OFFER_ID = "01JOFFER00000000000000000"

        /** Long enough for the collector to see a frame, short enough to be a unit test. */
        val SHORT_WINDOW = 300.milliseconds
        val TICK = 50.milliseconds
    }
}

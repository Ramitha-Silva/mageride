package lk.mageride.passenger.ride

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.live.FakeLiveHubTransport
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.shell.AppPreferences
import lk.mageride.shared.data.models.CallType
import lk.mageride.shared.data.models.Language
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.platform.platformH3Grid
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-014/015, and two Definition-of-Done lines: *"cancelling after acceptance surfaces the
 * Rs 50 outstanding-balance consequence"* and *"no masking language remains anywhere in the call
 * UI"*.
 *
 * The second is asserted through [CallChoice] rather than by grepping a layout: AL-48 withdrew the
 * masking requirement, so the app's job is to hand over a **real** number, remember which kind of
 * call the passenger prefers, and disclose number visibility once. A masked path would have to
 * exist somewhere for the copy to describe, and there is nowhere for it to be.
 */
class ActiveRideViewModelTest {

    private val main = MainDispatcher()
    private val rides = FakeRideRepository()
    private val transport = FakeLiveHubTransport()
    private val grid: H3Grid = requireNotNull(platformH3Grid()) { "com.uber:h3 should be on the test classpath" }
    private val backend = FakeApiBackend()
        .always(
            "getNearbyVehicles",
            FakeReply.value(NearbyVehiclesResponse(vehicles = emptyList(), asOf = Fixtures.NOW)),
        )
    private val planeScope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private var clock: Timestamp = Fixtures.NOW

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
        planeScope.cancel()
    }

    @Test
    fun cancelling_before_acceptance_is_free_and_the_screen_says_so() = runBlocking {
        // US-6A.9. Nothing has been accepted, so nothing is owed — and SCR-PA-014 puts the word
        // on the button rather than behind a dialog, because there is nothing to warn about.
        rides.rideAnswer = FakeRideRepository.ride(state = RideState.Matching)
        val model = viewModel()
        val state = model.state.await { it.ride != null }

        assertTrue(state.cancelIsFree)

        model.confirmCancel()
        val cancelled = model.state.await { it.cancelled }

        assertNull(cancelled.penalty, "nothing is owed before acceptance")
    }

    @Test
    fun cancelling_after_acceptance_surfaces_the_rs_50() = runBlocking {
        // US-6A.10 / D-05. The debt settles on the NEXT trip, so nothing appears on a statement
        // today — which is exactly why the consequence has to reach the passenger now.
        rides.rideAnswer = FakeRideRepository.accepted()
        val model = viewModel()
        val state = model.state.await { it.ride != null }

        assertFalse(state.cancelIsFree, "an accepted ride is not free to cancel")

        model.confirmCancel()
        val cancelled = model.state.await { it.cancelled }

        val penalty = assertNotNull(cancelled.penalty, "the Rs 50 is reported back to the screen")
        assertEquals(FakeRideRepository.PENALTY_MINOR, penalty.amountMinor)
        assertEquals(
            lk.mageride.shared.data.models.ride.PenaltySettlement.NEXT_TRIP,
            penalty.settledOn,
            "carried to the next trip, not charged now",
        )
    }

    @Test
    fun the_cancel_carries_the_version_the_screen_last_read() = runBlocking {
        // R-03's optimistic concurrency: a cancel against a stale version is `409 version-conflict`
        // rather than a cancel of something that has since moved on.
        rides.rideAnswer = FakeRideRepository.accepted()
        val model = viewModel()
        val state = model.state.await { it.ride != null }

        model.confirmCancel()
        model.state.await { it.cancelled }

        assertEquals(state.ride?.version, rides.cancels.single().second)
    }

    @Test
    fun the_drivers_real_number_appears_only_after_acceptance() = runBlocking {
        // AL-48. `counterpartyPhone` is absent before assignment and on rides cancelled before it,
        // which is what makes the direct-dial row unavailable rather than dialling nothing.
        rides.rideAnswer = FakeRideRepository.ride(state = RideState.Matching)
        val searching = viewModel()
        assertNull(searching.state.await { it.ride != null }.driverPhone)

        rides.rideAnswer = FakeRideRepository.accepted()
        val riding = viewModel()
        assertEquals("+94771234567", riding.state.await { it.driverPhone != null }.driverPhone)
    }

    @Test
    fun the_call_chooser_remembers_the_last_kind_and_discloses_the_number_once() {
        // AL-48 replaced masking with transparency (US-26.5). The notice is owed before a DIRECT
        // dial and never before a VoIP call — warning about number visibility before a call that
        // involves no number would be a warning about something that is not happening.
        val preferences = InMemoryPreferences()
        val choice = CallChoice(preferences)

        assertNull(choice.remembered)
        assertTrue(choice.owesNumberNotice(CallType.DIRECT_DIAL))
        assertFalse(choice.owesNumberNotice(CallType.FREE_VOIP), "a VoIP call reveals no number")

        choice.remember(CallType.DIRECT_DIAL)

        assertEquals(CallType.DIRECT_DIAL, choice.remembered)
        assertFalse(choice.owesNumberNotice(CallType.DIRECT_DIAL), "shown once, not on every call")

        // And the memory outlives the ride, which is the point of holding it in preferences.
        assertEquals(CallType.DIRECT_DIAL, CallChoice(preferences).remembered)
    }

    @Test
    fun a_free_call_never_marks_the_number_notice_as_seen() {
        // Otherwise a passenger whose first call was VoIP would never be told their number is
        // visible — and the first time it mattered would be the first time they dialled directly.
        val preferences = InMemoryPreferences()
        val choice = CallChoice(preferences)

        choice.remember(CallType.FREE_VOIP)

        assertFalse(preferences.callNumberNoticeShown)
        assertTrue(choice.owesNumberNotice(CallType.DIRECT_DIAL))
    }

    // `sos_reports_a_position_and_says_so` lived here until C084. SCR-PA-015's `⛨ SOS` NAVIGATES
    // now — the alarm, its countdown and its dispatched state are SCR-PA-029's, and one caller of
    // `POST /v1/sos` is what keeps one emergency to one row on the operator's live feed. The
    // assertion moved to `safety/SosViewModelTest`, which is where the behaviour is.

    /**
     * **A completed ride hands the passenger to SCR-PA-016** (Δ C098).
     *
     * `ActiveRideScreen`'s own KDoc said the ride-state change carried them there and **nothing
     * implemented it** — no screen, no `NavHost` arm and no push built that destination — so
     * `Completed` left a passenger on a finished trip and the whole of D-10 was reachable only from
     * the receipt it comes *after*. Found while building the iOS twin.
     */
    @Test
    fun a_completed_ride_hands_over_to_payment() = runBlocking {
        rides.rideAnswer = FakeRideRepository.ride(state = RideState.Completed)
        val model = viewModel()

        val state = model.state.await { it.handOff != null }
        assertEquals(RideHandOff.Payment, state.handOff)
    }

    /**
     * A ride the driver settled in cash while the app was away has nothing to pay, so the receipt is
     * where it belongs; a cancelled one has neither and goes back to the map.
     */
    @Test
    fun a_settled_ride_goes_to_the_receipt_and_a_cancelled_one_to_the_map() = runBlocking {
        rides.rideAnswer = FakeRideRepository.ride(state = RideState.CashSettled)
        assertEquals(RideHandOff.Receipt, viewModel().state.await { it.handOff != null }.handOff)

        rides.rideAnswer = FakeRideRepository.ride(state = RideState.CancelledByDriver)
        assertEquals(RideHandOff.Finished, viewModel().state.await { it.handOff != null }.handOff)
    }

    /**
     * **`ExpiredNoDriver` is deliberately not a hand-off.** SCR-PA-014 draws its own *"No drivers
     * available"* plus a retry over the same screen (US-6A.11), and navigating away would take the
     * retry with it.
     */
    @Test
    fun an_expired_search_stays_on_the_screen_and_offers_a_retry() = runBlocking {
        rides.rideAnswer = FakeRideRepository.ride(state = RideState.ExpiredNoDriver)
        val state = viewModel().state.await { it.noDriver }

        assertNull(state.handOff, "the retry lives on SCR-PA-014")
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        ActiveRideViewModel(
            rideId = FakeRideRepository.RIDE_ID,
            rides = rides,
            live = PassengerLiveMap(
                transport = transport,
                query = backend.mageRideApi().query,
                grid = grid,
                scope = planeScope,
            ),
            now = { clock },
        ),
    )

    /** `AppPreferences` in memory — the production one is `SharedPreferences`, which has no stub. */
    private class InMemoryPreferences(
        override var language: Language? = null,
        override var languagePendingSync: Boolean = false,
        override var locationRationaleAcknowledged: Boolean = false,
        override var lastCallType: String? = null,
        override var callNumberNoticeShown: Boolean = false,
        override var defaultPaymentMethod: String? = null,
    ) : AppPreferences {

        /** Written by the recents language sweep, never seeded by a test. */
        override var recentsLanguage: Language? = null
    }
}

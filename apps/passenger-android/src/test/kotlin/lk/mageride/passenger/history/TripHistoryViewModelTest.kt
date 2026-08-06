package lk.mageride.passenger.history

import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.nav.PassengerRoute
import lk.mageride.passenger.push.PushRouter
import lk.mageride.shared.data.models.AppSurface
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryDriver
import lk.mageride.shared.data.models.ride.RideHistoryRow
import lk.mageride.shared.domain.auth.AuthConfig
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.AuthSessionStore
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.InMemorySecureStore
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-022, and two Definition-of-Done lines: *"a cancelled-before-assignment trip shows no
 * driver number"* and *"the FCM deep link opens the recipient screen directly on the correct
 * package"*.
 *
 * The first is AL-48's own rule seen from the history list: a number is *withheld* for rides
 * cancelled before assignment, because there was never a driver to reach. The second is asserted
 * on `PushRouter`, which is where a `mageride://…` URI becomes a destination.
 */
class TripHistoryViewModelTest {

    private val main = MainDispatcher()
    private val history = FakeHistory()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_completed_trip_offers_a_call_and_a_cancelled_one_does_not() = runBlocking {
        // US-24.4 / AL-48. A completed trip's driver is reachable — for a lost item, most often —
        // and a ride nobody ever accepted has nobody to call.
        val completed = row(state = RideState.CashSettled, driver = driver())
        val cancelledEarly = row(state = RideState.CancelledByRiderBeforeAccept, driver = null)
        val expired = row(state = RideState.ExpiredNoDriver, driver = null)

        assertTrue(completed.hasReachableDriver())
        assertFalse(cancelledEarly.hasReachableDriver(), "there was never a driver to call")
        assertFalse(expired.hasReachableDriver(), "dispatch gave up before anyone accepted")
    }

    @Test
    fun a_cancelled_before_assignment_row_never_reaches_the_call_chooser() = runBlocking {
        // Belt and braces, deliberately: the card hides the action, and the view model refuses it
        // as well, so a stale list or a mis-wired callback cannot get around the rule.
        history.rows = listOf(row(state = RideState.CancelledByRiderBeforeAccept, driver = null))
        val model = viewModel()
        val state = model.state.await { !it.loading }

        model.call(state.visibleRides.single())

        assertNull(model.state.value.callFor)
        assertTrue(history.ridesRead.isEmpty(), "no read was even attempted")
    }

    @Test
    fun calling_a_completed_trip_resolves_the_real_number() = runBlocking {
        // The card renders `mobileMasked` because that is what the list carries, and `PhoneMasked`
        // must never be parsed back into a dialable number. AL-48 put the clear number on
        // `RideDetail.counterpartyPhone`, so the Call costs one read — see the C081 handoff.
        history.rows = listOf(row(state = RideState.CashSettled, driver = driver()))
        history.rideAnswer = ride(phone = "+94771234567")
        val model = viewModel()
        val state = model.state.await { !it.loading }

        model.call(state.visibleRides.single())
        val target = model.state.await { it.callFor != null }.callFor

        assertEquals("+94771234567", target?.phone)
        assertEquals(listOf(RIDE_ID), history.ridesRead)
    }

    @Test
    fun a_ride_whose_number_the_server_withheld_offers_nothing_to_dial() = runBlocking {
        // The same rule from the server's side: `counterpartyPhone` is absent on rides cancelled
        // pre-assignment. A row that slipped past the local check still cannot produce a dial.
        history.rows = listOf(row(state = RideState.CashSettled, driver = driver()))
        history.rideAnswer = ride(phone = null)
        val model = viewModel()
        val state = model.state.await { !it.loading }

        model.call(state.visibleRides.single())

        assertNull(model.state.value.callFor)
    }

    @Test
    fun the_packages_tab_and_the_past_tab_do_not_show_each_other() = runBlocking {
        history.rows = listOf(
            row(state = RideState.CashSettled, driver = driver()),
            row(state = RideState.CashOnDeliveryCollected, driver = driver(), id = PACKAGE_ID),
        )
        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(listOf(RIDE_ID), model.state.value.rides.map { it.rideId })

        model.select(HistoryTab.PACKAGES)
        assertEquals(listOf(PACKAGE_ID), model.state.value.visibleRides.map { it.rideId })
    }

    @Test
    fun an_empty_tab_is_told_apart_from_a_loading_one() {
        // The wireframe draws an illustration for empty and a shimmer for loading, and showing
        // "no trips yet" while the first page is still in flight is the one wrong answer.
        //
        // Asserted on the state rather than by racing the load: `Dispatchers.Unconfined` runs the
        // init read eagerly, so a view model built here is already loaded before the first line of
        // the test — which would make a timing assertion pass for the wrong reason.
        assertFalse(TripHistoryState(loading = true).empty, "a list still in flight is not empty")
        assertTrue(TripHistoryState(loading = false).empty)

        // And a tab with rows is never empty, whichever tab it is.
        val withRows = TripHistoryState(loading = false, rides = listOf(row(RideState.CashSettled, driver())))
        assertFalse(withRows.empty)
        assertTrue(withRows.copy(tab = HistoryTab.PACKAGES).empty, "the packages tab has none")
    }

    @Test
    fun the_package_deep_link_resolves_to_the_tracking_screen_for_that_ride() {
        // The DoD line. `mageride://package/{rideId}` is the same URI for both parties — the
        // recipient gets it on `package_picked_up`, the sender on `package_delivered` — and which
        // screen to draw is a fact about the ride, decided by `PackageTrackViewModel`.
        val route = PushRouter.resolve("mageride://package/$RIDE_ID")

        assertEquals(PassengerRoute.PackageTracking(RIDE_ID), route)
        assertEquals("package/$RIDE_ID", route?.path)

        // And a link with no ride goes nowhere rather than opening an empty tracker.
        assertNull(PushRouter.resolve("mageride://package"))
        assertNull(PushRouter.resolve("mageride://package/"))
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(TripHistoryViewModel(history, sessions()))

    /**
     * A signed-out session manager.
     *
     * Deliberately signed out: nothing this class asserts needs a user id, and the two reads that
     * would (the Scheduled tab, SCR-PA-023) are covered elsewhere. A manager that had to be
     * driven through a whole OTP flow to test a history list would be testing C077 again.
     */
    private fun sessions(): AuthSessionManager {
        val config = AuthConfig(app = AppSurface.PASSENGER)
        val backend = FakeApiBackend()
        return AuthSessionManager(
            api = { backend.mageRideApi().iam },
            store = AuthSessionStore(InMemorySecureStore(), config),
            config = config,
        )
    }

    private fun driver() = RideHistoryDriver(
        driverId = DRIVER_ID,
        name = "K. Fernando",
        // The masked form is what the list carries. It is never dialled.
        mobileMasked = "+9477*****67",
    )

    private fun row(state: RideState, driver: RideHistoryDriver?, id: Ulid = RIDE_ID) = RideHistoryRow(
        rideId = id,
        state = state,
        pickup = Place(lat = 6.9344, lng = 79.8428, address = "Nugegoda"),
        dropoff = Place(lat = 6.8649, lng = 79.8997, address = "Galle Face"),
        completedAt = Fixtures.NOW,
        driver = driver,
    )

    private fun ride(phone: String?) = RideDetail(
        rideId = RIDE_ID,
        kind = lk.mageride.shared.data.models.ride.RideKind.PASSENGER,
        state = RideState.CashSettled,
        version = 4,
        pickup = Place(lat = 6.9344, lng = 79.8428),
        dropoff = Place(lat = 6.8649, lng = 79.8997),
        vehicleType = lk.mageride.shared.data.models.RideVehicleType.SEDAN,
        paymentMethod = lk.mageride.shared.data.models.ride.RidePaymentMethod.CASH,
        counterpartyPhone = phone,
        createdAt = Fixtures.NOW,
    )

    /** The history seam, in memory. */
    private class FakeHistory : HistoryRepository {
        var rows: List<RideHistoryRow> = emptyList()
        var rideAnswer: RideDetail? = null
        val ridesRead = mutableListOf<Ulid>()

        override suspend fun rides(page: PageRequest): Page<RideHistoryRow> = Page.of(rows)

        override suspend fun trip(userId: Ulid, tripId: Ulid): TripDetail = error("not used here")

        override suspend fun ride(rideId: Ulid): RideDetail {
            ridesRead += rideId
            return rideAnswer ?: error("no ride configured")
        }

        override suspend fun scheduled(userId: Ulid): List<ScheduledRideRow> = emptyList()
    }

    private companion object {
        const val RIDE_ID = "01JRIDE0000000000000000001"
        const val PACKAGE_ID = "01JRIDE0000000000000000002"
        const val DRIVER_ID = "01JDRV00000000000000000001"
    }
}

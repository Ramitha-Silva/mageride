package lk.mageride.driver.home

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.ride.ActiveRideRepository
import lk.mageride.driver.vehicle.FakeActiveVehicleStore
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.data.models.dispatch.PresenceResponse
import lk.mageride.shared.data.models.dispatch.PresenceState
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.trip.Session
import lk.mageride.shared.data.models.trip.SessionState
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-010 / SCR-DA-011 — the go-online gate, the mode-aware home, and what going offline costs.
 *
 * The DoD lines these cover: *"go-online toggle with gating"*, *"selecting a Mode A/B vehicle
 * routes Home to SCR-DA-011 instead of SCR-DA-010"*, and DT-04's *"going offline clears
 * Directional"*.
 */
class HomeViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val location = FakeDriverLocationSource()
    private val publisher = FakePositionPublisher()
    private val activeVehicle = FakeActiveVehicleStore()
    private val journeyPreferences = FakeJourneyPreferences()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun with_no_eligible_vehicle_the_go_online_toggle_is_dead_and_the_empty_state_shows() = runBlocking {
        // US-9.6 — "the toggle is disabled until at least one vehicle is available". A vehicle
        // part-way through the wizard is not one: AL-30 makes onboarding_status the gate.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle(approved = false))))

        val model = viewModel()
        model.state.await { !it.loading }

        assertFalse(model.state.value.canGoOnline)
        assertTrue(model.state.value.needsVehicle, "the wireframe routes this to SCR-DA-026a")

        model.toggleOnline(desired = true)

        assertEquals(emptyList(), publisher.calls, "nothing may publish for a vehicle that cannot dispatch")
        assertFalse(backend.called("goOnline"))
        assertFalse(model.state.value.online)
    }

    @Test
    fun going_online_needs_a_position_and_starts_publishing_only_after_dispatch_accepts() = runBlocking {
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle())))
        backend.returns("goOnline", PresenceResponse(PresenceState.AVAILABLE))

        val model = viewModel()
        model.state.await { !it.loading }

        // `GoOnlineRequest` carries a position in its body, and there is not one yet. Refusing is
        // the honest answer — sending a made-up point would put the driver on the map in the wrong
        // place and dispatch would score them from it.
        model.toggleOnline(desired = true)
        assertFalse(backend.called("goOnline"), "no fix, no presence")

        location.emit(fix())
        model.state.await { it.position != null }

        model.toggleOnline(desired = true)
        model.state.await { it.online }

        assertTrue(backend.called("goOnline"))
        assertEquals(
            listOf("start:$HOME_VEHICLE_ID"),
            publisher.calls,
            "the publisher starts AFTER the call is accepted — a driver in the pool who is not " +
                "publishing is offered rides that cannot find them",
        )
    }

    @Test
    fun going_offline_stops_publishing_first_and_clears_the_directional_filter() = runBlocking {
        // DT-04. The activation is spent either way (US-6A.19) — `usesRemaining` is carried
        // through unchanged, which is the anti-gaming rule made visible on the chip.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle())))
        backend.returns("goOnline", PresenceResponse(PresenceState.AVAILABLE))
        backend.returns("goOffline", PresenceResponse(PresenceState.OFFLINE))
        backend.returns(
            "getDirectionalFilter",
            DirectionalFilterState(active = true, timeRemainingSec = 1_200, usesRemaining = 1),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        location.emit(fix())
        model.state.await { it.position != null }

        model.toggleOnline(desired = true)
        model.state.await { it.online }
        assertTrue(model.state.value.standing.directional?.active == true)

        model.toggleOnline(desired = false)
        model.state.await { !it.online }

        assertEquals(
            listOf("start:$HOME_VEHICLE_ID", "stop"),
            publisher.calls,
            "publishing stops before the driver leaves the pool, never after",
        )
        assertFalse(model.state.value.standing.directional?.active == true, "DT-04")
        assertEquals(1, model.state.value.standing.directional?.usesRemaining, "US-6A.19 — no refund")
    }

    @Test
    fun a_mode_a_vehicle_makes_home_the_start_end_journey_dashboard() = runBlocking {
        // D2' §SCR-DA-011: this screen IS home whenever the active vehicle is Mode A or Mode B.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle(mode = ServiceMode.A))))
        backend.returns("getActiveSession", session(SessionState.ACTIVE))

        val model = viewModel()
        model.state.await { !it.loading }

        assertTrue(model.state.value.isScheduledMode, "SCR-DA-011, not SCR-DA-010")
        assertTrue(model.state.value.journey.isRunning)
        assertTrue(
            model.state.value.journey.startedByDevice,
            "AL-32 — a live session this handset never opened is the GPS tracker's",
        )
    }

    @Test
    fun starting_a_journey_by_hand_is_not_the_trackers_and_the_dashboard_records_that() = runBlocking {
        // AL-32's other direction: the dashboard can override the device, and a session the driver
        // started must not later be reported as an ignition auto-start.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle(mode = ServiceMode.B))))
        backend.returns<Session?>("getActiveSession", null)
        backend.returns("startSession", session(SessionState.ACTIVE))

        val model = viewModel()
        model.state.await { !it.loading }
        assertFalse(model.state.value.journey.isRunning)

        model.startJourney()
        model.state.await { it.journey.isRunning }

        assertFalse(model.state.value.journey.startedByDevice)
        assertEquals(Fixtures.TRIP_ID, journeyPreferences.startedSessionId)
        assertEquals(listOf("start:$HOME_VEHICLE_ID"), publisher.calls)
    }

    @Test
    fun a_mode_c_vehicle_never_asks_trip_state_svc_about_a_session() = runBlocking {
        // R-01's fence, from the client side. Mode C rides are ride-svc's; a Mode C vehicle has no
        // tracking session, and asking for one would cross the boundary for nothing.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle())))

        val model = viewModel()
        model.state.await { !it.loading }

        assertFalse(model.state.value.isScheduledMode)
        assertFalse(backend.called("getActiveSession"))
    }

    private fun session(state: SessionState) = Session(
        sessionId = Fixtures.TRIP_ID,
        vehicleId = HOME_VEHICLE_ID,
        driverId = Fixtures.DRIVER_ID,
        mode = ServiceMode.A,
        state = state,
        startedAt = Fixtures.NOW,
    )

    private suspend fun viewModel(): HomeViewModel {
        // No ride in hand unless a test says so — otherwise the fake synthesises one from the
        // contract and Home would report a trip to resume in every case.
        backend.returns<RideDetail?>("getActiveDriverRide", null)
        val api = backend.mageRideApi()
        return HomeViewModel(
            identity = DriverIdentity(
                registry = api.registry,
                sessions = signedInSessions(backend),
                activeVehicle = activeVehicle,
            ),
            standby = StandbyRepository(
                dispatch = api.dispatch,
                wallet = api.wallet,
                subscription = api.subscription,
                query = api.query,
            ),
            journeys = JourneyRepository(
                tripState = api.tripState,
                transit = api.transit,
                preferences = journeyPreferences,
            ),
            rides = ActiveRideRepository(ride = api.ride, fare = api.fare),
            location = location,
            publisher = publisher,
        )
    }
}

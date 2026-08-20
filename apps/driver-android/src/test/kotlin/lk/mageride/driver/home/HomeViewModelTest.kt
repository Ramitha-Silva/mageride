package lk.mageride.driver.home

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.R
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

    @Test
    fun a_forbidden_vehicle_read_says_so_and_does_not_claim_the_driver_has_no_vehicle() = runBlocking {
        // The shape a first run takes on an account that never got the `driver` role: every
        // `/v1/vehicles` route is `RequireMageRideRole(driver)` and the only grant in the backend
        // is at account creation, so `GET /v1/vehicles/mine` answers `403 forbidden`.
        //
        // Two things used to go wrong at once. The copy was `error_generic` — "Something went
        // wrong", which is true of everything — and the empty `LiveVehicle` left behind by the
        // throw also read as *"add a vehicle to go online"*, so the dashboard showed an error and
        // a contradiction of it in the same column above the map.
        backend.fails("listMyVehicles", HttpStatusCode.Forbidden, "forbidden")

        val model = viewModel()
        model.state.await { !it.loading }
        val state = model.state.value

        assertEquals(R.string.error_forbidden, state.error)
        assertFalse(state.vehiclesKnown, "the read never answered, so nothing is known about vehicles")
        assertFalse(state.needsVehicle, "a dead read is not a driver without a vehicle")
        assertFalse(state.canGoOnline, "and the toggle stays shut either way")
    }

    @Test
    fun a_dead_ride_read_leaves_the_rest_of_the_dashboard_standing() = runBlocking {
        // `StandbyRepository.standing` already says a dead read must not blank the dashboard and
        // holds its five to it; the vehicle and the ride reads were outside that rule and threw
        // the whole screen away instead. The vehicle answered here, so the chip, the toggle and
        // the sheet are all still owed to the driver.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle())))
        backend.fails("getActiveDriverRide", HttpStatusCode.InternalServerError, "internal-error")

        val model = viewModel(stubActiveRide = false)
        model.state.await { !it.loading }
        val state = model.state.value

        assertEquals(HOME_VEHICLE_ID, state.vehicles.live?.vehicleId, "the vehicle read succeeded")
        assertTrue(state.vehiclesKnown)
        assertTrue(state.canGoOnline, "US-9.6's gate is about the vehicle, not about ride-svc")
        assertEquals(R.string.error_service_down, state.error, "and the driver is told which kind of failure it was")
    }

    @Test
    fun one_refresh_reads_each_endpoint_once() = runBlocking {
        // The reads used to sit inside `MutableStateFlow.update`, which re-runs its lambda every
        // time the compare-and-set loses — and `observeDevice` writes `tickAt` once a second and
        // `position` on every fix, so it loses whenever the round trips outlast a tick. This is
        // the invariant that broke: one refresh, one read each.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(liveVehicle())))

        val model = viewModel()
        model.state.await { !it.loading }

        listOf("listMyVehicles", "getActiveDriverRide", "getWallet", "getDirectionalFilter").forEach { operation ->
            assertEquals(1, backend.calls.count { it.operationId == operation }, "$operation was read more than once")
        }
    }

    private fun session(state: SessionState) = Session(
        sessionId = Fixtures.TRIP_ID,
        vehicleId = HOME_VEHICLE_ID,
        driverId = Fixtures.DRIVER_ID,
        mode = ServiceMode.A,
        state = state,
        startedAt = Fixtures.NOW,
    )

    private suspend fun viewModel(stubActiveRide: Boolean = true): HomeViewModel {
        // No ride in hand unless a test says so — otherwise the fake synthesises one from the
        // contract and Home would report a trip to resume in every case. A test that stubs the
        // read itself passes `false`, because `returns` would replace what it programmed.
        if (stubActiveRide) backend.returns<RideDetail?>("getActiveDriverRide", null)
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

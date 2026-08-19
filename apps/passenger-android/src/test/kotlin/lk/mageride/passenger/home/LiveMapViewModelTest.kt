package lk.mageride.passenger.home

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.passenger.live.FakeLiveHubTransport
import lk.mageride.passenger.live.LiveStatus
import lk.mageride.passenger.live.PassengerLiveMap
import lk.mageride.passenger.location.PassengerFix
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.passenger.map.MapVehicle
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.iam.SavedAddressListResponse
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import lk.mageride.shared.data.models.query.NearbyVehicle
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.domain.geo.GeoCells
import lk.mageride.shared.domain.geo.H3Grid
import lk.mageride.shared.platform.platformH3Grid
import lk.mageride.shared.realtime.LiveHub
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-PA-010 and the two sheets over it, with no map and no server.
 *
 * The socket, the nineteen cells and the hysteresis are C076's and are asserted in
 * `PassengerLiveMapTest`. What this class owns is the layer above: **what a passenger sees and what
 * a tap does** — the client-side filter, AL-23's routing by mode, US-7.16's engaged vehicle leaving
 * the map, and US-7.14's reason for an empty one.
 *
 * The live plane here is the **real** `PassengerLiveMap` over `FakeLiveHubTransport`, not a stub of
 * it. Every rule under test is about the boundary between the two, and a stubbed plane would let
 * this file assert a shape the production one does not have.
 */
class LiveMapViewModelTest {

    private val main = MainDispatcher()
    private val grid: H3Grid = requireNotNull(platformH3Grid()) {
        "no H3 grid on this host — com.uber:h3 should be on the unit-test runtime classpath"
    }

    private val transport = FakeLiveHubTransport()
    private val locations = FakeLocationSource()
    private val recents = FakeRecentPlaces(mutableListOf(NUGEGODA))
    private val backend = FakeApiBackend()
        .always("getNearbyVehicles", FakeReply.value(NearbyVehiclesResponse(vehicles = emptyList(), asOf = NOW)))
        .always("listSavedAddresses", FakeReply.value(SavedAddressListResponse(items = listOf(HOME, WORK))))

    /**
     * The plane's own scope.
     *
     * These tests are `runBlocking` over `Dispatchers.Unconfined` rather than `runTest`, for the
     * reason `MainDispatcher` gives: a call through [FakeApiBackend] is a real Ktor client over
     * MockEngine, which resolves on its own dispatcher and cannot be advanced past by a virtual
     * clock. So the plane gets a real scope and the assertions wait for state.
     */
    private val planeScope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)

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
    fun the_passengers_fix_is_what_joins_the_nineteen_cells() = runBlocking {
        // The screen owns the R-06 subscription's *input* and nothing else about it. Until a fix
        // arrives the plane is connected to a socket and subscribed to nothing at all, which draws
        // an empty map that no amount of waiting fixes.
        val live = connectedPlane()
        val model = viewModel(live)

        locations.emit(PassengerFix(lat = COLOMBO_LAT, lng = COLOMBO_LNG, accuracyMetres = 12.0))
        val state = model.state.await { it.fix != null }

        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, live.cells.value.size)
        assertEquals(12.0, state.fix?.accuracyMetres, "MAP-02's circle radius comes from the fix")
    }

    @Test
    fun a_batch_of_frames_is_drawn_through_the_filter() = runBlocking {
        val live = connectedPlane()
        val model = viewModel(live)

        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        val state = model.state.await { it.vehicles.size == 3 }

        assertEquals(setOf(BUS, VAN, TUK), state.vehicles.map { it.vehicleId }.toSet())
        assertEquals(EmptyReason.NONE, state.emptyReason)
    }

    @Test
    fun toggling_a_mode_redraws_from_what_is_already_in_hand() = runBlocking {
        // SCR-PA-006's own state line calls the filter "instant client-side". This asserts the
        // "client-side" half literally: the frames are already held, so switching a mode off
        // changes the map without a single further call to query-svc. A re-query here would put a
        // network round trip — and an offline failure — behind a switch.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }
        // The shortcut read is a background call of its own (`loadShortcuts`), so the snapshot has
        // to wait for it — otherwise it lands *between* the two counts on a loaded host and this
        // test fails for a call the toggle did not make. Δ C083, which is what makes those chips.
        model.state.await { it.shortcuts.isNotEmpty() }
        val callsBefore = backend.calls.size

        model.setMode(ServiceMode.C, enabled = false)

        assertEquals(setOf(BUS, VAN), model.state.value.vehicles.map { it.vehicleId }.toSet())
        assertEquals(callsBefore, backend.calls.size, "a filter toggle is not a query")
        assertEquals(3, model.state.value.lastFrames.size, "the unfiltered set is kept, so it can come back")

        // And back on, from the same held frames.
        model.setMode(ServiceMode.C, enabled = true)
        assertEquals(3, model.state.value.vehicles.size)
    }

    @Test
    fun a_filter_hidden_vehicle_comes_back_when_the_next_batch_lands() = runBlocking {
        // The filter is re-applied to every batch rather than to the first one. A screen that
        // filtered once and then appended would show a type the passenger had switched off the
        // moment that vehicle next moved.
        val live = connectedPlane()
        val model = viewModel(live)
        model.setType(VehicleType.THREE_WHEELER, enabled = false)

        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        val state = model.state.await { it.lastFrames.size == 3 }

        assertEquals(setOf(BUS, VAN), state.vehicles.map { it.vehicleId }.toSet())
    }

    @Test
    fun tapping_a_mode_a_marker_opens_the_popup() = runBlocking {
        // US-7.4 / SCR-PA-007 — a bus is public transport and its details are public.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }

        val tap = model.onMarkerTapped(BUS)

        assertIs<MarkerTap.ShowPopup>(tap)
        assertEquals(BUS, model.state.value.selected?.vehicleId, "the sheet opens from state, not from the tap")

        model.dismissPopup()
        assertNull(model.state.value.selected)
    }

    @Test
    fun tapping_a_mode_b_marker_asks_for_access_with_the_vehicle_pre_filled() = runBlocking {
        // AL-23 / US-4.6. The fence: a private vehicle never opens SCR-PA-007. The question a tap
        // asks is "may I subscribe to this?", and SCR-PA-024 is where it is asked — with the id
        // already filled in, because the passenger has no other way to name a vehicle they can see
        // but do not own.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }

        val tap = model.onMarkerTapped(VAN)

        assertEquals(MarkerTap.RequestModeBAccess(VAN), tap)
        assertNull(model.state.value.selected, "no popup was opened over the map")
    }

    @Test
    fun tapping_a_standby_on_demand_marker_does_nothing() = runBlocking {
        // US-7.4, verbatim: "Standby on-demand vehicles do not show info when tapped." An idle tuk
        // is booked through SCR-PA-009, not inspected — and its driver's name is not the
        // passenger's to see until the ride is accepted (US-7.12).
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }

        assertEquals(MarkerTap.Ignored, model.onMarkerTapped(TUK))
        assertEquals(MarkerTap.Ignored, model.onMarkerTapped(GONE), "and neither does a marker that has left")
        assertNull(model.state.value.selected)
    }

    @Test
    fun an_engaged_on_demand_vehicle_disappears_during_its_hire() = runBlocking {
        // US-7.16 / D-22 — a Mode C vehicle that accepts a ride leaves every public geocell group
        // and lives in `ride:{rideId}` until it is free. Leaving it drawn is how a passenger ends
        // up walking towards a taxi that already has a fare.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }

        transport.emit(LiveHub.Event.VEHICLE_REMOVED, """{"vehicleId":"$TUK","reason":"engaged"}""")
        val state = model.state.await { it.vehicles.size == 2 }

        assertEquals(setOf(BUS, VAN), state.vehicles.map { it.vehicleId }.toSet())
    }

    @Test
    fun a_vehicle_that_leaves_the_map_closes_the_popup_over_it() = runBlocking {
        // The corner the popup makes possible: SCR-PA-007 is open on a bus, and the bus goes
        // stale. A sheet left up would keep showing a distance to a vehicle the platform has
        // stopped tracking — which is worse than no sheet, because it looks live.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }
        model.onMarkerTapped(BUS)
        assertEquals(BUS, model.state.value.selected?.vehicleId)

        transport.emit(LiveHub.Event.VEHICLE_REMOVED, """{"vehicleId":"$BUS","reason":"stale"}""")
        val state = model.state.await { it.selected == null }

        assertEquals(setOf(VAN, TUK), state.vehicles.map { it.vehicleId }.toSet(), "and the bus is off the map")
    }

    @Test
    fun an_empty_map_says_which_kind_of_empty_it_is() = runBlocking {
        // US-7.14 — "an in-app message when no vehicles of my selected type are active in my area,
        // instead of seeing an empty map with no context". The context is this distinction: an
        // outage, a filter the passenger set, or a genuinely quiet area each ask for a different
        // response, and only the middle one is theirs to undo.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }

        model.setMode(ServiceMode.A, enabled = false)
        model.setMode(ServiceMode.B, enabled = false)
        model.setMode(ServiceMode.C, enabled = false)
        assertEquals(EmptyReason.FILTERED_OUT, model.state.value.emptyReason)

        model.setMode(ServiceMode.A, enabled = true)
        assertEquals(setOf(BUS), model.state.value.vehicles.map { it.vehicleId }.toSet())
    }

    @Test
    fun a_map_that_is_not_connected_is_stale_but_is_not_cleared() {
        // SCR-PA-032 / US-15.2. What is drawn is last-known and is marked as such; nothing is
        // erased, because a passenger who has lost signal still wants to know where the bus was.
        // `stale` is what fades the marker layers — see `MageRideMap.dimmed`.
        //
        // Asserted on the state rather than by dropping the socket: the plane's own reconnect is
        // R-09's and lands inside 1.25 s (`PassengerLiveMapTest` pins that), so a test that
        // dropped the connection would be racing the recovery it does not own.
        val drawn = LiveMapState(
            vehicles = listOf(MapVehicle(vehicleId = BUS, lat = COLOMBO_LAT, lng = COLOMBO_LNG)),
            status = LiveStatus.Connecting,
        )

        assertTrue(drawn.stale, "anything but Connected is last-known")
        assertTrue(drawn.vehicles.isNotEmpty(), "last-known positions stay on the map")
        assertEquals(EmptyReason.NONE, drawn.emptyReason, "there is something drawn, so there is no notice")

        // And when there is nothing drawn, the reason is the outage rather than the area — a
        // reconnecting map has no idea whether anything is nearby, and must not claim it does.
        assertEquals(EmptyReason.OFFLINE, drawn.copy(vehicles = emptyList()).emptyReason)
        assertEquals(
            EmptyReason.NOTHING_NEARBY,
            LiveMapState(status = LiveStatus.Connected).emptyReason,
        )
    }

    @Test
    fun the_shortcut_chips_are_the_passengers_saved_addresses() = runBlocking {
        // US-7.13's ★ Home / ★ Work. Best effort — a passenger with none simply has no chips, and
        // that is also what a failed call looks like, which is why the search bar beside them does
        // not depend on this.
        val model = viewModel(connectedPlane())

        val state = model.state.await { it.shortcuts.isNotEmpty() }

        assertEquals(listOf("Home", "Work"), state.shortcuts.map(SavedAddress::label))
    }

    @Test
    fun a_saved_address_gets_its_chip_on_the_next_resume_and_not_on_the_next_launch() = runBlocking {
        // **The bug this test exists for.** The ★ chips are written on ANOTHER screen (SCR-PA-026)
        // and `GET /v1/me/saved-addresses` has no change feed. This model is scoped to the live
        // map's back-stack entry and SURVIVES the trip there and back, so `init` does not run a
        // second time — `loadShortcuts` had exactly one caller, and an address the passenger had
        // just saved had no chip until the process was restarted. Reported from a handset.
        backend.next(
            "listSavedAddresses",
            FakeReply.value(SavedAddressListResponse(items = listOf(HOME, WORK))),
            FakeReply.value(SavedAddressListResponse(items = listOf(HOME, WORK, GYM))),
        )
        val model = viewModel(connectedPlane())
        val opening = model.state.await { it.shortcuts.isNotEmpty() }
        assertEquals(listOf("Home", "Work"), opening.shortcuts.map(SavedAddress::label))

        // Away to SCR-PA-026, an address saved, and back.
        model.onResumed()

        val resumed = model.state.await { it.shortcuts.size == 3 }
        assertEquals(listOf("Home", "Work", "Gym"), resumed.shortcuts.map(SavedAddress::label))
    }

    @Test
    fun the_popup_fills_its_eta_driver_and_plate_from_the_snapshot() = runBlocking {
        // The three fields the socket cannot carry. `VehicleFrame` is a position — putting a
        // driver's name in it would put a driver's name on every frame of every vehicle, several
        // times a second, across nineteen geocell groups — so SCR-PA-007 asks `GET /v1/nearby` for
        // them once, when a marker is actually tapped.
        backend.always(
            "getNearbyVehicles",
            FakeReply.value(NearbyVehiclesResponse(vehicles = listOf(BUS_DETAIL), asOf = NOW)),
        )
        val live = connectedPlane()
        val model = viewModel(live)
        locations.emit(PassengerFix(lat = COLOMBO_LAT, lng = COLOMBO_LNG))
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 && it.fix != null }

        model.onMarkerTapped(BUS)
        val state = model.state.await { it.detail != null }

        assertEquals(120, state.detail?.etaSeconds)
        assertEquals("K. Perera", state.detail?.driverName)
        assertEquals("NB-4521", state.detail?.registrationNumber)

        // Centred on the PASSENGER, because `NearbyVehicle.etaSeconds` is defined as seconds to
        // the querying passenger — a lookup centred on the bus would answer roughly zero and tell
        // every passenger their bus had already arrived.
        val call = backend.lastCall("getNearbyVehicles")
        assertEquals(COLOMBO_LAT.toString(), call.query["lat"])
        assertEquals(COLOMBO_LNG.toString(), call.query["lng"])
    }

    @Test
    fun the_popup_opens_without_a_fix_and_simply_has_no_eta() = runBlocking {
        // "Seconds to the querying passenger" has no meaning without a passenger position, so the
        // lookup is not made at all rather than made against an invented reference point. The
        // sheet still opens — the vehicle, its type and its mode are all known.
        val live = connectedPlane()
        val model = viewModel(live)
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, THREE_VEHICLES)
        model.state.await { it.vehicles.size == 3 }

        model.onMarkerTapped(BUS)

        assertEquals(BUS, model.state.value.selected?.vehicleId)
        assertNull(model.state.value.detail)
        assertFalse(backend.called("getNearbyVehicles"), "nothing to centre the ETA on")
    }

    @Test
    fun the_recent_rows_are_the_local_table_and_are_re_read_on_resume() = runBlocking {
        // §2.2's `place_recents` is local-only and has no change feed, and the row is written on
        // ANOTHER screen (SCR-PA-008). So coming back to the map re-reads it — otherwise a place
        // the passenger just searched for would be missing from the list of places they searched.
        val model = viewModel(connectedPlane())
        val opening = model.state.await { it.recents.isNotEmpty() }
        assertEquals(listOf(NUGEGODA.displayName), opening.recents.map { it.displayName })

        recents.remember(MAHARAGAMA)
        model.onResumed()
        val resumed = model.state.await { it.recents.size == 2 }

        assertEquals(MAHARAGAMA.displayName, resumed.recents.first().displayName, "newest first")
    }

    @Test
    fun a_held_boundary_crossing_lands_on_this_screens_tick_and_not_on_a_fix() = runBlocking {
        // **The bug this test exists for.** ADD §7.4 step 6 applies the first crossing immediately
        // and then HOLDS the next for thirty seconds, so a passenger standing on a cell edge does
        // not join and leave the same six groups every few seconds. A held crossing is applied by
        // the next call into `GeoCellSubscription` — and on a fix-driven path that is the next fix.
        // A passenger who steps over the edge and then STOPS WALKING produces none, so the crossing
        // never lands and they keep the nineteen cells around where they were until they move.
        //
        // C076's handoff asked C078 for this loop and C078 did not write it; Δ C096 found the same
        // hole from the iOS side. `refreshCells()` had no caller anywhere in this module.
        assertEquals(
            GeoCells.BOUNDARY_HYSTERESIS / 2,
            CELL_TICK,
            "the tick has to land a held crossing inside one window, not at the end of a second",
        )

        val tick = 20.milliseconds
        val clock = MutableClock(Fixtures.NOW)
        val live = connectedPlane(clock)
        val model = viewModel(live, cellTick = tick)

        locations.emit(PassengerFix(lat = COLOMBO_LAT, lng = COLOMBO_LNG))
        model.state.await { it.fix != null }
        awaitTransport("the opening join") { transport.joins().isNotEmpty() }
        val opening = live.cells.value
        transport.clearCalls()

        // Ten seconds later, nine kilometres away: a genuine crossing, inside the window.
        clock.advance(10.seconds)
        locations.emit(PassengerFix(lat = NUGEGODA.lat, lng = NUGEGODA.lng))
        delay(tick * TICKS_TO_OBSERVE)

        assertTrue(transport.calls.isEmpty(), "a crossing inside the window sends nothing")
        assertEquals(opening, live.cells.value, "and membership does not move")

        // Past the window — and with **no further fix at all**, which is the whole point. Only the
        // tick can land it now.
        clock.advance(25.seconds)
        awaitTransport("the held crossing lands") { transport.joins().isNotEmpty() }

        assertTrue(transport.leaves().isNotEmpty(), "a crossing is a delta: it leaves as well as joins")
        assertTrue(live.cells.value != opening, "and the subscription has actually moved")
        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, live.cells.value.size)
    }

    // ------------------------------------------------------------------------------------------

    /**
     * The plane, on a clock a test can wind.
     *
     * Frozen rather than real even where a test does not touch it: the only thing `now` feeds is
     * ADD §7.4 step 6's thirty-second hysteresis, and a frozen reading is what stops a slow host
     * lapsing a window a test did not mean to lapse.
     */
    private suspend fun connectedPlane(clock: MutableClock = MutableClock(Fixtures.NOW)): PassengerLiveMap {
        val live = PassengerLiveMap(
            transport = transport,
            query = backend.mageRideApi().query,
            grid = grid,
            scope = planeScope,
            now = clock::now,
        )
        live.connect()
        return live
    }

    private fun viewModel(live: PassengerLiveMap, cellTick: Duration = CELL_TICK) = main.own(
        LiveMapViewModel(
            live = live,
            locations = locations,
            iam = backend.mageRideApi().iam,
            query = backend.mageRideApi().query,
            recents = recents,
            cellTick = cellTick,
        ),
    )

    /** Waits for [predicate] to hold against the transport's recording. */
    private suspend fun awaitTransport(what: String, predicate: () -> Boolean) {
        withTimeout(5.seconds) {
            while (!predicate()) delay(5.milliseconds)
        }
        assertTrue(predicate(), what)
    }

    /** The wall clock, wound by hand — the hysteresis is a comparison against it. */
    private class MutableClock(private var value: Timestamp) {
        fun now(): Timestamp = value
        fun advance(by: Duration) {
            value += by
        }
    }

    /** §2.2's table, in memory — the real one is SQLCipher through a driver that throws here. */
    private class FakeRecentPlaces(private val rows: MutableList<GeocodedPlace>) : RecentPlaces {
        override suspend fun recent(limit: Int): List<GeocodedPlace> = rows.take(limit)
        override suspend fun remember(place: GeocodedPlace) {
            rows.add(0, place)
        }
    }

    /** Fixes a test hands over by name, rather than a satellite. */
    private class FakeLocationSource : PassengerLocationSource {
        private val flow = MutableSharedFlow<PassengerFix>(replay = 1)
        override val fixes: Flow<PassengerFix> = flow
        suspend fun emit(fix: PassengerFix) = flow.emit(fix)
    }

    private companion object {
        val NOW: Timestamp = Fixtures.NOW

        /**
         * How many tick periods to watch before concluding that a held crossing is *staying* held.
         *
         * More than one, deliberately: a single period would pass if the loop had died after its
         * first iteration, which is one of the two ways this can regress.
         */
        const val TICKS_TO_OBSERVE = 5

        const val COLOMBO_LAT = 6.9344
        const val COLOMBO_LNG = 79.8428

        const val BUS = "01JVEH0000000000000000001"
        const val VAN = "01JVEH0000000000000000002"
        const val TUK = "01JVEH0000000000000000003"

        /** A marker id that is not on the map — a tap on one that has since left. */
        const val GONE = "01JVEH0000000000000000009"

        /** One of each mode, so a filter assertion always has something on both sides of it. */
        val THREE_VEHICLES = """
            [{"vehicleId":"$BUS","lat":6.9344,"lng":79.8428,"heading":90,"type":"bus","mode":"A"},
             {"vehicleId":"$VAN","lat":6.9350,"lng":79.8430,"type":"van","mode":"B"},
             {"vehicleId":"$TUK","lat":6.9360,"lng":79.8440,"type":"three_wheeler","mode":"C"}]
        """.trimIndent()

        val MAHARAGAMA = GeocodedPlace(
            lat = 6.8480,
            lng = 79.9265,
            displayName = "Maharagama Town",
            source = GeocodedPlaceSource.RECENT,
        )

        /** What `GET /v1/nearby` knows about the bus that the socket frame does not. */
        val BUS_DETAIL = NearbyVehicle(
            vehicleId = BUS,
            type = VehicleType.BUS,
            mode = ServiceMode.A,
            lat = 6.9344,
            lng = 79.8428,
            driverName = "K. Perera",
            etaSeconds = 120,
            registrationNumber = "NB-4521",
        )

        val HOME = SavedAddress(
            addressId = "01JADDR000000000000000001",
            label = "Home",
            line1 = "22 Galle Road",
            lat = 6.9271,
            lng = 79.8612,
        )
        val NUGEGODA = GeocodedPlace(
            lat = 6.8649,
            lng = 79.8997,
            displayName = "Nugegoda Junction",
            line1 = "High Level Road",
            source = GeocodedPlaceSource.RECENT,
        )

        val WORK = SavedAddress(
            addressId = "01JADDR000000000000000002",
            label = "Work",
            line1 = "1 Union Place",
            lat = 6.9200,
            lng = 79.8600,
        )

        /** Saved on SCR-PA-026 while the map sat in the back stack — see the resume test. */
        val GYM = SavedAddress(
            addressId = "01JADDR000000000000000003",
            label = "Gym",
            line1 = "14 Duplication Road",
            lat = 6.9000,
            lng = 79.8550,
        )
    }
}

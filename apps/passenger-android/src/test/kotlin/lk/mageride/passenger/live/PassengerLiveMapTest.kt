package lk.mageride.passenger.live

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runTest
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.VehicleType
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
import lk.mageride.shared.util.ReconnectBackoff
import kotlin.random.Random
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * The live plane's rules, on the JVM, with no server.
 *
 * Everything asserted here is one of C076's four Definition-of-Done lines: nineteen cells at open,
 * a re-subscription on a boundary crossing with hysteresis, a killed connection that recovers
 * inside five seconds and back-fills the snapshot, and — the fence — never a per-vehicle
 * subscription.
 *
 * **The H3 grid is the real one.** `platformH3Grid()` is `com.uber:h3` on Android and on this JVM
 * host, and using it is the whole point: R-06's "19" is a property of `gridDisk(cell, 2)` over the
 * actual grid, and a hand-rolled fake that returned nineteen of anything would assert nothing. It
 * is also the grid `position-processor-svc` computes against, which is what makes a cell token
 * here the same string the server publishes under.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class PassengerLiveMapTest {

    private val grid: H3Grid = requireNotNull(platformH3Grid()) {
        "no H3 grid on this host — com.uber:h3 should be on the unit-test runtime classpath"
    }

    /**
     * The scopes handed to the live maps built here, so each test can end them.
     *
     * **Not `TestScope.backgroundScope`**, which is the obvious choice and the wrong one:
     * kotlinx-coroutines deliberately stopped draining background work from `advanceUntilIdle()`,
     * and driving the supervision loop is the whole of what these tests do. A scope of our own on
     * the test scheduler runs under `advanceUntilIdle()` exactly as production code would under a
     * real dispatcher.
     */
    private val scopes = mutableListOf<CoroutineScope>()

    @AfterTest
    fun tearDown() {
        scopes.forEach(CoroutineScope::cancel)
    }

    @Test
    fun the_first_fix_joins_exactly_nineteen_cells() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()

        // R-06: H3 res-7 self + ring(2). The corrected figure — res-8 + ring(1) reaches about a
        // kilometre and is the value still in circulation in older ADD text.
        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, live.cells.value.size, "the R-06 view is 19 cells")
        assertEquals(GeoCells.VIEW_RESOLUTION, live.cells.value.first().resolution, "res-7 groups only")

        val join = transport.joins().single()
        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, join.cells.size)
        assertEquals(live.cells.value.map { it.token }.toSet(), join.cells.toSet())
    }

    @Test
    fun staying_inside_the_same_cell_sends_nothing() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()
        transport.clearCalls()

        // A few metres. A res-7 hexagon is about 1.2 km across, so this is the same cell — and
        // recomputing nineteen groups on every fix would be nineteen backplane operations for a
        // passenger standing still.
        live.onPosition(COLOMBO.copy(lat = COLOMBO.lat + 0.0001))
        advanceUntilIdle()

        assertTrue(transport.calls.isEmpty(), "no membership change means no traffic")
        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, live.cells.value.size)
    }

    @Test
    fun a_boundary_crossing_inside_thirty_seconds_is_held_and_then_applied() = runTest {
        val clock = MutableClock(Fixtures.NOW)
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this, clock)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()
        val opening = live.cells.value
        transport.clearCalls()

        // Far enough to be a different res-7 cell, five seconds after the first fix. ADD §7.4
        // step 6: group membership is held still for thirty seconds after a crossing, because a
        // passenger walking along a cell edge would otherwise join and leave the same six groups
        // every few seconds and every one of those is a `RemoveFromGroupAsync`.
        clock.advance(5.seconds)
        live.onPosition(NUGEGODA)
        advanceUntilIdle()

        assertTrue(transport.calls.isEmpty(), "a crossing inside the window sends nothing")
        assertEquals(opening, live.cells.value, "and membership does not move")

        // Past the window, the same fix is applied — and it is a delta, not a re-send of all 19.
        clock.advance(GeoCells.BOUNDARY_HYSTERESIS)
        live.onPosition(NUGEGODA)
        advanceUntilIdle()

        assertTrue(live.cells.value != opening, "the crossing lands once the window has lapsed")
        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, live.cells.value.size)

        val joined = transport.joins().single().cells.toSet()
        val left = transport.leaves().single().cells.toSet()
        assertTrue(joined.isNotEmpty() && left.isNotEmpty(), "a crossing is a delta")
        assertTrue(
            joined.size < GeoCells.PASSENGER_VIEW_CELL_COUNT,
            "only the cells that changed are sent, not all nineteen",
        )
        assertTrue(joined.intersect(left).isEmpty(), "a cell is never joined and left at once")
    }

    @Test
    fun crossing_back_cancels_the_held_crossing() = runTest {
        val clock = MutableClock(Fixtures.NOW)
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this, clock)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()
        val opening = live.cells.value
        transport.clearCalls()

        // The case the hysteresis exists for: a passenger standing on a boundary whose fixes
        // alternate. The held crossing is cancelled by coming back, so there is no group churn at
        // all — not even one deferred change per lapse.
        //
        // Each fix is drained before the clock moves again: `onPosition` launches, and winding the
        // clock first would hand BOTH fixes the later reading, which is a different scenario
        // (a crossing after the window, then a crossing back inside it) that passes for the wrong
        // reason.
        clock.advance(5.seconds)
        live.onPosition(NUGEGODA)
        advanceUntilIdle()
        clock.advance(40.seconds)
        live.onPosition(COLOMBO)
        advanceUntilIdle()

        assertTrue(transport.calls.isEmpty(), "returning to the served cell changes nothing")
        assertEquals(opening, live.cells.value)
    }

    @Test
    fun a_dropped_connection_is_redialled_inside_five_seconds_and_backfills_the_snapshot() = runTest {
        val backend = FakeApiBackend().always(
            "getNearbyVehicles",
            FakeReply.value(
                NearbyVehiclesResponse(vehicles = listOf(nearbyTuk(TUK)), asOf = Fixtures.NOW),
            ),
        )
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this, backend = backend)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()
        transport.clearCalls()
        val startedAt = currentTime

        transport.drop()
        advanceUntilIdle()

        // R-09's curve is 1 s ±25 %, so the first retry lands inside 1.25 s — comfortably inside
        // the five seconds SCR-PA-032 promises, which matters because the SignalR Java client has
        // no `withAutomaticReconnect()` and this loop is the only reconnect there is.
        assertTrue(
            currentTime - startedAt <= 5_000,
            "recovered in ${currentTime - startedAt} ms; SCR-PA-032 promises under five seconds",
        )
        assertEquals(2, transport.connects, "the socket was redialled")
        assertEquals(LiveStatus.Connected, live.status.value)

        // D6' §5.4 / signalr-hub.md §1.1 — rejoin ALL nineteen groups (a reconnect is not churn
        // and is not subject to the hysteresis), then resync from `GET /v1/nearby`.
        val rejoin = transport.joins().single()
        assertEquals(GeoCells.PASSENGER_VIEW_CELL_COUNT, rejoin.cells.size, "everything is rejoined")

        // The snapshot is a real round trip through the fake backend's engine, which runs on its
        // own dispatcher — so it is *awaited* rather than advanced to. Virtual time cannot skip an
        // HTTP call that is not a `delay`.
        assertEquals(listOf(TUK), live.vehicles.first { it.isNotEmpty() }.map { it.vehicleId })
        assertTrue(backend.called("getNearbyVehicles"), "the snapshot back-fills what the socket missed")
    }

    @Test
    fun the_groups_are_rejoined_before_the_snapshot_is_fetched() = runTest {
        // The order IS the contract: a client that snapshots first loses every frame published
        // between the two calls, and those are exactly the frames that moved while it was away.
        //
        // Asserted by looking at what had already been SENT at the instant `/v1/nearby` was
        // called, rather than at the two facts afterwards — "both happened" is true of the wrong
        // order too.
        val transport = FakeLiveHubTransport()
        val backend = FakeApiBackend().always(
            "getNearbyVehicles",
            FakeReply.value(NearbyVehiclesResponse(vehicles = emptyList(), asOf = Fixtures.NOW)),
        )
        val atSnapshot = CompletableDeferred<List<String>>()
        val live = liveMap(
            transport,
            this,
            query = RecordingNearby(backend.mageRideApi().query) {
                atSnapshot.complete(transport.calls.map(HubCall::method))
            },
        )

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()
        live.watchRide(RIDE)
        advanceUntilIdle()
        transport.clearCalls()

        transport.drop()
        advanceUntilIdle()

        assertEquals(
            listOf(LiveHub.Method.JOIN_GEOCELLS, LiveHub.Method.SUBSCRIBE_RIDE),
            atSnapshot.await(),
            "the cells and the ride were rejoined before the snapshot was asked for",
        )
    }

    @Test
    fun a_refused_handshake_is_retried_rather_than_given_up_on() = runTest {
        val transport = FakeLiveHubTransport().apply { failNextConnects = 2 }
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()

        assertEquals(3, transport.connects, "two refusals, then a success")
        assertEquals(LiveStatus.Connected, live.status.value)
    }

    @Test
    fun the_client_never_subscribes_to_a_vehicle() = runTest {
        // The C076 fence, and `signalr-hub.md` §2.1's own rule: `vehicle:{vehicleId}` groups are
        // "joined by the server, never asked for", and there is no `SubscribeVehicle` method. A
        // Mode B vehicle is visible because fanout-svc checked the `share:{userId}` entitlement at
        // join (D-23) — not because the client asked for that vehicle.
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        live.watchRide(RIDE)
        live.watchLocationRequest(LOCATION_REQUEST)
        advanceUntilIdle()

        assertEquals(
            setOf(
                LiveHub.Method.JOIN_GEOCELLS,
                LiveHub.Method.SUBSCRIBE_RIDE,
                LiveHub.Method.SUBSCRIBE_LOC_REQUEST,
            ),
            transport.methodsUsed(),
            "the client → server surface is `signalr-hub.md` §2's four methods and nothing else",
        )
    }

    @Test
    fun a_positions_batch_puts_vehicles_on_the_map() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        transport.emit(
            LiveHub.Event.VEHICLE_POSITIONS,
            """[{"vehicleId":"$TUK","lat":6.9271,"lng":79.8612,"heading":45,"type":"three_wheeler","mode":"C"}]""",
        )
        advanceUntilIdle()

        val drawn = live.vehicles.value.single()
        assertEquals(TUK, drawn.vehicleId)
        // The wire spelling is `three_wheeler`; Gson's default enum binding would have looked for
        // `THREE_WHEELER` and thrown. This is decoded with `MageRideJson`, which is the whole
        // reason the transport hands payloads up as text.
        assertEquals(VehicleType.THREE_WHEELER, drawn.type)
        assertEquals(ServiceMode.C, drawn.mode)
    }

    @Test
    fun a_removed_vehicle_and_a_revoked_share_both_leave_the_map() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        transport.emit(
            LiveHub.Event.VEHICLE_POSITIONS,
            """[{"vehicleId":"$TUK","lat":6.9271,"lng":79.8612,"type":"three_wheeler","mode":"C"},
                {"vehicleId":"$VAN","lat":6.9280,"lng":79.8620,"type":"van","mode":"B"}]""",
        )
        advanceUntilIdle()
        assertEquals(2, live.vehicles.value.size)

        // US-7.16 — a Mode C vehicle went on active hire and left the public groups.
        transport.emit(LiveHub.Event.VEHICLE_REMOVED, """{"vehicleId":"$TUK","reason":"engaged"}""")
        advanceUntilIdle()
        assertEquals(listOf(VAN), live.vehicles.value.map { it.vehicleId })

        // D-22 — the owner revoked this passenger's Mode B share. The platform stops sending
        // frames in under 200 ms; dropping the marker already drawn is the half only we can do.
        transport.emit(LiveHub.Event.SHARE_REVOKED, """{"vehicleId":"$VAN"}""")
        advanceUntilIdle()
        assertTrue(live.vehicles.value.isEmpty(), "a revoked vehicle is not left on screen to go stale")
    }

    @Test
    fun a_malformed_payload_does_not_take_the_socket_down() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        transport.emit(LiveHub.Event.VEHICLE_POSITIONS, """{"this":"is not a batch"}""")
        transport.emit(
            LiveHub.Event.VEHICLE_POSITIONS,
            """[{"vehicleId":"$TUK","lat":6.9271,"lng":79.8612,"type":"sedan","mode":"C"}]""",
        )
        advanceUntilIdle()

        assertEquals(listOf(TUK), live.vehicles.value.map { it.vehicleId }, "the next good batch still lands")
        assertEquals(LiveStatus.Connected, live.status.value)
    }

    @Test
    fun a_directed_event_reaches_a_screen() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)
        val seen = mutableListOf<LiveEvent>()
        val collector = scopes.first().launch { live.events.collect(seen::add) }

        live.connect()
        advanceUntilIdle()
        transport.emit(
            LiveHub.Event.LOCATION_REQUEST_RESOLVED,
            """{"requestId":"$LOCATION_REQUEST","state":"Confirmed","geo":{"lat":6.93,"lng":79.86}}""",
        )
        advanceUntilIdle()

        val resolved = seen.filterIsInstance<LiveEvent.LocationRequest>().single()
        assertEquals(LOCATION_REQUEST, resolved.payload.requestId)
        assertNotNull(resolved.payload.geo, "a Confirmed carries the pin the booker's screen fills in")
        collector.cancel()
    }

    @Test
    fun signing_out_drops_the_map_and_the_subscription() = runTest {
        val transport = FakeLiveHubTransport()
        val live = liveMap(transport, this)

        live.connect()
        advanceUntilIdle()
        live.onPosition(COLOMBO)
        advanceUntilIdle()
        transport.emit(
            LiveHub.Event.VEHICLE_POSITIONS,
            """[{"vehicleId":"$TUK","lat":6.9271,"lng":79.8612,"type":"sedan","mode":"C"}]""",
        )
        advanceUntilIdle()

        live.disconnect()

        assertTrue(live.cells.value.isEmpty(), "nothing is subscribed")
        assertTrue(live.vehicles.value.isEmpty(), "and nothing is left on the map")
        assertEquals(LiveStatus.Disconnected, live.status.value)
    }

    // ------------------------------------------------------------------------------------------

    private fun liveMap(
        transport: FakeLiveHubTransport,
        scope: TestScope,
        clock: MutableClock = MutableClock(Fixtures.NOW),
        backend: FakeApiBackend = FakeApiBackend().always(
            "getNearbyVehicles",
            FakeReply.value(NearbyVehiclesResponse(vehicles = emptyList(), asOf = Fixtures.NOW)),
        ),
        query: QueryApi = backend.mageRideApi().query,
    ): PassengerLiveMap = PassengerLiveMap(
        transport = transport,
        query = query,
        grid = grid,
        scope = CoroutineScope(StandardTestDispatcher(scope.testScheduler) + Job()).also(scopes::add),
        now = clock::now,
        // A fixed seed, and the SPEC'S OWN ±25 % band rather than a flattened one. R-09's jitter
        // is not decoration — it is what stops a regional outage ending in a synchronised
        // reconnect wave — so a test that switched it off would be asserting a curve this app does
        // not use. The seed only makes the run reproducible; what is asserted is the ceiling.
        newBackoff = { ReconnectBackoff(random = Random(SEED)) },
    )

    /**
     * query-svc, with a hook on the one operation the recovery calls.
     *
     * Kotlin interface delegation so the other eight methods stay the real client's: what is being
     * tested is *when* `/v1/nearby` is asked for, not what it answers, and a hand-written stub of
     * a nine-method interface would drift the moment C078 uses a tenth.
     */
    private class RecordingNearby(private val delegate: QueryApi, private val onCall: () -> Unit) :
        QueryApi by delegate {
        override suspend fun getNearbyVehicles(
            lat: Double,
            lng: Double,
            radiusMetres: Int?,
            types: List<VehicleType>?,
            modes: List<ServiceMode>?,
        ): NearbyVehiclesResponse {
            onCall()
            return delegate.getNearbyVehicles(lat, lng, radiusMetres, types, modes)
        }
    }

    /** A clock a test winds by hand — `GeoCellSubscription`'s whole behaviour is read off it. */
    private class MutableClock(private var value: Timestamp) {
        fun now(): Timestamp = value
        fun advance(by: kotlin.time.Duration) {
            value += by
        }
    }

    private fun nearbyTuk(id: String) = NearbyVehicle(
        vehicleId = id,
        type = VehicleType.THREE_WHEELER,
        mode = ServiceMode.C,
        lat = COLOMBO.lat,
        lng = COLOMBO.lng,
    )

    private companion object {
        /** Colombo Fort. */
        val COLOMBO = GeoPoint(lat = 6.9344, lng = 79.8428)

        /** ~9 km away — a different res-7 cell by any reckoning. */
        val NUGEGODA = GeoPoint(lat = 6.8649, lng = 79.8997)

        const val TUK = "01JVEH0000000000000000001"
        const val VAN = "01JVEH0000000000000000002"
        const val RIDE = "01JRIDE0000000000000000000"
        const val LOCATION_REQUEST = "01JLOCREQ00000000000000000"
        const val SEED = 20260803
    }
}

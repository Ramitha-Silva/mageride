package lk.mageride.driver.tracker

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.FakePositionPublisher
import lk.mageride.driver.home.HOME_VEHICLE_ID
import lk.mageride.driver.home.liveVehicle
import lk.mageride.driver.home.signedInSessions
import lk.mageride.driver.jobs.identity
import lk.mageride.driver.location.PositionPublisher
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.BindVehicleDeviceResponse
import lk.mageride.shared.data.models.registry.VehicleListResponse
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
 * SCR-DA-027 — pairing, and the fence that hangs off it.
 *
 * The definition-of-done case is here: *"pairing a tracker visibly stops phone GPS ingestion for
 * that vehicle"*. It is asserted twice, because it is two separate things — the running publisher
 * is stopped by [TrackerPairingViewModel], and the *next* start is refused by
 * [TrackerPositionPublisher], which is what every go-online and every Start Journey goes through.
 */
class TrackerPairingViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val bindings = FakeTrackerBindingStore()
    private val publisher = FakePositionPublisher()

    private val bindingId = "01JBINDING000000000000001"
    private val secondVehicleId: Ulid = "01JVEHICLE0000000000000011"

    @BeforeTest
    fun setUp() {
        main.install()
        backend.returns("listMyVehicles", VehicleListResponse(items = listOf(liveVehicle())))
        backend.returns("bindVehicleDevice", BindVehicleDeviceResponse(bindingId = bindingId))
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_live_vehicle_is_what_the_form_opens_on() = runBlocking {
        val model = viewModel()
        val state = model.state.await { !it.loading }

        assertEquals(HOME_VEHICLE_ID, state.selectedVehicleId)
        assertFalse(state.isPaired)
        assertFalse(state.canPair, "nothing typed yet")
    }

    @Test
    fun pairing_sends_the_imei_and_records_the_binding() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.onImeiChange("8612 3456 7890 123")
        assertTrue(model.state.value.canPair)
        model.pair()

        val state = model.state.await { it.binding != null }
        assertEquals(TRACKER_IMEI, state.binding?.imei)
        assertEquals(bindingId, state.binding?.bindingId, "the 201 IS the cert-issued confirmation")
        assertEquals("", state.imei, "the field clears; the vehicle is paired and cannot be paired again")
        assertFalse(state.canPair)

        val body = MageRideJson.parseToJsonElement(backend.lastCall("bindVehicleDevice").body).toString()
        assertTrue(body.contains("\"imei\":\"$TRACKER_IMEI\""), body)
    }

    @Test
    fun pairing_stops_the_phone_publishing_for_that_vehicle() = runBlocking {
        // The DoD case, first half: a driver who is online RIGHT NOW on the vehicle they have just
        // paired has a publisher running, and a gate that only refused the next start would leave
        // it running until they went offline.
        val model = viewModel()
        model.state.await { !it.loading }
        model.onImeiChange(TRACKER_IMEI)
        model.pair()

        model.state.await { it.binding != null }
        assertEquals(listOf("stop"), publisher.calls)
    }

    @Test
    fun a_paired_vehicle_can_no_longer_start_the_phone_publisher() = runBlocking {
        // The DoD case, second half — US-3.6's "exactly one publisher at a time", enforced at the
        // seam SCR-DA-010's toggle, SCR-DA-011's Start Journey and US-5.10's Restart all go through.
        val model = viewModel()
        model.state.await { !it.loading }
        model.onImeiChange(TRACKER_IMEI)
        model.pair()
        model.state.await { it.binding != null }

        val gate: PositionPublisher = TrackerPositionPublisher(delegate = publisher, bindings = bindings)
        publisher.calls.clear()

        gate.start(HOME_VEHICLE_ID)
        assertTrue(publisher.calls.isEmpty(), "the device is the single publisher for this vehicle")

        // A driver with a tracked bus and an untracked tuk goes online on the tuk normally.
        gate.start(secondVehicleId)
        assertEquals(listOf("start:$secondVehicleId"), publisher.calls)

        // `stop` is never gated: swallowing one would leave a handset publishing for a vehicle it
        // had just been paired away from, which is the state the gate exists to prevent.
        gate.stop()
        assertEquals(listOf("start:$secondVehicleId", "stop"), publisher.calls)
    }

    @Test
    fun a_duplicate_imei_is_a_quarantine_notice_and_not_a_retry() = runBlocking {
        // US-3.4 / T-08. The serial is already active somewhere on the platform; offering the same
        // button again would invite a driver to keep pressing it.
        backend.fails("bindVehicleDevice", HttpStatusCode.Conflict, "imei-duplicate")

        val model = viewModel()
        model.state.await { !it.loading }
        model.onImeiChange(TRACKER_IMEI)
        model.pair()

        val state = model.state.await { it.error != null }
        assertTrue(state.quarantined)
        assertNull(state.binding, "nothing is recorded locally, so the phone keeps publishing")
        assertFalse(state.pairing)
    }

    @Test
    fun a_failed_pair_never_stops_the_phone_publishing() = runBlocking {
        backend.fails("bindVehicleDevice", HttpStatusCode.Conflict, "imei-duplicate")

        val model = viewModel()
        model.state.await { !it.loading }
        model.onImeiChange(TRACKER_IMEI)
        model.pair()
        model.state.await { it.error != null }

        assertTrue(publisher.calls.isEmpty(), "the device did not take over, so this handset must not stop")
    }

    @Test
    fun a_scanned_code_fills_the_field_and_an_unreadable_one_says_so() = runBlocking {
        val model = viewModel()
        model.state.await { !it.loading }

        model.startScan()
        assertTrue(model.state.value.scanning)

        model.onScanned("IMEI:$TRACKER_IMEI")
        val filled = model.state.value
        assertFalse(filled.scanning)
        assertEquals(TRACKER_IMEI, filled.imei)
        assertNull(filled.error)

        model.startScan()
        model.onScanned("ICCID:89940011223344556677")
        val refused = model.state.value
        assertFalse(refused.scanning)
        assertNotNull(refused.error, "a scan that cannot be read leaves the driver typing")
        assertEquals(TRACKER_IMEI, refused.imei, "and does not wipe what was already there")
    }

    @Test
    fun changing_the_vehicle_clears_the_imei_typed_for_the_other_one() = runBlocking {
        // An IMEI belongs to the vehicle it was typed for. Carrying it across the selector is how a
        // tracker gets bound to the wrong vehicle.
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                items = listOf(
                    liveVehicle(),
                    liveVehicle(vehicleId = secondVehicleId, mode = ServiceMode.B),
                ),
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }
        model.onImeiChange(TRACKER_IMEI)

        model.selectVehicle(secondVehicleId)
        val state = model.state.value
        assertEquals(secondVehicleId, state.selectedVehicleId)
        assertEquals("", state.imei)
        assertFalse(state.canPair)
    }

    private suspend fun viewModel(): TrackerPairingViewModel {
        val api = backend.mageRideApi()
        return main.own(
            TrackerPairingViewModel(
                identity = identity(backend, signedInSessions(backend)),
                trackers = TrackerRepository(registry = api.registry, bindings = bindings),
                publisher = publisher,
            ),
        )
    }
}

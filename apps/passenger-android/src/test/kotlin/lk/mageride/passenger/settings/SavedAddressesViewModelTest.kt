package lk.mageride.passenger.settings

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.passenger.location.PassengerFix
import lk.mageride.passenger.location.PassengerLocationSource
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.iam.SavedAddressListResponse
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import lk.mageride.shared.serialization.MageRideJson
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-026 and SCR-PA-026a — the address book.
 *
 * Four things carry consequences and are asserted here: **all three lines and the label round-trip**
 * (the component's own Definition of Done), the Home and Work rows are the `isHome`/`isWork` flags
 * and not a label convention, a reverse geocode that fails still leaves an address that can be
 * saved, and an edit that moves the Home flag clears it off whichever row held it.
 */
class SavedAddressesViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val locations = FakeLocationSource()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_saved_address_round_trips_all_three_lines_and_its_label() = runBlocking {
        // The Definition of Done, from both ends: what the sheet captured is what `POST` carried,
        // and what came back is what the row renders. AL-26's four fields exist precisely because
        // "a single string" lost the middle of an address.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = emptyList()))
        backend.returns("reverseGeocode", NUGEGODA)
        backend.returns("createSavedAddress", GYM)

        val model = viewModel()
        model.state.await { !it.loading }
        locations.emit(PassengerFix(lat = GYM.lat, lng = GYM.lng))
        model.state.await { it.pin != null }

        model.addAddress()
        model.state.await { it.sheet?.locating == false }
        model.onLine1Changed("No. 42, Galle Road")
        model.onLine2Changed("Kollupitiya")
        model.onLine3Changed("Colombo 03")
        model.onLabelChanged("Gym")
        model.save()

        model.state.await { it.sheet == null && it.labelled.isNotEmpty() }

        val sent = MageRideJson.parseToJsonElement(backend.lastCall("createSavedAddress").body).toString()
        assertTrue(sent.contains("No. 42, Galle Road"), "line 1")
        assertTrue(sent.contains("Kollupitiya"), "line 2 — the one a single-string address lost")
        assertTrue(sent.contains("Colombo 03"), "line 3")
        assertTrue(sent.contains("\"label\":\"Gym\""), "the free-text label")

        val row = model.state.value.labelled.single()
        assertEquals(listOf("No. 42, Galle Road", "Kollupitiya", "Colombo 03"), listOf(row.line1, row.line2, row.line3))
    }

    @Test
    fun an_empty_line_travels_as_null_rather_than_as_a_blank() = runBlocking {
        // The contract types lines 2 and 3 as optional. Storing "" would put a blank in a list that
        // then has to render it; a line the passenger left empty is a line they do not have.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = emptyList()))
        backend.returns("reverseGeocode", NUGEGODA)
        backend.returns("createSavedAddress", GYM)

        val model = viewModel()
        model.state.await { !it.loading }
        locations.emit(PassengerFix(lat = GYM.lat, lng = GYM.lng))
        model.state.await { it.pin != null }
        model.addAddress()
        model.state.await { it.sheet?.locating == false }

        model.onLine1Changed("No. 42, Galle Road")
        model.onLine2Changed("   ")
        model.onLine3Changed("")
        model.onLabelChanged("Gym")
        model.save()
        model.state.await { it.sheet == null }

        val body = backend.lastCall("createSavedAddress").body
        assertFalse(body.contains("\"line2\""), "a blank line is absent, not empty")
        assertFalse(body.contains("\"line3\""))
    }

    @Test
    fun home_and_work_are_the_flags_and_the_rest_are_labelled_rows() = runBlocking {
        // `isHome`/`isWork` and not `label == "Home"`: the label is free text a passenger types in
        // their own language, and matching against it would make a Sinhala "නිවස" not a Home.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = listOf(HOME, WORK, GYM)))

        val state = viewModel().state.await { !it.loading }

        assertEquals(HOME.addressId, state.home?.addressId)
        assertEquals(WORK.addressId, state.work?.addressId)
        assertEquals(listOf(GYM.addressId), state.labelled.map { it.addressId })
    }

    @Test
    fun the_home_row_opens_the_sheet_on_the_shortcut_and_sends_the_flag() = runBlocking {
        // US-22.1 — "save Home and Work by selecting the location on the map". The wireframe draws
        // no Home/Work control inside the sheet, so which row was tapped is what decides it.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = emptyList()))
        backend.returns("reverseGeocode", NUGEGODA)
        backend.returns("createSavedAddress", HOME)

        val model = viewModel()
        model.state.await { !it.loading }
        locations.emit(PassengerFix(lat = HOME.lat, lng = HOME.lng))
        model.state.await { it.pin != null }

        model.editShortcut(AddressShortcut.HOME, "Home")
        val sheet = model.state.await { it.sheet?.locating == false }.sheet!!
        assertEquals(AddressShortcut.HOME, sheet.shortcut)
        assertEquals("Home", sheet.label, "pre-filled, so the sheet still has only AL-26's four fields")

        model.onLine1Changed("221 Galle Rd")
        model.save()
        model.state.await { it.sheet == null }

        val body = backend.lastCall("createSavedAddress").body
        assertTrue(body.contains("\"isHome\":true"))
        assertTrue(body.contains("\"isWork\":false"))
    }

    @Test
    fun the_pin_pre_fills_the_street_and_the_city_and_leaves_the_suburb_alone() = runBlocking {
        // `GeocodedPlace` carries `line1` and `city` — AL-26's FIRST and THIRD lines. Line 2 is
        // "area/suburb" and Nominatim answers no such field; splitting `displayName` on commas to
        // invent one would fill a form with a guess nobody checked.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = emptyList()))
        backend.returns("reverseGeocode", NUGEGODA)

        val model = viewModel()
        model.state.await { !it.loading }
        locations.emit(PassengerFix(lat = NUGEGODA.lat, lng = NUGEGODA.lng))
        model.state.await { it.pin != null }

        model.addAddress()
        val sheet = model.state.await { it.sheet?.locating == false }.sheet!!

        assertEquals("High Level Road", sheet.line1)
        assertEquals("", sheet.line2)
        assertEquals("Nugegoda", sheet.line3)
    }

    @Test
    fun a_geocoder_that_cannot_name_the_pin_still_leaves_an_address_that_can_be_saved() = runBlocking {
        // AL-14's lookup is a pre-fill, never a gate: `GET /v1/geo/reverse` answers 404 in the sea
        // and 503 when Nominatim is down, and the passenger dropped the pin where they meant to.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = emptyList()))
        backend.fails("reverseGeocode", HttpStatusCode.ServiceUnavailable, "dependency-unavailable")
        backend.returns("createSavedAddress", GYM)

        val model = viewModel()
        model.state.await { !it.loading }
        locations.emit(PassengerFix(lat = GYM.lat, lng = GYM.lng))
        model.state.await { it.pin != null }

        model.addAddress()
        val sheet = model.state.await { it.sheet?.locating == false }.sheet!!

        assertNull(model.state.value.error, "a failed lookup is not an error the passenger sees")
        assertEquals("", sheet.line1)

        model.onLine1Changed("No. 42, Galle Road")
        model.onLabelChanged("Gym")
        assertTrue(model.state.value.sheet!!.canSave)
    }

    @Test
    fun moving_the_home_flag_clears_it_off_the_row_that_held_it() = runBlocking {
        // What the server does — "moving the Home or Work flag to this address clears it from
        // whichever address held it" — reflected without a second read. Two Home rows on screen
        // until a refetch came back would be the list disagreeing with itself.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = listOf(HOME, GYM)))
        backend.returns("updateSavedAddress", GYM.copy(isHome = true))

        val model = viewModel()
        model.state.await { !it.loading }

        model.editShortcut(AddressShortcut.HOME, "Home")
        model.state.await { it.sheet != null }
        // The Home row is edited where it is, so its own id is what is replaced.
        assertEquals(HOME.addressId, model.state.value.sheet?.addressId)

        model.dismissSheet()
        model.edit(GYM)
        model.onLabelChanged("Gym")
        model.save()
        model.state.await { it.sheet == null }

        val state = model.state.value
        assertEquals(GYM.addressId, state.home?.addressId, "the flag moved")
        assertEquals(listOf(HOME.addressId), state.labelled.map { it.addressId }, "and the old Home is now labelled")
    }

    @Test
    fun a_second_home_is_reported_as_the_conflict_it_is() = runBlocking {
        // C003's partial unique indexes refuse a second Home. Reachable only from a stale list —
        // the screen edits the row it already has — so the message says to edit that one.
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = emptyList()))
        backend.returns("reverseGeocode", NUGEGODA)
        backend.fails("createSavedAddress", HttpStatusCode.Conflict, "conflict")

        val model = viewModel()
        model.state.await { !it.loading }
        locations.emit(PassengerFix(lat = HOME.lat, lng = HOME.lng))
        model.state.await { it.pin != null }
        model.editShortcut(AddressShortcut.HOME, "Home")
        model.state.await { it.sheet?.locating == false }
        model.onLine1Changed("221 Galle Rd")

        model.save()
        val state = model.state.await { it.error != null }

        assertEquals(R.string.error_address_shortcut_taken, state.error)
        assertFalse(state.sheet!!.saving, "and the sheet stays open with what was typed")
    }

    @Test
    fun deleting_removes_the_row_and_closes_the_sheet() = runBlocking {
        backend.returns("listSavedAddresses", SavedAddressListResponse(items = listOf(HOME, GYM)))

        val model = viewModel()
        model.state.await { !it.loading }
        model.edit(GYM)
        model.state.await { it.sheet != null }

        model.delete()
        val state = model.state.await { it.sheet == null && it.labelled.isEmpty() }

        assertTrue(backend.called("deleteSavedAddress"))
        assertNull(state.busyWith)
        assertEquals(HOME.addressId, state.home?.addressId, "the other row is untouched")
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel(): SavedAddressesViewModel {
        val api = backend.mageRideApi()
        return main.own(
            SavedAddressesViewModel(
                addresses = ApiAddressBook(iam = api.iam, query = api.query),
                locations = locations,
                keys = { KEY },
            ),
        )
    }

    /** Fixes a test hands over by name, rather than a satellite. */
    private class FakeLocationSource : PassengerLocationSource {
        private val flow = MutableSharedFlow<PassengerFix>(replay = 1)
        override val fixes: Flow<PassengerFix> = flow
        suspend fun emit(fix: PassengerFix) = flow.emit(fix)
    }

    private companion object {
        const val KEY = "01JKEY00000000000000000001"

        val HOME = SavedAddress(
            addressId = "01JADDR000000000000000001",
            label = "Home",
            line1 = "221 Galle Rd",
            line3 = "Dehiwala",
            lat = 6.8511,
            lng = 79.8653,
            isHome = true,
        )
        val WORK = SavedAddress(
            addressId = "01JADDR000000000000000002",
            label = "Work",
            line1 = "World Trade Center",
            line3 = "Colombo 01",
            lat = 6.9344,
            lng = 79.8428,
            isWork = true,
        )
        val GYM = SavedAddress(
            addressId = "01JADDR000000000000000003",
            label = "Gym",
            line1 = "No. 42, Galle Road",
            line2 = "Kollupitiya",
            line3 = "Colombo 03",
            lat = 6.9101,
            lng = 79.8501,
        )
        val NUGEGODA = GeocodedPlace(
            lat = 6.8649,
            lng = 79.8997,
            displayName = "Nugegoda Junction, Nugegoda",
            line1 = "High Level Road",
            city = "Nugegoda",
            source = GeocodedPlaceSource.NOMINATIM,
        )
    }
}

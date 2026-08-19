package lk.mageride.passenger.booking

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import lk.mageride.shared.data.models.query.PlaceSearchResponse
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds

/**
 * The Map capture method's search box, and the one rule that makes it honest.
 *
 * **A search result names the pin; it does not become the pin.** Everything below is a consequence
 * of that: tapping a result moves the camera and lends its name, nudging the map takes the name
 * back, and what is committed is always the coordinates under the marker the passenger is looking
 * at. A picker that committed the geocoder's point instead would move a pickup after it had been
 * placed — on SCR-PA-010b, somebody else's pickup.
 */
class MapPickViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
        .always("searchPlaces", FakeReply.value(PlaceSearchResponse(places = listOf(FORT, PETTAH))))

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_lookup_is_debounced_and_biased_towards_the_pin() = runBlocking {
        // Nominatim is self-hosted (D-14) and shared with every passenger in the country, so a
        // request per keystroke turns a search box into a load test — the same reason SCR-PA-008
        // debounces. The bias is the pin rather than the passenger: this sheet is routinely used to
        // place somebody ELSE's pickup, and the map is already where the booker was looking.
        val model = viewModel()
        model.opened(COLOMBO)

        model.onQueryChanged("Sta")
        model.onQueryChanged("Stat")
        model.onQueryChanged("Station")
        model.awaitPredictions(FORT.displayName, PETTAH.displayName)
        delay(SETTLE)

        assertEquals(1, backend.callsTo("searchPlaces").size, "one request for the word, not one per letter")
        val call = backend.lastCall("searchPlaces")
        assertEquals("Station", call.query["q"])
        assertEquals(COLOMBO.lat.toString(), call.query["lat"])
        assertEquals(COLOMBO.lng.toString(), call.query["lng"])
    }

    @Test
    fun two_characters_spend_no_request_at_all() = runBlocking {
        val model = viewModel()
        model.opened(COLOMBO)

        model.onQueryChanged("Fo")
        delay(SETTLE)

        assertTrue(model.state.value.showingDefaults)
        assertTrue(model.state.value.predictions.isEmpty())
        assertFalse(backend.called("searchPlaces"), "nothing went out for two characters")
    }

    @Test
    fun choosing_a_result_flies_the_pin_to_it_and_lends_it_the_name() = runBlocking {
        // The camera move is the whole point of the search: `MageRideMap.camera` is read once when
        // the style loads, so before `focus` existed a result had nowhere to go and the passenger
        // still had to pan across the city by hand.
        val model = viewModel()
        model.opened(COLOMBO)
        model.onQueryChanged("Station")
        model.awaitPredictions(FORT.displayName, PETTAH.displayName)

        model.onPredictionChosen(FORT)

        val state = model.state.value
        assertEquals(GeoPoint(FORT.lat, FORT.lng), state.focus, "the map is asked to move")
        assertEquals(FORT.displayName, state.chosen?.displayName)
        assertTrue(state.predictions.isEmpty(), "the list closes over the map it just moved")
    }

    @Test
    fun the_camera_settling_on_the_result_keeps_its_name() = runBlocking {
        // `onCameraIdle` fires straight after the animation, a metre or two off what was asked for.
        // Without the tolerance the name would be dropped by the very move that fetched it.
        val model = viewModel()
        model.opened(COLOMBO)
        model.onPredictionChosen(FORT)

        model.onPinMoved(GeoPoint(lat = FORT.lat + 0.00005, lng = FORT.lng))

        assertEquals(FORT.displayName, model.state.value.chosen?.displayName)
    }

    @Test
    fun panning_off_a_named_place_gives_the_coordinates_back() = runBlocking {
        // The honest half of the rule. The pin is no longer on Colombo Fort, so calling it Colombo
        // Fort would put a name on a point that is not it — and the label the booker reads, and the
        // address that reaches SCR-PA-009, would both be wrong.
        val model = viewModel()
        model.opened(COLOMBO)
        model.onPredictionChosen(FORT)

        val moved = GeoPoint(lat = 6.8480, lng = 79.9265)
        model.onPinMoved(moved)

        val state = model.state.value
        assertNull(state.chosen, "a pin two towns away is not that place")
        assertNull(state.focus, "and the camera request goes with it, so the same result can move it again")
        assertEquals(moved, state.centre)
        assertEquals(null, state.selection?.address)
        assertEquals(moved.lat, state.selection?.lat)
    }

    @Test
    fun what_is_committed_is_where_the_pin_is_under_the_name_it_was_given() = runBlocking {
        // Both halves of `selection` in one assertion: the coordinates are the marker's, the name
        // is the search result's. Nudging inside the tolerance keeps the name and still commits the
        // nudged point — the passenger aimed there.
        val model = viewModel()
        model.opened(COLOMBO)
        model.onPredictionChosen(FORT)
        val nudged = GeoPoint(lat = FORT.lat + 0.0001, lng = FORT.lng)
        model.onPinMoved(nudged)

        val selection = requireNotNull(model.state.value.selection)

        assertEquals(FORT.displayName, selection.address)
        assertEquals(nudged.lat, selection.lat)
        assertEquals(nudged.lng, selection.lng)
    }

    @Test
    fun a_geocoder_that_cannot_answer_leaves_the_pin_working() = runBlocking {
        // AL-14 makes the same call about a reverse geocode: a lookup that fails costs a
        // convenience, never the capture. The passenger dropped the pin where they meant to.
        backend.always("searchPlaces", FakeReply.problem(HttpStatusCode.BadGateway, "upstream-unavailable"))
        val model = viewModel()
        model.opened(COLOMBO)

        model.onQueryChanged("Station")
        val state = model.state.await { it.geocoderDown }

        assertFalse(state.searching)
        assertEquals(COLOMBO, state.selection?.point, "the pin is untouched and still committable")
    }

    @Test
    fun opening_the_sheet_again_starts_from_the_field_it_was_opened_for() = runBlocking {
        // One model serves SCR-PA-010b's pickup and both of SCR-PA-012's ends, because a
        // `koinViewModel()` in one back-stack entry is one instance. A query left in the field from
        // the last capture would be somebody else's search, and a stale `chosen` would name this
        // pin after the last one.
        val model = viewModel()
        model.opened(COLOMBO)
        model.onQueryChanged("Station")
        model.awaitPredictions(FORT.displayName, PETTAH.displayName)
        model.onPredictionChosen(FORT)

        val elsewhere = GeoPoint(lat = 6.8480, lng = 79.9265)
        model.opened(elsewhere)

        val state = model.state.value
        assertEquals("", state.query)
        assertTrue(state.predictions.isEmpty())
        assertNull(state.chosen)
        assertEquals(elsewhere, state.centre)
    }

    // ------------------------------------------------------------------------------------------

    private suspend fun MapPickViewModel.awaitPredictions(vararg names: String) =
        state.await { !it.searching && it.predictions.map(GeocodedPlace::displayName) == names.toList() }

    private fun viewModel() = main.own(MapPickViewModel(query = backend.mageRideApi().query))

    private companion object {
        /** Comfortably past the 300 ms debounce, so a second request would have been seen. */
        val SETTLE = 600.milliseconds

        val COLOMBO = GeoPoint(lat = 6.9271, lng = 79.8612)

        val FORT = GeocodedPlace(
            lat = 6.9344,
            lng = 79.8428,
            displayName = "Colombo Fort",
            line1 = "Olcott Mawatha",
            city = "Colombo",
            source = GeocodedPlaceSource.NOMINATIM,
        )
        val PETTAH = GeocodedPlace(
            lat = 6.9355,
            lng = 79.8500,
            displayName = "Pettah",
            city = "Colombo",
            source = GeocodedPlaceSource.NOMINATIM,
        )
    }
}

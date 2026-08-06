package lk.mageride.passenger.home

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.await
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.iam.SavedAddress
import lk.mageride.shared.data.models.iam.SavedAddressListResponse
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
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds

/**
 * SCR-PA-008 — and the fence that is the whole reason this screen is small.
 *
 * **AL-17: geo only.** The interesting assertions here are the negative ones — that typing `138`
 * produces a place lookup and nothing else, and that `getBusesOnRoute` is never reached from this
 * screen. D2' §SCR-PA-008 says the opposite; the wireframe and AL-17 say this, and they win. See
 * [SearchLocationViewModel]'s KDoc and the C078 handoff.
 *
 * The rest is the debounce, which is not a nicety: Nominatim is self-hosted (D-14) and shared with
 * every other passenger in the country, so a request per keystroke turns a search box into a load
 * test.
 */
class SearchLocationViewModelTest {

    private val main = MainDispatcher()
    private val recents = FakeRecentPlaces()
    private val backend = FakeApiBackend()
        .always("listSavedAddresses", FakeReply.value(SavedAddressListResponse(items = listOf(HOME))))
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
    fun typing_a_route_number_returns_places_and_never_a_route() = runBlocking {
        // The fence, asserted from both ends. `138` is the Kottawa–Pettah bus route and is exactly
        // the string D2' §SCR-PA-008 uses as its example of what this field should accept; here it
        // is a *place* query like any other. There is no route row in the result type at all —
        // `GeocodedPlace` cannot express one — and `getBusesOnRoute` stays untouched.
        val model = viewModel()

        model.onQueryChanged("138")
        val state = model.awaitPredictions(FORT.displayName, PETTAH.displayName)

        assertEquals("138", backend.lastCall("searchPlaces").query["q"], "the digits went to the geocoder")
        assertFalse(backend.called("getBusesOnRoute"), "AL-17 — a route number is not a destination")
        assertEquals(listOf(FORT.displayName, PETTAH.displayName), state.predictions.map { it.displayName })
    }

    @Test
    fun choosing_a_prediction_is_what_writes_the_recent() = runBlocking {
        // §2.2's table is "recent / SEARCHED locations", and this is the screen where searching
        // happens — so the write is here rather than in the booking flow. It is what fills
        // SCR-PA-010's "Recent" list, and it is local-only: nothing about it is sent anywhere.
        val model = viewModel()
        model.onQueryChanged("Fort")
        model.awaitPredictions(FORT.displayName, PETTAH.displayName)

        model.onPlaceChosen(FORT)

        assertEquals(listOf(FORT.displayName), recents.rows.map { it.displayName })
    }

    @Test
    fun a_query_shorter_than_three_characters_shows_saved_places_instead() = runBlocking {
        // Two characters is nothing to a geocoder and everything to a rate limit. The screen is
        // not blank while the passenger types, though — the wireframe's "Empty → recents/saved" is
        // what fills it, and neither half of that needs the geocoder.

        recents.rows += NUGEGODA

        val model = viewModel()
        model.onQueryChanged("Fo")
        val state = model.state.await { it.predictions.size == 2 }

        assertTrue(state.showingDefaults)
        assertFalse(backend.called("searchPlaces"), "nothing went out for two characters")

        // "Empty → recents/saved", in that order: the places this handset has been looking for
        // come before the ones the account has stored.
        assertEquals(listOf(NUGEGODA.displayName, HOME.label), state.predictions.map { it.displayName })
        assertEquals(GeocodedPlaceSource.RECENT, state.predictions.first().source, "🕘")
        assertEquals(GeocodedPlaceSource.SAVED, state.predictions.last().source, "★")
    }

    @Test
    fun typing_quickly_makes_one_request_rather_than_one_per_letter() = runBlocking {
        // The debounce. Each keystroke cancels the pending lookup, so a passenger typing "Fort"
        // sends one request for the whole word — and the answer that lands is always the one for
        // what is on screen, which the cancel is what guarantees.
        val model = viewModel()

        model.onQueryChanged("For")
        model.onQueryChanged("Fort")
        model.onQueryChanged("Fort ")
        model.onQueryChanged("Fort R")
        model.awaitPredictions(FORT.displayName, PETTAH.displayName)

        // Past the debounce window with room to spare, so a second request would have landed by
        // now if the earlier keystrokes had each started one.
        delay(SETTLE)

        assertEquals(1, backend.callsTo("searchPlaces").size)
        assertEquals("Fort R", backend.lastCall("searchPlaces").query["q"], "and it is the last thing typed")
    }

    @Test
    fun the_lookup_is_biased_towards_where_the_passenger_is() = runBlocking {
        // A search for "Station" from Colombo and the same search from Jaffna are different
        // questions. The map hands the fix over; without it the geocoder ranks the whole country.
        val model = viewModel()
        model.biasAround(GeoPoint(lat = 6.9344, lng = 79.8428))

        model.onQueryChanged("Station")
        model.awaitPredictions(FORT.displayName, PETTAH.displayName)

        val call = backend.lastCall("searchPlaces")
        assertEquals("6.9344", call.query["lat"])
        assertEquals("79.8428", call.query["lng"])
    }

    @Test
    fun a_geocoder_failure_offers_the_map_rather_than_an_error_dialog() = runBlocking {
        // The wireframe's "geocoder down → Pick on map". Nominatim being down is not something a
        // passenger can retry their way out of, and they still have a map and a pin — which is a
        // better answer than a dialog with an OK button on it.
        backend.always("searchPlaces", FakeReply.problem(HttpStatusCode.BadGateway, "upstream-unavailable"))
        val model = viewModel()

        model.onQueryChanged("Fort")
        val state = model.state.await { it.geocoderDown }

        assertFalse(state.searching)
        assertEquals(lk.mageride.passenger.R.string.search_geocoder_down, state.error)
    }

    @Test
    fun a_new_keystroke_clears_the_previous_failure() = runBlocking {
        // Otherwise the "search is unavailable" line outlives the outage and sits under a field
        // that is working again.
        backend.always("searchPlaces", FakeReply.problem(HttpStatusCode.BadGateway, "upstream-unavailable"))
        val model = viewModel()
        model.onQueryChanged("Fort")
        model.state.await { it.geocoderDown }

        backend.always("searchPlaces", FakeReply.value(PlaceSearchResponse(places = listOf(FORT))))
        model.retry()
        val state = model.awaitPredictions(FORT.displayName)

        assertEquals(null, state.error)
        assertFalse(state.geocoderDown, "the field works again and stops saying it does not")
    }

    // ------------------------------------------------------------------------------------------

    /**
     * Waits for exactly [names] to be on screen, rather than for "something" to be.
     *
     * The screen is **never empty** — `loadDefaults()` fills it with the passenger's saved places
     * at construction and again on every keystroke below the minimum length — so a predicate of
     * `predictions.isNotEmpty()` is satisfied by the ★ Home row before the debounced lookup has
     * even gone out, and the assertion after it reads the wrong state.
     */
    private suspend fun SearchLocationViewModel.awaitPredictions(vararg names: String) =
        state.await { !it.searching && it.predictions.map(GeocodedPlace::displayName) == names.toList() }

    private fun viewModel() = main.own(
        SearchLocationViewModel(
            query = backend.mageRideApi().query,
            iam = backend.mageRideApi().iam,
            recents = recents,
        ),
    )

    /** §2.2's table, in memory. The real one is SQLCipher, which does not open on this host. */
    private class FakeRecentPlaces(val rows: MutableList<GeocodedPlace> = mutableListOf()) : RecentPlaces {
        override suspend fun recent(limit: Int): List<GeocodedPlace> = rows.take(limit)
        override suspend fun remember(place: GeocodedPlace) {
            rows.add(0, place)
        }
    }

    private companion object {
        /** Comfortably past the 300 ms debounce, so a second request would have been seen. */
        val SETTLE = 600.milliseconds

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
        val NUGEGODA = GeocodedPlace(
            lat = 6.8649,
            lng = 79.8997,
            displayName = "Nugegoda Junction",
            source = GeocodedPlaceSource.RECENT,
        )
        val HOME = SavedAddress(
            addressId = "01JADDR000000000000000001",
            label = "Home",
            line1 = "22 Galle Road",
            lat = 6.9271,
            lng = 79.8612,
        )
    }
}

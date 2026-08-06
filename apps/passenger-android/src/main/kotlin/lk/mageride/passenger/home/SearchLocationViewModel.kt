package lk.mageride.passenger.home

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.R
import lk.mageride.shared.data.api.iam.IamApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import kotlin.time.Duration.Companion.milliseconds

/**
 * SCR-PA-008's state.
 *
 * @property query What is in the drop field.
 * @property predictions Geocoded places and the passenger's own saved/recent ones. **Never a
 *   route** — see the class KDoc.
 * @property searching A lookup is in flight; the wireframe's shimmer.
 * @property geocoderDown The lookup failed. The wireframe's *"Pick on map"* is the way out.
 * @property error Resolved copy for a failure worth naming, or `null`.
 */
internal data class SearchLocationState(
    val query: String = "",
    val predictions: List<GeocodedPlace> = emptyList(),
    val searching: Boolean = false,
    val geocoderDown: Boolean = false,
    @param:StringRes val error: Int? = null,
) {
    /** Whether the field is short enough that the screen shows saved/recent instead. */
    val showingDefaults: Boolean get() = query.trim().length < MIN_QUERY_LENGTH

    internal companion object {
        /**
         * How much has to be typed before a lookup goes out.
         *
         * Two characters is nothing to a geocoder and everything to a rate limit: Nominatim is
         * self-hosted (D-14) but shared with every other passenger in the country, and a request
         * per keystroke from the first one turns a search box into a load test.
         */
        const val MIN_QUERY_LENGTH = 3
    }
}

/**
 * SCR-PA-008 — the destination field.
 *
 * **GEO ONLY. A route number is not a destination (AL-17).** Typing `138` returns places called
 * "138" or nothing; it never returns a route row, and selecting a prediction always yields a
 * coordinate. This is a **fence**, and it is the one place in this app where a specification
 * disagrees with itself:
 *
 * - `D2' §SCR-PA-008` says the drop field *"accepts a destination place **or** a public route
 *   number (e.g. `138`)"* and that predictions *"blend matched public routes (bus/train) with
 *   Nominatim/Photon geocoded places"*, with a public-route row drawn in its own sketch.
 * - **AL-17, the wireframe and this component's prompt all say the opposite** — the wireframe's
 *   own state line reads *"route numbers are **not** accepted here … no route rows"*.
 *
 * The wireframe is the approved baseline and AL-17 is the later decision, so they win; D2'
 * §SCR-PA-008 needs a micro-change-set. Recorded in the C078 handoff, along with what that leaves
 * of US-7.9.
 *
 * **Once something is typed, the blend is the server's.** `GET /v1/geo/search` answers
 * `GeocodedPlace.source` of `nominatim`, `saved` or `recent`, so a query is one call rather than
 * three lists merged on the device — and the ★ row the wireframe draws among the 📍 ones is that
 * field, not a separate section. The **empty** state is the exception, and has to be: there is no
 * "search for nothing" request, so it is assembled here from §2.2's local recents and the saved
 * addresses.
 */
internal class SearchLocationViewModel(
    private val query: QueryApi,
    private val iam: IamApi,
    private val recents: RecentPlaces,
) : ViewModel() {

    private val mutableState = MutableStateFlow(SearchLocationState())
    private var lookup: Job? = null

    val state: StateFlow<SearchLocationState> = mutableState.asStateFlow()

    /** Where to bias the geocoder — the passenger's own position, set by the screen. */
    private var around: GeoPoint? = null

    init {
        viewModelScope.launch { loadDefaults() }
    }

    /** The map hands over where the passenger is, so results are ordered by what is near them. */
    fun biasAround(point: GeoPoint) {
        around = point
    }

    /**
     * A keystroke.
     *
     * Debounced rather than fired per character — see [SearchLocationState.MIN_QUERY_LENGTH]. The
     * previous lookup is cancelled, so a passenger typing quickly makes one request rather than
     * one per letter, and the answer that lands is always the one for what is on screen.
     */
    fun onQueryChanged(input: String) {
        mutableState.update { it.copy(query = input, error = null, geocoderDown = false) }
        lookup?.cancel()

        if (mutableState.value.showingDefaults) {
            lookup = viewModelScope.launch { loadDefaults() }
            return
        }

        lookup = viewModelScope.launch {
            delay(DEBOUNCE)
            search(input.trim())
        }
    }

    /** The wireframe's *"Pick on map"* fallback re-arms the field after a geocoder failure. */
    fun retry() {
        onQueryChanged(mutableState.value.query)
    }

    /**
     * A prediction was chosen.
     *
     * Writes §2.2's `place_recents` row, which is what puts the place in SCR-PA-010's *"Recent"*
     * list — this screen is the only writer, because "recent / searched locations" is what the
     * table is for and searching is what happens here. **Local-only**: nothing is sent anywhere.
     *
     * Fire-and-forget on `viewModelScope`, and deliberately not awaited by the caller: the
     * navigation to the next screen is the passenger's answer to their own tap, and it must not
     * wait on a SQLCipher write.
     */
    @Suppress("TooGenericExceptionCaught")
    fun onPlaceChosen(place: GeocodedPlace) {
        viewModelScope.launch {
            try {
                recents.remember(place)
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // A place the sheet will not remember. The passenger still gets where they chose.
            }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun search(text: String) {
        mutableState.update { it.copy(searching = true) }
        try {
            val places = query.searchPlaces(
                query = text,
                lat = around?.lat,
                lng = around?.lng,
                limit = RESULT_LIMIT,
            ).places
            mutableState.update { it.copy(predictions = places, searching = false) }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // The wireframe's "geocoder down → Pick on map". Not an error dialog: the passenger
            // still has a map and a pin, which is a better answer than a retry button.
            mutableState.update {
                it.copy(searching = false, geocoderDown = true, error = R.string.search_geocoder_down)
            }
        }
    }

    /**
     * The empty state — the passenger's saved addresses, as places.
     *
     * The wireframe's *"Empty → recents/saved"* — literally both, in that order. The recents are
     * §2.2's local table (this screen writes it, see [onPlaceChosen]) and the saved addresses are
     * `iam.users`; a recent that is also saved would be two rows for one place, so the saved ones
     * are filtered against what the recents already list.
     *
     * Once something IS typed, the blend is the server's: `GET /v1/geo/search` answers
     * `source = saved` and `source = recent` rows alongside the geocoded ones.
     */
    @Suppress("TooGenericExceptionCaught")
    private suspend fun loadDefaults() {
        val recent = runCatching { recents.recent() }.getOrDefault(emptyList())
        try {
            val saved = iam.listSavedAddresses().items.map { address ->
                GeocodedPlace(
                    lat = address.lat,
                    lng = address.lng,
                    displayName = address.label,
                    line1 = address.line1,
                    city = address.line3,
                    source = GeocodedPlaceSource.SAVED,
                )
            }
            val seen = recent.map { it.displayName }.toSet()
            mutableState.update {
                it.copy(predictions = recent + saved.filterNot { row -> row.displayName in seen }, searching = false)
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (_: Throwable) {
            // iam is unreachable; the local recents are still the passenger's own and still work.
            mutableState.update { it.copy(predictions = recent, searching = false) }
        }
    }

    private companion object {

        /**
         * How long the field rests before a lookup goes out.
         *
         * The wireframe says *"debounced"* and pins no number. 300 ms is the usual floor for
         * "stopped typing" without feeling laggy; anything shorter sends a request mid-word.
         */
        val DEBOUNCE = 300.milliseconds

        /** Enough rows to fill the list without scrolling into a second screen of guesses. */
        const val RESULT_LIMIT = 8
    }
}

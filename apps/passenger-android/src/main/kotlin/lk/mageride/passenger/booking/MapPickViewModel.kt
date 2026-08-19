package lk.mageride.passenger.booking

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
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.domain.geo.distanceMetres
import kotlin.time.Duration.Companion.milliseconds

/**
 * [MapPickSheet]'s state — a search box and a pin, over one map.
 *
 * @property query What is in the search field.
 * @property predictions What the geocoder answered. Drawn **over** the map rather than above it, so
 *   the sheet keeps its height and the CTA never leaves the screen.
 * @property searching A lookup is in flight.
 * @property geocoderDown The lookup failed. Not an error state for the sheet: the pin still works,
 *   which is the whole reason a map picker exists (AL-14 makes the same call about a reverse
 *   geocode being a pre-fill and never a gate).
 * @property centre Where the pin is — the map's centre, reported by `onCameraIdle`.
 * @property chosen The searched place the pin is currently sitting on, or `null` for a pin the
 *   passenger placed by hand. This is what gives the committed [Place] a **name**.
 * @property focus A camera move the sheet is asking for. `MageRideMap` applies it and the passenger
 *   sees the map fly to their search result.
 */
internal data class MapPickState(
    val query: String = "",
    val predictions: List<GeocodedPlace> = emptyList(),
    val searching: Boolean = false,
    val geocoderDown: Boolean = false,
    val centre: GeoPoint? = null,
    val chosen: GeocodedPlace? = null,
    val focus: GeoPoint? = null,
) {

    /**
     * What *"Use this location"* commits.
     *
     * **The coordinates are always the pin's and never the search result's.** They are the same
     * point until the passenger nudges the map, and after that the pin is what they are looking at
     * — a picker that committed the place they searched for rather than the spot they aimed at
     * would move the pickup after they had placed it. The NAME comes from [chosen], which
     * [MapPickViewModel.onPinMoved] drops as soon as the pin leaves it.
     */
    val selection: Place?
        get() = centre?.let { Place(lat = it.lat, lng = it.lng, address = chosen?.displayName) }

    /** Whether the field is too short to spend a geocoder request on. */
    val showingDefaults: Boolean get() = query.trim().length < MIN_QUERY_LENGTH

    internal companion object {

        /** The same floor SCR-PA-008 keeps, and for the same reason — see `SearchLocationState`. */
        const val MIN_QUERY_LENGTH = 3
    }
}

/**
 * The **Map** capture method, with a search box in it.
 *
 * **Why a search here at all, when SCR-PA-008 is one tap away on the same row.** The two answer
 * different questions. SCR-PA-008 turns a *name* into a coordinate and is done; this sheet is for a
 * pickup that has no name a geocoder knows — a lane, a gate, the third house past the junction —
 * and searching is how a passenger gets the map *near* it before placing the pin by eye. Landing on
 * the right junction and then dragging fifty metres is the gesture; without the search the only way
 * to reach a junction across town is to pan there.
 *
 * **A search result is a camera move, not a commitment.** Tapping a prediction moves the pin there
 * and names it; the passenger can then nudge the map, and the moment the pin leaves the named place
 * the name is dropped and the label falls back to the coordinates. What is committed is always
 * where the pin is — see [MapPickState.selection].
 *
 * Geo only, debounced, and biased toward the pin: the same three rules `SearchLocationViewModel`
 * keeps, for the same reasons (AL-17, and a self-hosted Nominatim shared with the whole country).
 */
internal class MapPickViewModel(private val query: QueryApi) : ViewModel() {

    private val mutableState = MutableStateFlow(MapPickState())
    private var lookup: Job? = null

    val state: StateFlow<MapPickState> = mutableState.asStateFlow()

    /**
     * The sheet was opened over [around].
     *
     * Called on every open rather than only on construction, because one instance of this model
     * serves both of SCR-PA-012's ends and SCR-PA-010b's pickup — a query left in the field from
     * the last thing the passenger captured would be somebody else's search.
     */
    fun opened(around: GeoPoint?) {
        lookup?.cancel()
        mutableState.value = MapPickState(centre = around)
    }

    /** A keystroke in the search field. Debounced; the previous lookup is cancelled. */
    fun onQueryChanged(input: String) {
        mutableState.update { it.copy(query = input, geocoderDown = false) }
        lookup?.cancel()

        if (mutableState.value.showingDefaults) {
            mutableState.update { it.copy(predictions = emptyList(), searching = false) }
            return
        }

        lookup = viewModelScope.launch {
            delay(DEBOUNCE)
            search(input.trim())
        }
    }

    /**
     * A prediction was tapped: fly the pin to it and remember what it is called.
     *
     * The list closes with it. It is drawn over the map, and a passenger who has just chosen where
     * to look wants to see the place rather than the list they left behind.
     */
    fun onPredictionChosen(place: GeocodedPlace) {
        val point = GeoPoint(lat = place.lat, lng = place.lng)
        mutableState.update {
            it.copy(predictions = emptyList(), chosen = place, focus = point, centre = point)
        }
    }

    /**
     * The map settled somewhere.
     *
     * **This is where a searched name stops being true.** The camera lands on the result within a
     * metre or so of what was asked for, so a small tolerance keeps the name through the settle
     * itself; a genuine pan past it means the pin is no longer on that place and the label goes
     * back to the coordinates. Clearing [MapPickState.focus] with it is what lets the same
     * prediction be tapped a second time and move the map again.
     */
    fun onPinMoved(point: GeoPoint) {
        mutableState.update { current ->
            val stillOnChosen = current.chosen
                ?.let { distanceMetres(GeoPoint(it.lat, it.lng), point) <= SETTLE_TOLERANCE_M }
                ?: false

            current.copy(
                centre = point,
                chosen = current.chosen.takeIf { stillOnChosen },
                focus = current.focus.takeIf { stillOnChosen },
            )
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun search(text: String) {
        mutableState.update { it.copy(searching = true) }
        try {
            val around = mutableState.value.centre
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
            // The pin is unaffected, so this is a line of text rather than a state: a passenger
            // with no geocoder can still place a pickup, which is what this sheet is for.
            mutableState.update { it.copy(predictions = emptyList(), searching = false, geocoderDown = true) }
        }
    }

    private companion object {

        /** SCR-PA-008's number. See `SearchLocationViewModel`. */
        val DEBOUNCE = 300.milliseconds

        /** A short list: it is drawn over a map the passenger is trying to look at. */
        const val RESULT_LIMIT = 5

        /**
         * How far the pin may sit from a searched place and still wear its name.
         *
         * Twenty-five metres is wider than the camera's own settling error and narrower than any
         * two addresses — a pin this close is on the place, and a pin further away is not.
         */
        const val SETTLE_TOLERANCE_M = 25.0
    }
}

package lk.mageride.passenger.booking

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place
import kotlin.time.Duration.Companion.seconds

/**
 * SCR-PA-012a's four states, as one type.
 *
 * D2' §SCR-PA-012a and D5' §BR-23.4 name them identically — *"Empty → Parsing ('Reading link…') →
 * Resolved → Error"* — and they are a sealed hierarchy rather than a set of booleans because the
 * sheet shows exactly one at a time and two of them carry data the others do not have.
 */
internal sealed interface PasteLinkState {

    /** Nothing pasted yet. The 📋 Paste affordance and the explanatory line. */
    data object Empty : PasteLinkState

    /** *"Reading link…"* — a short link is being followed by transit-svc. */
    data object Parsing : PasteLinkState

    /**
     * A point, and what it is called.
     *
     * @property address The reverse-geocoded name, or `null` while it is still being fetched — the
     *   pin preview and the coordinates are shown the instant the point is known, because those
     *   are the answer and the address is a courtesy.
     */
    data class Resolved(val point: GeoPoint, val address: String? = null) : PasteLinkState {
        /** What *"Use this location"* commits. */
        fun asPlace(): Place = Place(lat = point.lat, lng = point.lng, address = address)
    }

    /** *"Couldn't read that link — pick on map"*. Unparseable, or the resolve timed out. */
    data object Error : PasteLinkState
}

/**
 * SCR-PA-012a — a Google Maps link becomes a pin.
 *
 * **The device does the parsing and the server only follows redirects** (AL-20, D6' §I-23.1). Which
 * of the two happens is [MapsLink]'s answer, and it matters: a full URL resolves with no network at
 * all, which is the difference between a sheet that answers instantly and one that waits on a
 * roadside connection for a coordinate that was already in the string.
 *
 * **Three seconds, one retry, then the map.** D5' §BR-23.4 pins both numbers. The budget is real:
 * a passenger has already left WhatsApp, copied a link and come back, and a sheet that spins for
 * fifteen seconds before failing has spent more of their time than picking on the map would have.
 *
 * Opened from every Paste-link entry — SCR-PA-010b's proxy pickup and SCR-PA-012's package pickup
 * *and* drop-off — which is why it is a view model with a `Place` result rather than logic inside
 * any one of those screens.
 */
internal class PasteLinkViewModel(private val bookings: BookingRepository) : ViewModel() {

    private val mutableState = MutableStateFlow<PasteLinkState>(PasteLinkState.Empty)

    val state: StateFlow<PasteLinkState> = mutableState.asStateFlow()

    /** The sheet was reopened for another field. */
    fun reset() {
        mutableState.value = PasteLinkState.Empty
    }

    /**
     * Something was pasted.
     *
     * A full URL never touches the network: [MapsLinkParse.Resolved] short-circuits to the pin and
     * only the reverse-geocode (which the sheet can live without) goes out.
     */
    fun onPasted(text: String) {
        when (val parsed = MapsLink.parse(text)) {
            is MapsLinkParse.Resolved -> resolve(parsed.point)
            is MapsLinkParse.NeedsServer -> followShortLink(parsed.url)
            MapsLinkParse.Unreadable -> mutableState.value = PasteLinkState.Error
        }
    }

    /**
     * A short link — transit-svc follows the redirect.
     *
     * **One retry, because the failure this guards against is a single dropped request rather than
     * a service being down.** A second attempt after a 3 s timeout costs a passenger three more
     * seconds; a third would cost nine, by which point picking on the map is faster and the sheet
     * says so.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun followShortLink(url: String) {
        mutableState.value = PasteLinkState.Parsing
        viewModelScope.launch {
            val point = attemptResolve(url) ?: attemptResolve(url)
            if (point == null) {
                mutableState.value = PasteLinkState.Error
            } else {
                resolve(point)
            }
        }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun attemptResolve(url: String): GeoPoint? = try {
        withTimeout(RESOLVE_TIMEOUT) { bookings.parseMapsLink(url) }
    } catch (_: TimeoutCancellationException) {
        // `withTimeout` cancels with a CancellationException subclass, so this arm has to come
        // first — catching CancellationException below would swallow the view model's own
        // cancellation and rethrowing this one would fail the whole paste.
        null
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }

    /**
     * Shows the pin, then names it.
     *
     * The address is fetched *after* the state is published rather than before, so the preview
     * appears as soon as the coordinate is known. A reverse-geocode failure leaves the pin usable
     * with its coordinates showing, which is what SCR-PA-012a draws underneath the address anyway.
     */
    @Suppress("TooGenericExceptionCaught")
    private fun resolve(point: GeoPoint) {
        mutableState.value = PasteLinkState.Resolved(point)
        viewModelScope.launch {
            try {
                val named = bookings.reverseGeocode(point)
                mutableState.update { current ->
                    if (current is PasteLinkState.Resolved && current.point == point) {
                        current.copy(address = named.displayName)
                    } else {
                        current
                    }
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (_: Throwable) {
                // A pin with no name is still a pin, and its coordinates are on screen.
            }
        }
    }

    private companion object {
        /** D5' §BR-23.4's own budget for a short-link resolve. */
        val RESOLVE_TIMEOUT = 3.seconds
    }
}

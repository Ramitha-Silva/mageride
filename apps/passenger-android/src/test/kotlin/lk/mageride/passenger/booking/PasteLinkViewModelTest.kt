package lk.mageride.passenger.booking

import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.shared.data.models.GeoPoint
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds

/**
 * SCR-PA-012a's four states, and the Definition-of-Done line about *"an unparseable link offers
 * pick on map"*.
 *
 * The parsing itself is `MapsLinkTest`'s. What this asserts is the **routing**: which links never
 * touch the network, which ones go to transit-svc, and that a failure lands on Error rather than on
 * a spinner that never ends.
 */
class PasteLinkViewModelTest {

    private val main = MainDispatcher()
    private val bookings = FakeBookingRepository()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_full_url_resolves_without_touching_the_network() = runBlocking {
        // The whole reason AL-20 puts full URLs on the device: the coordinates are already in the
        // string, and a passenger who has just come back from WhatsApp on a roadside connection
        // should not wait on a round trip for something the app can read.
        val model = viewModel()

        model.onPasted("https://www.google.com/maps/place/X/@6.92,79.85,15z/data=!3d6.9344!4d79.8428")

        val resolved = assertIs<PasteLinkState.Resolved>(model.state.value)
        assertEquals(6.9344, resolved.point.lat)
        assertTrue(bookings.parsedLinks.isEmpty(), "no short-link resolve went out")
    }

    @Test
    fun a_short_link_goes_to_transit_svc() = runBlocking {
        // The coordinates are behind a redirect and following redirects is the server's job —
        // "no Google API", D6' §I-23.1.
        bookings.parsedPoint = GeoPoint(lat = 6.9344, lng = 79.8428)
        val model = viewModel()

        model.onPasted("https://maps.app.goo.gl/aBcDeFgH123")
        awaitResolved(model)

        assertEquals(listOf("https://maps.app.goo.gl/aBcDeFgH123"), bookings.parsedLinks)
    }

    @Test
    fun a_resolved_pin_is_shown_before_its_address_is_known() = runBlocking {
        // The coordinate is the answer and the address is a courtesy, so the preview appears the
        // instant the point is known rather than after a second round trip.
        bookings.parsedPoint = GeoPoint(lat = 6.9344, lng = 79.8428)
        val model = viewModel()

        model.onPasted("https://maps.app.goo.gl/aBcDeFgH123")
        val named = awaitResolved(model) { it.address != null }

        assertEquals("Colombo Fort", named.address)
        assertEquals(6.9344, named.point.lat)
    }

    @Test
    fun an_unreadable_link_offers_the_map() = runBlocking {
        // The Definition-of-Done line. Not a retry button: whatever this string is, reading it
        // again will not help, and the passenger still has a map and a pin.
        val model = viewModel()

        model.onPasted("have a look at this place")

        assertIs<PasteLinkState.Error>(model.state.value)
        assertTrue(bookings.parsedLinks.isEmpty())
    }

    @Test
    fun a_short_link_the_server_cannot_follow_ends_on_error_after_one_retry() = runBlocking {
        // D5' §BR-23.4 pins the retry count at one. A second attempt covers a single dropped
        // request; a third would spend nine seconds of a passenger's time before admitting that
        // picking on the map was faster.
        bookings.parseFails = IllegalStateException("410 gone")
        val model = viewModel()

        model.onPasted("https://goo.gl/maps/aBcDeFgH123")
        delay(SETTLE)

        assertIs<PasteLinkState.Error>(model.state.value)
        assertEquals(2, bookings.parsedLinks.size, "one attempt, then exactly one retry")
    }

    @Test
    fun reopening_the_sheet_for_another_field_starts_empty() = runBlocking {
        // The sheet is opened from three places — the proxy pickup and both package ends — and a
        // drop-off that arrived pre-filled with the pickup's pin would be a parcel sent to itself.
        bookings.parsedPoint = GeoPoint(lat = 6.9344, lng = 79.8428)
        val model = viewModel()
        model.onPasted("https://www.google.com/maps?q=6.9344,79.8428")
        assertIs<PasteLinkState.Resolved>(model.state.value)

        model.reset()

        assertEquals(PasteLinkState.Empty, model.state.value)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(PasteLinkViewModel(bookings))

    /**
     * Waits for the Resolved state.
     *
     * `Dispatchers.Unconfined` runs the launched coroutine eagerly up to its first real suspension,
     * so a short poll is enough and there is no clock to advance — the fake answers immediately.
     */
    private suspend fun awaitResolved(
        model: PasteLinkViewModel,
        predicate: (PasteLinkState.Resolved) -> Boolean = { true },
    ): PasteLinkState.Resolved {
        repeat(POLLS) {
            (model.state.value as? PasteLinkState.Resolved)?.takeIf(predicate)?.let { return it }
            delay(POLL_INTERVAL)
        }
        error("never resolved; last state was ${model.state.value}")
    }

    private companion object {
        val SETTLE = 200.milliseconds
        val POLL_INTERVAL = 20.milliseconds
        const val POLLS = 50
    }
}

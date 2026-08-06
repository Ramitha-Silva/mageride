package lk.mageride.passenger.booking

import kotlinx.coroutines.runBlocking
import lk.mageride.passenger.MainDispatcher
import lk.mageride.passenger.R
import lk.mageride.passenger.await
import lk.mageride.shared.data.api.IdempotencyKeyGenerator
import lk.mageride.shared.data.models.PackageSize
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.FareEstimateKind
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * SCR-PA-012 — a parcel instead of a person (US-20.1/20.2/20.8, P-06, P-07).
 *
 * Three things here are rules rather than layout, and all three are asserted: the pickup offers no
 * **Request** method because there is nobody standing there to ask, the size decides the vehicle
 * because P-06's hint has already promised it does, and the pickup OTP is shown once because the
 * server keeps only its hash.
 */
class PackageBookingViewModelTest {

    private val main = MainDispatcher()
    private val bookings = FakeBookingRepository()
    private val draft = BookingDraft()
    private val keys = IdempotencyKeyGenerator { CLIENT_REQUEST_ID }

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_pickup_end_refuses_the_request_method() = runBlocking {
        // The fence, and it is enforced rather than only left off the chip row: the sender is
        // standing at the pickup, so "ask somebody where this is" has no one to ask. The drop-off
        // is the opposite case — the recipient is the only person who knows.
        val model = viewModel()

        model.setMethod(PackageEnd.PICKUP, PickupMethod.REQUEST)
        assertEquals(PickupMethod.SEARCH, model.state.value.pickupMethod, "unchanged")

        model.setMethod(PackageEnd.DROPOFF, PickupMethod.REQUEST)
        assertEquals(PickupMethod.REQUEST, model.state.value.dropoffMethod)
    }

    @Test
    fun the_size_hint_changes_with_the_size() = runBlocking {
        // P-06 / change619 #2: "the hint updates per pick". It is what stops a sender choosing S
        // for a fridge and a motorbike arriving.
        val model = viewModel()
        assertEquals(R.string.package_hint_s, model.state.value.sizeHint)

        model.setSize(PackageSize.M)
        assertEquals(R.string.package_hint_m, model.state.value.sizeHint)

        model.setSize(PackageSize.L)
        assertEquals(R.string.package_hint_l, model.state.value.sizeHint)
    }

    @Test
    fun the_size_decides_the_vehicle_that_is_quoted() = runBlocking {
        // The smallest vehicle the size fits, which is the cheapest honest answer — and the same
        // one the hint has already named, so the price matches what the sender was told to expect.
        val model = fullyFilled()
        model.setSize(PackageSize.L)

        model.estimate()
        model.state.await { it.estimateMinor != null }

        assertEquals(RideVehicleType.VAN to FareEstimateKind.PACKAGE, bookings.estimated.last())
    }

    @Test
    fun changing_anything_that_affects_the_price_invalidates_the_quote() = runBlocking {
        // A token binds a price to a journey. Keeping one after the size or an end changed would
        // book a fridge at a documents-folder fare — which the server would refuse anyway
        // (`400 invalid-fare-token`), after the passenger had already tapped Book.
        val model = fullyFilled()
        model.estimate()
        model.state.await { it.estimateMinor != null }

        model.setSize(PackageSize.M)
        assertNull(model.state.value.estimateMinor)
        assertNull(model.state.value.quoteToken)
        assertFalse(model.state.value.canBook)

        model.estimate()
        model.state.await { it.estimateMinor != null }
        model.setPlace(PackageEnd.DROPOFF, NUGEGODA)
        assertNull(model.state.value.quoteToken, "moving an end re-prices it too")
    }

    @Test
    fun booking_carries_the_parcel_the_recipient_and_the_package_kind() = runBlocking {
        val model = fullyFilled()
        model.setPaymentMethod(RidePaymentMethod.COD)
        model.estimate()
        model.state.await { it.estimateMinor != null }

        model.book()
        model.state.await { it.booked != null }

        val sent = bookings.requested.single()
        assertEquals(RideKind.PACKAGE, sent.kind)
        assertEquals(PackageSize.S, sent.packageSize)
        assertEquals("Documents folder", sent.packageDescription)
        assertEquals("Sunethra", sent.recipientName)
        // E.164 on the wire, whatever the sender typed — the recipient is called on this number.
        assertEquals("+94712223344", sent.recipientPhone)
        // US-20.8: COD is a booking-time method and a package-only one (AL-22).
        assertEquals(RidePaymentMethod.COD, sent.paymentMethod)
        assertEquals(CLIENT_REQUEST_ID, sent.clientRequestId)
    }

    @Test
    fun the_pickup_otp_is_held_until_the_screen_has_shown_it() = runBlocking {
        // P-07: `pickupOtp` comes back on exactly one response and the server stores only its
        // hash. A view model that dropped it on the way to the next screen would have destroyed
        // the only copy the sender will ever get.
        val model = fullyFilled()
        model.estimate()
        model.state.await { it.estimateMinor != null }

        model.book()
        val state = model.state.await { it.booked != null }

        assertEquals("4829", state.pickupOtp)

        model.onBookingConsumed()
        assertNull(model.state.value.pickupOtp, "and it is gone once the screen has shown it")
    }

    @Test
    fun an_incomplete_parcel_can_neither_be_quoted_nor_booked() = runBlocking {
        // Both ends, a description, and a reachable recipient. A parcel with no recipient number
        // is a driver at a door with nobody to call.
        val model = viewModel()
        model.onDescriptionChanged("Documents folder")
        model.setPlace(PackageEnd.PICKUP, COLOMBO)
        model.setPlace(PackageEnd.DROPOFF, NUGEGODA)

        assertFalse(model.state.value.canEstimate, "no recipient yet")
        model.estimate()
        assertTrue(bookings.estimated.isEmpty())

        model.onRecipientNameChanged("Sunethra")
        model.onRecipientPhoneChanged("0712223344")

        assertTrue(model.state.value.canEstimate)
    }

    // ------------------------------------------------------------------------------------------

    private fun viewModel() = main.own(
        PackageBookingViewModel(draft = draft, bookings = bookings, keys = keys),
    )

    /** Everything the wireframe's form asks for, so a test can get to the estimate in one line. */
    private fun fullyFilled(): PackageBookingViewModel = viewModel().apply {
        onDescriptionChanged("Documents folder")
        onRecipientNameChanged("Sunethra")
        onRecipientPhoneChanged("0712223344")
        setPlace(PackageEnd.PICKUP, COLOMBO)
        setPlace(PackageEnd.DROPOFF, NUGEGODA)
    }

    private companion object {
        val COLOMBO = Place(lat = 6.9344, lng = 79.8428, address = "Colombo Fort")
        val NUGEGODA = Place(lat = 6.8649, lng = 79.8997, address = "Nugegoda")

        const val CLIENT_REQUEST_ID = "01JREQ00000000000000000002"
    }
}

package lk.mageride.passenger.ride

import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The Definition-of-Done line *"no surcharge is ever displayed on a ride — the +5 % died with the
 * OnePay ride rail (AL-57)"*.
 *
 * Asserted where a surcharge could only ever come from: **the list of rails**. The +5 % was
 * OnePay's, OnePay is gone, and nothing that survives has one — so a test that pins the surviving
 * set is a test that pins the absence of a surcharge, and it fails the moment somebody puts a
 * retired rail back.
 */
class PaymentRailsTest {

    @Test
    fun a_ride_offers_exactly_the_three_rails_that_survive() {
        // AL-57 removed `onepay` (one merchant account per merchant, so a card fare could only land
        // in MageRide's own account — card acceptance moved to the wallet top-up, where MageRide
        // legitimately is the payee). AL-59 removed the platform-merchant `lankaqr` (it collected
        // into the platform account and credited the driver a read-model row).
        assertEquals(
            listOf(PaymentMethod.CASH, PaymentMethod.WALLET, PaymentMethod.SCAN_DRIVER_QR),
            PaymentRails.RIDE,
        )
    }

    @Test
    fun neither_retired_rail_can_be_offered_on_any_screen() {
        // The two lists are the whole surface — SCR-PA-016 renders `RIDE`, SCR-PA-012 renders
        // `PACKAGE`, and C079's booking chip renders `RIDE`. If neither contains a retired rail,
        // no screen in the app can draw one, and no screen can draw a surcharge.
        PaymentRails.RETIRED.forEach { retired ->
            assertFalse(retired in PaymentRails.RIDE, "$retired is retired and must not be offered")
            assertFalse(retired in PaymentRails.PACKAGE, "$retired is retired and must not be offered")
        }
        assertEquals(setOf(PaymentMethod.ONEPAY, PaymentMethod.LANKAQR), PaymentRails.RETIRED)
    }

    @Test
    fun cod_is_the_one_rail_a_parcel_adds() {
        // US-20.8. A passenger ride offering COD would be `400 payment-method-invalid`, which is
        // why the two lists differ by exactly this one entry rather than being one list with a flag.
        assertEquals(PaymentRails.RIDE + PaymentMethod.COD, PaymentRails.PACKAGE)
        assertTrue(PaymentMethod.COD !in PaymentRails.RIDE)
    }

    @Test
    fun a_booking_records_cod_for_a_parcel_and_cash_for_everything_else() {
        // The gap, made explicit. `ride.yaml`'s booking-time enum still declares
        // `[cash, lankaqr, onepay, cod]` while `fare.yaml`'s settlement-time one is
        // `[cash, wallet, scan_driver_qr, cod]` — the AL-57/AL-59 change set updated the payment
        // column and left the booking column behind. There is no booking-time value that means
        // "I intend to pay by wallet", so the booking says cash and SCR-PA-016 asks again.
        assertEquals(RidePaymentMethod.COD, PaymentRails.bookingValueOf(PaymentMethod.COD))
        assertEquals(RidePaymentMethod.CASH, PaymentRails.bookingValueOf(PaymentMethod.CASH))
        assertEquals(RidePaymentMethod.CASH, PaymentRails.bookingValueOf(PaymentMethod.WALLET))
        assertEquals(RidePaymentMethod.CASH, PaymentRails.bookingValueOf(PaymentMethod.SCAN_DRIVER_QR))
    }

    @Test
    fun a_retired_rail_never_resolves_to_a_rail_name() {
        // `PaymentMethod` still declares both because `:shared` types the whole
        // `fares.ride_payments.method` domain, including historical rows a trip history might show.
        // What must not happen is one of them rendering as an offer.
        assertEquals(PaymentRails.label(PaymentMethod.ONEPAY), PaymentRails.label(PaymentMethod.LANKAQR))
        assertTrue(PaymentRails.label(PaymentMethod.CASH) != PaymentRails.label(PaymentMethod.ONEPAY))
    }
}

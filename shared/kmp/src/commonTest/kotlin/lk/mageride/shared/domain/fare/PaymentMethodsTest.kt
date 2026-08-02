package lk.mageride.shared.domain.fare

import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.fare.DriverQrInitiation
import lk.mageride.shared.data.models.fare.PaymentInitiation
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue

/**
 * The method rules: the OnePay surcharge (US-8.11), proxy payer routing (P-04), COD's package
 * fence (P-08), the tip (E-10), and the two presentation fences AL-15 and AL-22.
 */
class PaymentMethodsTest {

    private fun initiation(method: PaymentMethod, driverQr: DriverQrInitiation? = null) = PaymentInitiation(
        paymentId = "01JPAY0000000000000000000",
        state = PaymentState.Initiated,
        method = method,
        amountMinor = 48_000,
        driverQr = driverQr,
    )

    // ----------------------------------------------------------------------------------------
    // US-8.11 — OnePay adds 5%, and nothing else adds anything
    // ----------------------------------------------------------------------------------------

    @Test
    fun only_onepay_carries_a_surcharge() {
        assertEquals(2_400L, PaymentMethods.surchargeMinor(PaymentMethod.ONEPAY, 48_000))
        assertEquals(0L, PaymentMethods.surchargeMinor(PaymentMethod.LANKAQR, 48_000))
        assertEquals(0L, PaymentMethods.surchargeMinor(PaymentMethod.CASH, 48_000))

        // AL-47: driver-QR is bank-to-bank with zero commission — the platform is not in the
        // transaction at all, so there is nothing to surcharge.
        assertEquals(0L, PaymentMethods.surchargeMinor(PaymentMethod.SCAN_DRIVER_QR, 48_000))
    }

    @Test
    fun the_surcharge_rounds_the_way_every_other_percentage_does() {
        // Rs 80.10 at 5% is 400.5 minor units — the tie — and banker's rounding gives 400.
        assertEquals(400L, PaymentMethods.surchargeMinor(PaymentMethod.ONEPAY, 8_010))
        assertEquals(5, PaymentMethods.ONEPAY_SURCHARGE_PCT)
    }

    @Test
    fun a_tip_and_a_carried_penalty_are_added_but_never_surcharged() {
        // A passenger tipping Rs 100 through OnePay must not be charged Rs 105 for the privilege,
        // and the Rs 50 cross-trip penalty (D-05) is a transfer to the stood-up driver rather than
        // a gateway service.
        val due = PaymentMethods.amountDue(
            fare = Money.ofMinor(48_000),
            method = PaymentMethod.ONEPAY,
            tip = Money.ofMinor(10_000),
            carriedPenalty = Money.ofMinor(5_000),
        )

        assertEquals(48_000L + 2_400L + 10_000L + 5_000L, due.amountMinor)
    }

    @Test
    fun a_cash_fare_with_no_tip_is_the_fare() {
        assertEquals(
            48_000L,
            PaymentMethods.amountDue(Money.ofMinor(48_000), PaymentMethod.CASH).amountMinor,
        )
    }

    // ----------------------------------------------------------------------------------------
    // P-04 — who is charged on a proxy booking
    // ----------------------------------------------------------------------------------------

    @Test
    fun cash_is_paid_by_the_rider_and_an_account_method_by_the_booker() {
        // "Cash ⇒ rider pays driver; LankaQR/OnePay ⇒ booker charged, regardless of who is at
        // pickup."
        assertEquals(PayerRole.RIDER, PaymentMethods.payerRole(PaymentMethod.CASH))
        assertEquals(PayerRole.BOOKER, PaymentMethods.payerRole(PaymentMethod.LANKAQR))
        assertEquals(PayerRole.BOOKER, PaymentMethods.payerRole(PaymentMethod.ONEPAY))

        // P-04 predates AL-22 and does not route this one. Scanning is a kerbside act — the payer
        // has to be standing in front of the driver's QR — which is P-04's own reasoning for cash.
        assertEquals(PayerRole.RIDER, PaymentMethods.payerRole(PaymentMethod.SCAN_DRIVER_QR))
    }

    @Test
    fun cod_is_collected_from_whoever_takes_delivery() {
        assertEquals(PayerRole.RIDER, PaymentMethods.payerRole(RidePaymentMethod.COD))
        assertEquals(PayerRole.RIDER, PaymentMethods.payerRole(RidePaymentMethod.CASH))
        assertEquals(PayerRole.BOOKER, PaymentMethods.payerRole(RidePaymentMethod.ONEPAY))
    }

    // ----------------------------------------------------------------------------------------
    // P-08 — COD is package-only
    // ----------------------------------------------------------------------------------------

    @Test
    fun cod_is_offered_for_a_package_and_for_nothing_else() {
        assertTrue(PaymentMethods.isOfferedFor(RidePaymentMethod.COD, RideKind.PACKAGE))
        assertFalse(PaymentMethods.isOfferedFor(RidePaymentMethod.COD, RideKind.PASSENGER))
        assertFalse(PaymentMethods.isOfferedFor(RidePaymentMethod.COD, RideKind.PROXY))

        RideKind.entries.forEach {
            assertTrue(PaymentMethods.isOfferedFor(RidePaymentMethod.CASH, it))
            assertTrue(PaymentMethods.isOfferedFor(RidePaymentMethod.LANKAQR, it))
        }
    }

    @Test
    fun a_cod_package_settles_on_delivery_and_offers_no_settlement_choice() {
        assertTrue(PaymentMethods.settlementMethodsFor(RidePaymentMethod.COD).isEmpty())
        assertTrue(
            PaymentMethod.SCAN_DRIVER_QR in PaymentMethods.settlementMethodsFor(RidePaymentMethod.CASH),
            "a cash booking may still be settled by scanning the driver's QR (AL-22)",
        )
    }

    // ----------------------------------------------------------------------------------------
    // AL-15 / AL-22 — what the pay sheet puts on screen
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_al_15_deep_link_rule_survives_on_the_top_up_sheet() {
        // Δ AL-57 — LankaQR is gone as a RIDE rail, so `actionFor` no longer reaches this. The
        // rule itself is unchanged and still applies to the wallet top-up, which faces the same
        // choice, so it is exercised through the public overload the top-up sheet calls.
        assertEquals(
            FarePaymentAction.OpenBankApp("lankaqr://pay?x=1"),
            PaymentMethods.lankaQrAction("lankaqr://pay?x=1", "0002010102", bankAppAvailable = true),
        )
        assertEquals(
            FarePaymentAction.ShowLankaQrFallback("0002010102"),
            PaymentMethods.lankaQrAction("lankaqr://pay?x=1", "0002010102", bankAppAvailable = false),
        )
    }

    @Test
    fun a_top_up_with_nothing_usable_is_reported_rather_than_guessed_at() {
        assertEquals(FarePaymentAction.Unavailable, PaymentMethods.lankaQrAction(null, null, true))
    }

    @Test
    fun paying_by_driver_qr_opens_the_camera_and_renders_no_mageride_code() {
        // AL-22: "the app no longer renders a QR in the centre — it offers a camera scan". There is
        // no `ShowMageRideQr` action to return, which is the fence.
        val action = PaymentMethods.actionFor(initiation(PaymentMethod.SCAN_DRIVER_QR))

        assertEquals(FarePaymentAction.ScanDriverQr, action)
    }

    @Test
    fun a_retired_ride_rail_has_no_sheet_to_open() {
        // Δ AL-57/AL-59. fare-svc's `PaymentMethod` is now `[cash, wallet, scan_driver_qr, cod]`,
        // so neither value can come back on a real initiation. They survive in this enum because
        // `ride.yaml` and `iam.yaml` still declare them — see the MCS-02 handoff.
        assertEquals(FarePaymentAction.Unavailable, PaymentMethods.actionFor(initiation(PaymentMethod.ONEPAY)))
        assertEquals(FarePaymentAction.Unavailable, PaymentMethods.actionFor(initiation(PaymentMethod.LANKAQR)))
    }

    @Test
    fun cash_needs_nothing_on_screen() {
        assertEquals(FarePaymentAction.CollectCash, PaymentMethods.actionFor(initiation(PaymentMethod.CASH)))
    }
}

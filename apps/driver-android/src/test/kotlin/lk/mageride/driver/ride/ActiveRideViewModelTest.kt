package lk.mageride.driver.ride

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.home.FakeDriverLocationSource
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.PaymentState
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.fare.PaymentMethod
import lk.mageride.shared.data.models.fare.PaymentStatus
import lk.mageride.shared.data.models.ride.CompleteRideResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import lk.mageride.shared.data.models.ride.RideStateChange
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-015 — arrive, start, complete, and AL-47's settlement.
 *
 * The DoD line these carry: *"the QR confirm sheet posts driver-qr/confirm and reflects
 * DriverConfirmedQR"*.
 */
class ActiveRideViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val location = FakeDriverLocationSource()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun the_cta_walks_accepted_to_arrived_to_in_progress_to_completed() = runBlocking {
        backend.returns("getRide", ride(RideState.Accepted))
        backend.returns("markDriverArrived", moved(RideState.DriverArrived, version = 2))
        backend.returns("startRide", moved(RideState.InProgress, version = 3))
        backend.returns(
            "completeRide",
            CompleteRideResponse(rideId = Fixtures.RIDE_ID, state = RideState.PaymentPending, version = 4),
        )

        val model = viewModel()
        model.state.await { it.rideState == RideState.Accepted }

        model.advance()
        model.state.await { it.rideState == RideState.DriverArrived }

        // DriverArrived does NOT send: it raises the OTP entry, because a ride starts against the
        // rider's four digits and nothing else (P-07).
        model.advance()
        assertEquals(RideSheet.START_OTP, model.state.value.sheet)
        assertFalse(backend.called("startRide"))

        model.startRide(Fixtures.OTP)
        model.state.await { it.rideState == RideState.InProgress }

        model.advance()
        model.state.await { it.rideState == RideState.PaymentPending }

        // Every mutation echoed the version the screen was showing (R-14) — never a bumped one.
        assertEquals("1", backend.lastCall("markDriverArrived").json["version"].toString())
        assertEquals("3", backend.lastCall("completeRide").json["version"].toString())
    }

    @Test
    fun a_driver_qr_ride_raises_the_confirm_sheet_instead_of_finishing() = runBlocking {
        // AL-47. The money moved bank-to-bank into the driver's own LankaQR account and no gateway
        // callback exists, so `PaymentPending` is where the ride waits for the driver's word.
        backend.returns("getRide", ride(RideState.InProgress, method = RidePaymentMethod.LANKAQR))
        backend.returns(
            "completeRide",
            CompleteRideResponse(rideId = Fixtures.RIDE_ID, state = RideState.PaymentPending, version = 4),
        )

        val model = viewModel()
        model.state.await { it.rideState == RideState.InProgress }

        model.advance()
        val state = model.state.await { it.rideState == RideState.PaymentPending }

        assertTrue(state.settlesByDriverQr)
        assertTrue(state.awaitingQrConfirm)
        assertEquals(RideSheet.QR_CONFIRM, state.sheet)
        assertFalse(state.finished, "a completed trip with no settled payment is not over")
    }

    @Test
    fun confirming_posts_driver_qr_confirm_and_reflects_driver_confirmed_qr() = runBlocking {
        backend.returns("getRide", ride(RideState.PaymentPending, method = RidePaymentMethod.LANKAQR))
        backend.returns("confirmDriverQrPayment", payment(PaymentState.DriverConfirmedQR))

        val model = viewModel()
        model.state.await { it.rideState == RideState.PaymentPending }
        assertEquals(RideSheet.QR_CONFIRM, model.state.value.sheet)

        model.confirmQrPayment(received = true)
        model.state.await { it.finished }

        assertTrue(backend.called("confirmDriverQrPayment"))
        assertEquals(
            "\"${Fixtures.RIDE_ID}\"",
            backend.lastCall("confirmDriverQrPayment").json["rideId"].toString(),
        )
    }

    @Test
    fun the_confirm_sheet_does_not_come_back_while_the_ride_waits_for_settlement() = runBlocking {
        // `DriverConfirmedQR` is terminal on the PAYMENT machine, but the ride stays at
        // `PaymentPending` until fare-svc's settlement reaches ride-svc through the outbox. Re-
        // reading into that window must not put the sheet back up: a driver cannot attest twice,
        // and being asked again reads as the first answer not having been taken.
        backend.returns("getRide", ride(RideState.PaymentPending, method = RidePaymentMethod.LANKAQR))
        backend.returns("confirmDriverQrPayment", payment(PaymentState.DriverConfirmedQR))

        val model = viewModel()
        model.state.await { it.sheet == RideSheet.QR_CONFIRM }

        model.confirmQrPayment(received = true)
        model.state.await { it.finished }

        model.refresh()
        model.state.await { it.rideState == RideState.PaymentPending && !it.busy }

        assertTrue(model.state.value.qrAttested)
        assertFalse(model.state.value.awaitingQrConfirm, "already answered")
        assertEquals(null, model.state.value.sheet)
        assertTrue(model.state.value.finished, "terminal is terminal — a ride never un-finishes")
    }

    @Test
    fun not_received_opens_a_dispute_and_moves_no_money() = runBlocking {
        // The ticket is routed Support → Finance. No wallet movement: the platform takes no
        // commission on this path and holds none of the money, so there is nothing to reverse.
        backend.returns("getRide", ride(RideState.PaymentPending, method = RidePaymentMethod.LANKAQR))

        val model = viewModel()
        model.state.await { it.rideState == RideState.PaymentPending }

        model.confirmQrPayment(received = false)
        model.state.await { it.finished }

        assertTrue(backend.called("disputeDriverQrPayment"))
        assertFalse(backend.called("confirmDriverQrPayment"))
    }

    @Test
    fun a_cash_ride_never_raises_the_qr_sheet() = runBlocking {
        // The attestation exists because no callback can settle a driver's own bank QR. A cash
        // ride settles the ordinary way and must not ask the driver to vouch for anything.
        backend.returns("getRide", ride(RideState.PaymentPending, method = RidePaymentMethod.CASH))

        val model = viewModel()
        model.state.await { it.rideState == RideState.PaymentPending }

        assertFalse(model.state.value.settlesByDriverQr)
        assertFalse(model.state.value.awaitingQrConfirm)
        assertEquals(null, model.state.value.sheet)
    }

    private fun ride(state: RideState, method: RidePaymentMethod = RidePaymentMethod.CASH) = RideDetail(
        rideId = Fixtures.RIDE_ID,
        kind = RideKind.PASSENGER,
        state = state,
        version = 1,
        pickup = Fixtures.PICKUP,
        dropoff = Fixtures.DROPOFF,
        vehicleType = RideVehicleType.THREE_WHEELER,
        paymentMethod = method,
        counterpartyPhone = Fixtures.PASSENGER_PHONE,
        createdAt = Fixtures.NOW,
    )

    private fun moved(state: RideState, version: Int) =
        RideStateChange(rideId = Fixtures.RIDE_ID, state = state, version = version)

    private fun payment(state: PaymentState) = PaymentStatus(
        paymentId = Fixtures.TRANSACTION_ID,
        rideId = Fixtures.RIDE_ID,
        state = state,
        method = PaymentMethod.SCAN_DRIVER_QR,
        amountMinor = Fixtures.FARE.amountMinor,
        currency = Currency.LKR,
    )

    private fun viewModel(): ActiveRideViewModel {
        val api = backend.mageRideApi()
        return ActiveRideViewModel(
            rideId = Fixtures.RIDE_ID,
            rides = ActiveRideRepository(ride = api.ride, fare = api.fare),
            contact = RideContact(voip = api.voip, safety = api.safety),
            location = location,
        )
    }
}

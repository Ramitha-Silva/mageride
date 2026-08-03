package lk.mageride.driver.delivery

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.R
import lk.mageride.driver.home.FakeDriverLocationSource
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.onboarding.testImage
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.ride.ProofArtifactResponse
import lk.mageride.shared.domain.ride.PackageHandoff
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fixture.Fixtures
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-016a/b/c — the three delivery sheets over the package ride machine (AL-33).
 *
 * The DoD lines these carry: *"a correct pickup OTP advances to sheet 3 and notifies the
 * recipient"*, *"a 6th wrong OTP shows the locked state and the admin-queue message"* and
 * *"Cancel on sheet 1 re-dispatches and returns the driver to standby"*.
 */
class DeliveryViewModelTest {

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
    fun the_sheets_run_review_then_pickup_then_complete() = runBlocking {
        backend.returns("getRide", packageRide(RideState.Accepted))
        backend.returns("verifyPackagePickupOtp", movedTo(RideState.InProgress, version = 2))

        val model = viewModel()
        model.state.await { it.ride != null }

        assertEquals(DeliverySheet.REVIEW, model.state.value.sheet)

        // Sheet 1's Start delivery sends nothing: the parcel is released by the sender's code, and
        // D5' §11 takes a package straight from Accepted to InProgress with no arrival marker.
        model.advance()
        assertEquals(DeliverySheet.PICKUP, model.state.value.sheet)
        assertFalse(backend.called("markDriverArrived"))

        model.onOtpChange(Fixtures.OTP)
        model.advance()
        val delivered = model.state.await { it.sheet == DeliverySheet.COMPLETE }

        // `package.picked_up` is what notifies the recipient — the same event carries their own
        // code out (AL-21, US-20.5), which is why the client makes this call and no other.
        assertTrue(backend.called("verifyPackagePickupOtp"))
        assertEquals("\"${Fixtures.OTP}\"", backend.lastCall("verifyPackagePickupOtp").json["otp"].toString())
        assertEquals(RideState.InProgress, delivered.rideState)
        assertEquals("", delivered.otp, "the boxes are cleared for the recipient's code")
    }

    @Test
    fun the_delivery_otp_hands_the_parcel_over_and_returns_the_driver_to_standby() = runBlocking {
        backend.returns("getRide", packageRide(RideState.InProgress, version = 2))
        backend.returns("verifyPackageDeliveryOtp", movedTo(RideState.Completed, version = 3))

        val model = viewModel()
        model.state.await { it.sheet == DeliverySheet.COMPLETE }

        model.onOtpChange(Fixtures.OTP)
        model.advance()
        val done = model.state.await { it.finished }

        assertTrue(backend.called("verifyPackageDeliveryOtp"))
        assertTrue(done.handedOver)

        // AL-33 decoupled the cash from the handover: "Delivery completed" REPLACES "Cash received
        // (COD)", and an uncollected COD is a 24-hour timer's problem (P-14), not this button's.
        assertFalse(backend.called("confirmCashOnDelivery"))
    }

    @Test
    fun the_fifth_wrong_code_locks_the_gate_and_names_the_admin_queue() = runBlocking {
        backend.returns("getRide", packageRide(RideState.DriverArrived))
        backend.fails("verifyPackagePickupOtp", HttpStatusCode.BadRequest, "invalid-otp")

        val model = viewModel()
        model.state.await { it.sheet == DeliverySheet.PICKUP }

        repeat(PackageHandoff.MAX_OTP_ATTEMPTS) { attempt ->
            model.onOtpChange(WRONG_OTP)
            model.advance()
            model.state.await { it.attemptsUsed == attempt + 1 }
        }

        // The attempt that SPENDS the budget is the one that raises the queue item — ride-svc says
        // so, and `PackageHandoff` locks on the same count. So the sixth is never sent: the screen
        // is already showing the admin-queue message, and the entry is disabled behind it.
        val locked = model.state.value
        assertTrue(locked.locked)
        assertEquals(0, locked.attemptsRemaining)
        assertEquals(R.string.delivery_otp_wrong, locked.error)
        assertFalse(locked.canAdvance, "there is no sixth box")

        val spent = backend.callsTo("verifyPackagePickupOtp").size
        model.onOtpChange(WRONG_OTP)
        model.advance()
        assertEquals(spent, backend.callsTo("verifyPackagePickupOtp").size, "a locked gate sends nothing")
    }

    @Test
    fun a_server_lockout_locks_the_gate_even_with_attempts_left_on_this_device() = runBlocking {
        // The server's count is authoritative because it survives what this one does not: a
        // reinstall, or a driver resuming the same handoff on a second handset.
        backend.returns("getRide", packageRide(RideState.DriverArrived))
        backend.fails("verifyPackagePickupOtp", HttpStatusCode.Locked, "otp-locked")

        val model = viewModel()
        model.state.await { it.sheet == DeliverySheet.PICKUP }

        model.onOtpChange(WRONG_OTP)
        model.advance()
        val locked = model.state.await { it.locked }

        assertEquals(R.string.delivery_otp_locked, locked.error)
        assertEquals(0, locked.attemptsRemaining, "the server says the budget is gone, so it is")
    }

    @Test
    fun a_malformed_code_is_refused_without_spending_an_attempt() = runBlocking {
        backend.returns("getRide", packageRide(RideState.DriverArrived))

        val model = viewModel()
        model.state.await { it.sheet == DeliverySheet.PICKUP }

        model.onOtpChange("48")
        model.advance()

        assertFalse(backend.called("verifyPackagePickupOtp"), "a typo the client can see is not a guess")
        assertEquals(PackageHandoff.MAX_OTP_ATTEMPTS, model.state.value.attemptsRemaining)
    }

    @Test
    fun cancel_on_sheet_one_releases_the_job_and_returns_the_driver_to_standby() = runBlocking {
        backend.returns("getRide", packageRide(RideState.Accepted, version = 7))

        val model = viewModel()
        // The review sheet is what an unread screen already shows, so wait for the ride itself —
        // Cancel has nothing to release until there is a version to echo.
        model.state.await { it.ride != null && it.sheet == DeliverySheet.REVIEW }

        model.cancel()
        model.state.await { it.finished }

        assertTrue(backend.called("cancelRide"))

        // R-14: the version echoed is the one the sheet was showing, never a bumped one.
        assertEquals("7", backend.lastCall("cancelRide").json["version"].toString())
    }

    @Test
    fun a_photo_completes_the_delivery_when_the_recipient_is_absent() = runBlocking {
        backend.returns("getRide", packageRide(RideState.InProgress, version = 4))
        backend.returns(
            "uploadPackageProofPhoto",
            ProofArtifactResponse(artifactId = Fixtures.RIDE_ID, state = RideState.Completed, version = 5),
        )

        val proofs = ProofUploadQueue()
        val model = viewModel(proofs)
        model.state.await { it.sheet == DeliverySheet.COMPLETE }

        // P-10. No delivery OTP is typed at all — there is nobody there to read one out.
        model.attachProof(testImage("delivery-proof.jpg"))
        model.state.await { it.proof != null }
        model.advance()
        val done = model.state.await { it.finished }

        assertTrue(backend.called("uploadPackageProofPhoto"))
        assertFalse(backend.called("verifyPackageDeliveryOtp"))
        assertTrue(done.handedOver)
        assertEquals(null, proofs.pendingFor(Fixtures.RIDE_ID), "an uploaded proof is not kept (§4.3)")
    }

    @Test
    fun a_failed_upload_keeps_the_photograph_so_a_retry_needs_no_camera() = runBlocking {
        backend.returns("getRide", packageRide(RideState.InProgress, version = 4))
        backend.fails("uploadPackageProofPhoto", HttpStatusCode.ServiceUnavailable, "server-error")

        val proofs = ProofUploadQueue()
        val model = viewModel(proofs)
        model.state.await { it.sheet == DeliverySheet.COMPLETE }

        model.attachProof(testImage("delivery-proof.jpg"))
        model.state.await { it.proof != null }
        model.advance()
        model.state.await { it.error != null }

        assertFalse(model.state.value.finished, "a delivery that did not upload is not delivered")
        assertEquals(1, proofs.pendingFor(Fixtures.RIDE_ID)?.attempts)
        assertTrue(model.state.value.proof != null, "the driver must not have to re-photograph a door")
    }

    @Test
    fun each_call_button_dials_its_own_end_of_the_delivery() = runBlocking {
        backend.returns("getRide", packageRide(RideState.Accepted))

        val model = viewModel()
        model.state.await { it.ride != null }

        model.call(DeliveryParty.SENDER)
        assertEquals(SENDER_PHONE, model.state.await { it.dialNumber != null }.dialNumber)
        assertEquals("\"sender\"", backend.lastCall("startCall").json["calleeRole"].toString())

        // AL-33: a direct cellular dial, not AL-48's Free-call / Normal-call chooser.
        assertEquals("\"direct_dial\"", backend.lastCall("startCall").json["callType"].toString())

        model.consume()
        model.call(DeliveryParty.RECIPIENT)
        assertEquals(RECIPIENT_PHONE, model.state.await { it.dialNumber != null }.dialNumber)
        assertEquals("\"recipient\"", backend.lastCall("startCall").json["calleeRole"].toString())
    }

    @Test
    fun resuming_a_delivery_that_is_already_in_transit_opens_the_last_sheet() = runBlocking {
        // A driver whose app was killed between the two doors comes back to the sheet the RIDE is
        // on, because `package.picked_up` is the `→ InProgress` move. Nothing local is remembered.
        backend.returns("getRide", packageRide(RideState.InProgress, version = 6))

        val model = viewModel()
        val resumed = model.state.await { it.ride != null }

        assertEquals(DeliverySheet.COMPLETE, resumed.sheet)
        assertTrue(resumed.pickedUp)
    }

    private fun viewModel(proofs: ProofUploadQueue = ProofUploadQueue()): DeliveryViewModel =
        deliveryViewModel(backend = backend, location = location, proofs = proofs)

    private companion object {

        /** Four digits, and not the one the fixture ride was booked with. */
        const val WRONG_OTP = "0000"
    }
}

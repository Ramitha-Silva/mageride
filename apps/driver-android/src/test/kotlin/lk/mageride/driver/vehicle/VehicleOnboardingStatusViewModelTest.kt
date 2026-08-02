package lk.mageride.driver.vehicle

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-006 — the four-document verdict list and the automatic APPROVED transition.
 *
 * The DoD line: *"4-doc status screen with per-doc Verified/Pending and automatic APPROVED
 * transition"*. Note where the transition happens — registry-svc sets `status=APPROVED` when the
 * fourth step verifies (AL-27, Change 6/22), with no Verification Officer in the path. What is
 * asserted here is that the screen reads it rather than deciding it.
 */
class VehicleOnboardingStatusViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val session = VehicleOnboardingSession()

    @BeforeTest
    fun setUp() {
        main.install()
        session.open(VEHICLE_ID)
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun four_verified_documents_read_as_approved_with_no_officer_step() = runBlocking {
        backend.returns(
            "getVehicleOnboardingStatus",
            onboardingStatus(
                steps = verdicts(
                    details = StepVerdict.VERIFIED,
                    insurance = StepVerdict.VERIFIED,
                    revenue = StepVerdict.VERIFIED,
                    photos = StepVerdict.VERIFIED,
                ),
                nextStep = null,
                status = RegistrationStatus.APPROVED,
                onboardingStatus = OnboardingStatus.APPROVED,
            ),
        )
        backend.returns(
            "getVehicle",
            detail(status = RegistrationStatus.APPROVED, onboardingStatus = OnboardingStatus.APPROVED),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(4, model.state.value.rows.size, "the wireframe's Document verification (4)")
        assertEquals(0, model.state.value.pendingCount)
        assertTrue(model.state.value.isApproved)
        assertEquals("ABC-1234", model.state.value.registrationNumber)
        assertEquals(VehicleType.SEDAN, model.state.value.vehicleType)
    }

    @Test
    fun one_pending_document_keeps_the_vehicle_out_of_service_and_is_counted() = runBlocking {
        // The wireframe's "⚠ 1 pending — plate unreadable; sent to a Verification Officer".
        backend.returns(
            "getVehicleOnboardingStatus",
            onboardingStatus(
                steps = verdicts(
                    details = StepVerdict.VERIFIED,
                    insurance = StepVerdict.VERIFIED,
                    revenue = StepVerdict.VERIFIED,
                    photos = StepVerdict.PENDING_REVIEW,
                ),
            ),
        )
        backend.returns("getVehicle", detail())

        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(1, model.state.value.pendingCount)
        assertFalse(model.state.value.isApproved)
        assertEquals(
            StepVerdict.PENDING_REVIEW,
            model.state.value.rows.single { (step, _) -> step == OnboardingStep.PHOTOS }.second,
        )
    }

    @Test
    fun a_screen_opened_with_no_vehicle_named_says_so_rather_than_failing() = runBlocking {
        // A back-stack restore onto a vehicle that has since been deactivated. Asking registry-svc
        // for it would render a 404 the driver cannot act on.
        session.close()

        val model = viewModel()
        model.state.await { !it.loading }

        assertTrue(model.state.value.unknownVehicle)
        assertFalse(backend.called("getVehicleOnboardingStatus"))
    }

    @Test
    fun a_rejection_carries_its_reason_so_the_driver_knows_what_to_re_upload() = runBlocking {
        // US-2.15.
        backend.returns(
            "getVehicleOnboardingStatus",
            onboardingStatus(steps = verdicts(details = StepVerdict.VERIFIED), status = RegistrationStatus.REJECTED),
        )
        backend.returns(
            "getVehicle",
            detail(status = RegistrationStatus.REJECTED, rejectionReason = "Insurance certificate has expired"),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertTrue(model.state.value.isRejected)
        assertEquals("Insurance certificate has expired", model.state.value.rejectionReason)
    }

    private fun viewModel(): VehicleOnboardingStatusViewModel = VehicleOnboardingStatusViewModel(
        vehicles = VehicleOnboardingRepository(backend.mageRideApi().registry),
        session = session,
    )
}

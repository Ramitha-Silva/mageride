package lk.mageride.driver.vehicle

import io.ktor.http.HttpStatusCode
import kotlinx.coroutines.runBlocking
import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.capture.DocumentCaptureTarget
import lk.mageride.driver.onboarding.MainDispatcher
import lk.mageride.driver.onboarding.await
import lk.mageride.driver.onboarding.testImage
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.VerifyStatus
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-004 → 004c — the AL-30 rules the wizard exists to obey.
 *
 * The DoD lines these exist for: *"four wizard steps with per-step save,
 * resume-at-next-incomplete, and Pending (⚑ admin verify) states"*, *"a saved partial vehicle
 * shows Incomplete and resumes at the correct step after app restart"*, and *"a plate/reg mismatch
 * surfaces the Pending + admin-verify state on the photos step"*.
 */
class VehicleOnboardingViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val captures = DocumentCaptureCoordinator()
    private val session = VehicleOnboardingSession()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun a_driver_with_no_vehicle_starts_at_step_1_and_registration_saves_it() = runBlocking {
        backend.returns("listMyVehicles", VehicleListResponse(emptyList()))
        backend.returns("registerVehicle", registered())

        val model = viewModel()
        model.state.await { !it.loading }
        assertEquals(OnboardingStep.DETAILS, model.state.value.step)
        assertFalse(model.state.value.canContinue, "an empty form")

        model.onVehicleTypeChanged(RideVehicleType.SEDAN)
        model.onRegistrationChanged("ABC-1234")
        assertTrue(model.state.value.canContinue)

        model.onContinue()
        model.state.await { it.step == OnboardingStep.INSURANCE || it.error != null }

        // Δ C029: the registration IS the `details` step, so there is no second call to save it.
        val body = backend.lastCall("registerVehicle").json
        assertEquals("ABC-1234", body["registrationNumber"]?.toString()?.trim('"'))
        assertEquals("sedan", body["vehicleType"]?.toString()?.trim('"'))
        // AL-27's fence on the wire: the Driver App onboards Mode C and nothing else.
        assertEquals("C", body["mode"]?.toString()?.trim('"'))
        assertFalse(backend.called("saveVehicleOnboardingStep"), "step 1 is the registration itself")

        assertEquals(OnboardingStep.INSURANCE, model.state.value.step)
        assertEquals(VEHICLE_ID, model.state.value.vehicleId)
    }

    @Test
    fun there_is_no_permit_and_no_tracker_field_and_no_mode_a_type_to_pick() {
        // AL-27's fence as the *shape* of the screen rather than a rule in it: `RideVehicleType`
        // has no `bus` and no `train`, so a Mode A vehicle cannot be typed in, and nothing on the
        // request carries a permit or an IMEI. Both belong to the Fleet Portal (SCR-FP-004).
        assertTrue(RideVehicleType.entries.none { it.name == "BUS" || it.name == "TRAIN" })
    }

    @Test
    fun re_opening_the_wizard_resumes_at_the_first_non_verified_step_and_never_at_step_1() = runBlocking {
        // AL-30/BR-25.4 — the rule the driver notices when they come back to a half-finished
        // vehicle, and the one that costs them the most if it is wrong.
        backend.returns("listMyVehicles", VehicleListResponse(listOf(summary(registrationNumber = "QP-7788"))))
        backend.returns(
            "getVehicleOnboardingStatus",
            onboardingStatus(
                steps = verdicts(details = StepVerdict.VERIFIED, insurance = StepVerdict.VERIFIED),
                nextStep = OnboardingStep.REVENUE,
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(OnboardingStep.REVENUE, model.state.value.step, "step 3 of 4, not step 1")
        assertEquals("QP-7788", model.state.value.registrationNumber, "the plate is not retyped")
        assertEquals(VEHICLE_ID, model.state.value.vehicleId)
    }

    @Test
    fun an_approved_vehicle_is_not_resumed_the_wizard_starts_a_new_one() = runBlocking {
        // US-2.27: "when the current vehicle is Approved, the wizard entry point creates a NEW
        // vehicle at Step 1/4".
        backend.returns(
            "listMyVehicles",
            VehicleListResponse(
                listOf(
                    summary(
                        status = RegistrationStatus.APPROVED,
                        onboardingStatus = OnboardingStatus.APPROVED,
                    ),
                ),
            ),
        )

        val model = viewModel()
        model.state.await { !it.loading }

        assertEquals(OnboardingStep.DETAILS, model.state.value.step)
        assertEquals(null, model.state.value.vehicleId, "a NEW vehicle, so no id until it is created")
    }

    @Test
    fun a_captured_document_goes_up_with_its_step_in_one_request_and_says_how_it_was_captured() = runBlocking {
        val model = resumedAt(OnboardingStep.INSURANCE)
        backend.returns("saveVehicleOnboardingStep", stepSaved(nextStep = OnboardingStep.REVENUE))

        assertFalse(model.state.value.canContinue, "nothing captured yet")
        captures.open(DocumentCaptureTarget.INSURANCE)
        captures.deliver(testImage("insurance.jpg"))
        assertTrue(model.state.value.canContinue)

        model.onContinue()
        model.state.await { it.step == OnboardingStep.REVENUE || it.error != null }

        // Δ MCS-01 — the image and the step in one multipart request; there is no id to mint.
        val call = backend.lastCall("saveVehicleOnboardingStep")
        assertTrue(call.path.endsWith("/onboarding/insurance"), "path was ${call.path}")
        assertTrue(call.body.contains("insurance.jpg"), "the document itself")
        // AL-43: the provenance travels with the image, and it is what the officer queue sorts on.
        assertTrue(call.body.contains("camera_dragcrop"), "captured through SCR-DA-005")
    }

    @Test
    fun the_photos_step_needs_both_sides_because_one_photo_cannot_show_two_plates() = runBlocking {
        val model = resumedAt(OnboardingStep.PHOTOS)

        captures.open(DocumentCaptureTarget.VEHICLE_FRONT)
        captures.deliver(testImage("vehicle-front.jpg"))
        assertFalse(model.state.value.canContinue, "only the front")

        captures.open(DocumentCaptureTarget.VEHICLE_BACK)
        captures.deliver(testImage("vehicle-back.jpg"))
        assertTrue(model.state.value.canContinue)
    }

    @Test
    fun a_plate_that_does_not_match_the_reg_no_holds_the_driver_on_the_pending_card() = runBlocking {
        // The DoD line: "a plate/reg mismatch surfaces the Pending + admin-verify state on the
        // photos step". BR-25.3 makes `reg_no_match=false` a pending field, which makes the step
        // `PENDING_REVIEW`.
        val model = resumedAt(OnboardingStep.PHOTOS)
        backend.returns("saveVehicleOnboardingStep", stepSaved(stepStatus = StepVerdict.PENDING_REVIEW))
        backend.returns(
            "getVehicleOnboardingStatus",
            onboardingStatus(
                steps = verdicts(photos = StepVerdict.PENDING_REVIEW),
                fields = listOf(
                    field(VehicleFieldKeys.PLATE_TEXT, "ABC-1284", VerifyStatus.PENDING),
                    field(VehicleFieldKeys.REG_NO_MATCH, "false", VerifyStatus.PENDING),
                ),
            ),
        )

        captures.open(DocumentCaptureTarget.VEHICLE_FRONT)
        captures.deliver(testImage("vehicle-front.jpg"))
        captures.open(DocumentCaptureTarget.VEHICLE_BACK)
        captures.deliver(testImage("vehicle-back.jpg"))

        model.onContinue()
        model.state.await { it.savedVerdict != null || it.error != null }

        assertTrue(model.state.value.isPendingReview, "⚑ admin verify")
        assertFalse(model.state.value.submitted, "the driver has to see why before moving on")
        assertTrue(
            model.state.value.stepFields.all { it.needsOfficerReview },
            "both plate rows are flagged for the officer queue",
        )

        // BR-25.3: a `pending_review` step is pending BY DESIGN — an officer clears it, not the
        // driver — so a second tap continues rather than trapping them in the wizard.
        model.onContinue()
        model.state.await { it.submitted }
        assertEquals(1, backend.callsTo("saveVehicleOnboardingStep").size, "the same images, the same verdict")
    }

    @Test
    fun a_verified_step_does_not_stop_for_a_card_there_is_nothing_to_read() = runBlocking {
        val model = resumedAt(OnboardingStep.REVENUE)
        backend.returns("saveVehicleOnboardingStep", stepSaved(nextStep = OnboardingStep.PHOTOS))

        captures.open(DocumentCaptureTarget.REVENUE_LICENCE)
        captures.deliver(testImage("revenue-licence.jpg"))
        model.onContinue()
        model.state.await { it.step == OnboardingStep.PHOTOS || it.error != null }

        assertEquals(OnboardingStep.PHOTOS, model.state.value.step)
        assertFalse(model.state.value.isPendingReview)
    }

    @Test
    fun a_duplicate_plate_is_an_inline_error_on_the_field_that_has_to_change() = runBlocking {
        // D-37's active-set uniqueness. A screen-level "something went wrong" would leave the
        // driver looking for which of the two fields to fix.
        backend.returns("listMyVehicles", VehicleListResponse(emptyList()))
        backend.fails("registerVehicle", HttpStatusCode.Conflict, "registration-exists")

        val model = viewModel()
        model.state.await { !it.loading }
        model.onVehicleTypeChanged(RideVehicleType.THREE_WHEELER)
        model.onRegistrationChanged("ABC-1234")
        model.onContinue()
        model.state.await { it.registrationTaken || it.error != null }

        assertTrue(model.state.value.registrationTaken)
        assertEquals(null, model.state.value.error, "the field carries the message, not the screen")
        assertEquals(OnboardingStep.DETAILS, model.state.value.step)
    }

    @Test
    fun back_from_step_1_leaves_the_wizard_and_back_from_anywhere_else_steps_back() = runBlocking {
        val model = resumedAt(OnboardingStep.REVENUE)

        model.onBack()
        assertEquals(OnboardingStep.INSURANCE, model.state.value.step)
        assertFalse(model.state.value.exited)

        model.onBack()
        assertEquals(OnboardingStep.DETAILS, model.state.value.step)

        model.onBack()
        assertTrue(model.state.value.exited, "D2' §SCR-DA-004: back exits the wizard")
    }

    @Test
    fun a_licence_capture_never_lands_on_the_wizard() = runBlocking {
        // C068's Profile Setup shares the coordinator and owns the two licence slots. AL-27 keeps
        // driver identity and vehicle onboarding apart, in both directions.
        val model = resumedAt(OnboardingStep.INSURANCE)

        captures.open(DocumentCaptureTarget.LICENCE_FRONT)
        captures.deliver(testImage("licence-front.jpg"))

        assertEquals(null, model.state.value.insurance)
        assertFalse(model.state.value.canContinue)
    }

    /** A wizard opened onto a vehicle that is part-way through, resuming at [step]. */
    private suspend fun resumedAt(step: OnboardingStep): VehicleOnboardingViewModel {
        backend.returns("listMyVehicles", VehicleListResponse(listOf(summary())))
        backend.returns("getVehicleOnboardingStatus", onboardingStatus(steps = verdicts(), nextStep = step))

        return viewModel().also { it.state.await { state -> !state.loading } }
    }

    private fun viewModel(): VehicleOnboardingViewModel = VehicleOnboardingViewModel(
        vehicles = VehicleOnboardingRepository(backend.mageRideApi().registry),
        captures = captures,
        session = session,
    )
}

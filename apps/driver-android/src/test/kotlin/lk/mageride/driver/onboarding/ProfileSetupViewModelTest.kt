package lk.mageride.driver.onboarding

import kotlinx.coroutines.runBlocking
import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.capture.DocumentCaptureTarget
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.FieldSource
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.VerifyStatus
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.UpsertDriverProfileResponse
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.mageRideApi
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * SCR-DA-003a — the three required inputs, the AL-29 manual-entry path, and the ⚑ that goes with
 * it.
 *
 * The DoD line these exist for is *"a driver-typed (unreadable) field is submitted with
 * source=manual and shows the admin-verify flag"*. Note where the provenance is decided: the
 * client sends the value and **registry-svc** stamps `source='manual'` /
 * `verify_status='pending'` (AL-29, US-2.4a). A client that could claim `source='ai'` would make
 * the rule advisory, so what is asserted here is that the typed value reaches the request body
 * and that the verdict that comes back is rendered as a flag.
 */
class ProfileSetupViewModelTest {

    private val main = MainDispatcher()
    private val backend = FakeApiBackend()
    private val uploader = RecordingDocumentUploader()
    private val captures = DocumentCaptureCoordinator()

    @BeforeTest
    fun setUp() {
        main.install()
    }

    @AfterTest
    fun tearDown() {
        main.uninstall()
    }

    @Test
    fun save_is_dead_until_the_name_the_photo_and_both_licence_sides_are_there() = runBlocking {
        val model = viewModel()
        assertFalse(model.state.value.canSave, "an empty form")

        model.onNameChanged("K. Fernando")
        assertFalse(model.state.value.canSave, "no photo — US-2.12 makes it required")

        model.onPhotoPicked(testImage("photo.jpg"))
        assertFalse(model.state.value.canSave, "no licence")

        captures.open(DocumentCaptureTarget.LICENCE_FRONT)
        captures.deliver(testImage("front.jpg"))
        assertFalse(model.state.value.canSave, "only one side of the licence")

        captures.open(DocumentCaptureTarget.LICENCE_BACK)
        captures.deliver(testImage("back.jpg"))
        assertTrue(model.state.value.canSave)
    }

    @Test
    fun a_clean_extraction_continues_straight_to_permissions() = runBlocking {
        backend.returns("upsertDriverProfile", response(nic = confirmed("nic_no", "199012345678")))
        val model = completedForm()

        model.save()
        model.state.await { it.done || it.error != null }

        assertTrue(model.state.value.done, "nothing to review, so nothing to stop for")
        assertFalse(model.state.value.hasOfficerFlag)
        // All three images exist server-side before the profile row does — the contract takes ids.
        assertContentEquals(
            listOf(
                DriverDocumentKind.PROFILE_PHOTO,
                DriverDocumentKind.LICENCE_FRONT,
                DriverDocumentKind.LICENCE_BACK,
            ),
            uploader.uploads,
        )
    }

    @Test
    fun an_unread_field_holds_the_driver_on_the_card_and_raises_the_admin_verify_flag() = runBlocking {
        // BR-25.2: a required key extraction could not settle comes back pending, with no value.
        backend.returns("upsertDriverProfile", response(nic = pending("nic_no", value = null)))
        val model = completedForm()

        model.save()
        model.state.await { it.extraction != null || it.error != null }

        assertFalse(model.state.value.done, "the driver has to see the card before moving on")
        assertTrue(model.state.value.hasOfficerFlag, "⚑ Admin verify")
        val nic = model.state.value.extraction?.field(LicenceFieldKeys.NIC_NO)
        assertTrue(nic?.needsOfficerReview == true)
    }

    @Test
    fun a_driver_typed_field_is_sent_on_the_next_save_and_the_screen_moves_on() = runBlocking {
        backend.returns("upsertDriverProfile", response(nic = pending("nic_no", value = null)))
        val model = completedForm()
        model.save()
        model.state.await { it.extraction != null && !it.busy }

        // The scan was unclear, so the driver types it (AL-29's manual path).
        model.onNicChanged("199012345678")
        model.onAllowedVehicleTypesChanged(listOf(VehicleType.THREE_WHEELER))
        backend.returns("upsertDriverProfile", response(nic = pending("nic_no", "199012345678")))

        model.save()
        model.state.await { it.done || it.error != null }

        val body = backend.lastCall("upsertDriverProfile").json
        assertEquals("199012345678", body["nicNo"]?.toString()?.trim('"'), "the typed NIC reaches the request")
        assertTrue(
            body["allowedVehicleTypes"].toString().contains("three_wheeler"),
            "and so do the licence classes",
        )

        // BR-25.2: "The driver may proceed; the field is trusted only after officer Confirm." A
        // manual field is pending BY DESIGN, so waiting for it to clear would trap the driver here.
        assertTrue(model.state.value.done)
        assertTrue(model.state.value.hasOfficerFlag, "the flag stays up — an officer still has to confirm it")
    }

    @Test
    fun a_second_tap_with_nothing_new_to_send_continues_without_calling_again() = runBlocking {
        backend.returns("upsertDriverProfile", response(nic = pending("nic_no", value = null)))
        val model = completedForm()

        model.save()
        model.state.await { it.extraction != null && !it.busy }
        val callsAfterFirst = backend.callsTo("upsertDriverProfile").size
        model.save()

        assertEquals(callsAfterFirst, backend.callsTo("upsertDriverProfile").size, "same images, same verdicts")
        assertTrue(model.state.value.done)
    }

    @Test
    fun the_missing_upload_route_is_reported_as_itself_and_not_as_a_generic_failure() = runBlocking {
        // The honest state of the platform today: no contract route creates a `docs.uploads` row
        // for a driver's photo or licence. The screen says so rather than showing "something went
        // wrong", which would send someone hunting for a network fault that is not there.
        val model = viewModel(uploader = UnavailableDriverDocumentUploader())
        fillForm(model)

        model.save()
        model.state.await { it.error != null }

        assertEquals(lk.mageride.driver.R.string.error_upload_unavailable, model.state.value.error)
        assertFalse(model.state.value.done)
        assertFalse(backend.called("upsertDriverProfile"), "nothing is posted with ids the server would refuse")
    }

    @Test
    fun a_vehicle_document_capture_never_lands_on_this_screen() = runBlocking {
        val model = completedForm()

        // C069's wizard shares the coordinator. Profile Setup owns two slots and must ignore the
        // other four — AL-27 keeps identity and vehicle onboarding apart.
        captures.open(DocumentCaptureTarget.INSURANCE)
        captures.deliver(testImage("insurance.jpg"))

        assertEquals("front.jpg", model.state.value.draft.licenceFront?.fileName)
        assertEquals("back.jpg", model.state.value.draft.licenceBack?.fileName)
    }

    private fun completedForm(): ProfileSetupViewModel = viewModel().also(::fillForm)

    private fun fillForm(model: ProfileSetupViewModel) {
        model.onNameChanged("K. Fernando")
        model.onPhotoPicked(testImage("photo.jpg"))
        captures.open(DocumentCaptureTarget.LICENCE_FRONT)
        captures.deliver(testImage("front.jpg"))
        captures.open(DocumentCaptureTarget.LICENCE_BACK)
        captures.deliver(testImage("back.jpg"))
    }

    private fun viewModel(uploader: DriverDocumentUploader = this.uploader): ProfileSetupViewModel {
        val api = backend.mageRideApi()
        return ProfileSetupViewModel(
            profiles = DriverProfileRepository(registry = api.registry, iam = api.iam, uploader = uploader),
            captures = captures,
        )
    }

    /** A `PUT /v1/drivers/profile` verdict with the licence number and expiry read cleanly. */
    private fun response(nic: ExtractedField) = UpsertDriverProfileResponse(
        driverId = "01JDRIVER00000000000000000",
        status = RegistrationStatus.PENDING,
        fields = listOf(
            confirmed("licence_no", "B1234567"),
            confirmed("licence_expiry", "2028-04-30"),
            nic,
            confirmed("allowed_vehicle_types", "three_wheeler"),
        ),
    )

    private fun confirmed(key: String, value: String) = ExtractedField(
        key = key,
        value = value,
        source = FieldSource.OCR,
        confidence = 0.97,
        verifyStatus = VerifyStatus.CONFIRMED,
    )

    private fun pending(key: String, value: String?) = ExtractedField(
        key = key,
        value = value,
        source = if (value == null) FieldSource.OCR else FieldSource.MANUAL,
        confidence = if (value == null) 0.2 else null,
        verifyStatus = VerifyStatus.PENDING,
    )
}

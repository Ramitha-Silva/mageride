package lk.mageride.driver.onboarding

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.capture.CapturedImage
import lk.mageride.driver.capture.DocumentCaptureCoordinator
import lk.mageride.driver.capture.DocumentCaptureTarget
import lk.mageride.shared.data.models.VehicleType

/**
 * SCR-DA-003a's state.
 *
 * @property draft What the driver has filled in.
 * @property extraction The verdict card, once a save has come back. `null` before the first one.
 * @property busy A save is in flight.
 * @property error Resolved copy for the last failure.
 * @property done Set when Profile Setup is finished; the screen navigates to SCR-DA-007.
 */
internal data class ProfileSetupState(
    val draft: ProfileDraft = ProfileDraft(),
    val extraction: LicenceExtraction? = null,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
    val done: Boolean = false,
) {
    /** Name, photo and both licence sides — all three are required before Save (AL-27). */
    val canSave: Boolean get() = !busy && draft.isComplete

    /** Whether the ⚑ banner is up: at least one field is manual, doubtful or unread (BR-25.2). */
    val hasOfficerFlag: Boolean get() = extraction?.hasOfficerFlag == true
}

/**
 * SCR-DA-003a — driver identity, and the only thing standing between a new driver and Home.
 *
 * **No vehicle is involved** (AL-27, Change 6/22). This screen writes `registry.driver_profiles`
 * and one vehicle-less `registry.documents(kind='driving_license')`; the Mode-C wizard that
 * onboards a vehicle is optional, comes later, and belongs to C069.
 *
 * The save is a two-beat interaction, and that is the wireframe's, not an invention:
 * `PUT /v1/drivers/profile` is what *queues* the Gemini Flash extraction, so the "✦ AI-extracted"
 * card cannot exist before the first save. The first Save reveals it; if every field came back
 * confirmed the screen continues immediately, and if anything is doubtful, unread or typed the
 * driver is left on the card to correct it — with the ⚑ chip saying the value goes to a
 * Verification Officer either way (AL-29, US-2.4a).
 */
internal class ProfileSetupViewModel(
    private val profiles: DriverProfileRepository,
    private val captures: DocumentCaptureCoordinator,
) : ViewModel() {

    private val mutableState = MutableStateFlow(ProfileSetupState())

    /** The draft the last successful save carried, so a second CTA tap knows if anything changed. */
    private var lastSubmitted: ProfileDraft? = null

    val state: StateFlow<ProfileSetupState> = mutableState.asStateFlow()

    init {
        // SCR-DA-005 (C069) hands its cropped, de-skewed image back here. Replayed, so an image
        // taken while this screen was in the background still lands.
        viewModelScope.launch {
            captures.results.collect { result ->
                apply(result.target, result.image)
                captures.consume()
            }
        }
    }

    fun onNameChanged(name: String) {
        mutableState.update { it.copy(draft = it.draft.copy(name = name), error = null) }
    }

    /** The avatar's camera badge, and the gallery pick the wireframe also allows. */
    fun onPhotoPicked(image: CapturedImage) {
        mutableState.update { it.copy(draft = it.draft.copy(photo = image), error = null) }
    }

    /** A licence tile was tapped. The scanner is SCR-DA-005's; this only says which slot. */
    fun requestCapture(target: DocumentCaptureTarget) {
        captures.open(target)
    }

    /**
     * The driver typed a NIC the scan could not read.
     *
     * Stored on the draft and sent on the next save. The **client never stamps a provenance** —
     * registry-svc is what writes `source='manual'`, `verify_status='pending'` and queues the
     * officer review, because a client that could claim `source='ai'` would make AL-29 advisory.
     */
    fun onNicChanged(nic: String) {
        mutableState.update { it.copy(draft = it.draft.copy(nicNo = nic), error = null) }
    }

    /** Same, for the licence classes the scan could not read. */
    fun onAllowedVehicleTypesChanged(types: List<VehicleType>) {
        mutableState.update { it.copy(draft = it.draft.copy(allowedVehicleTypes = types), error = null) }
    }

    /**
     * The CTA. `PUT /v1/drivers/profile` when there is something to send, otherwise onward.
     *
     * Three cases, and the wireframe draws all three under one "Save & continue" bar:
     *
     * * **No card yet** — save, which is what queues the extraction. If nothing came back
     *   doubtful, unread or manual there is nothing to review and the screen continues.
     * * **A card, and the driver has typed a correction since it appeared** — save again, then
     *   continue whatever the new verdicts are. A `manual` field is pending *by design*
     *   (BR-25.2), so waiting for it to clear would trap the driver on this screen forever.
     * * **A card and nothing new to send** — continue. Re-running the extraction over the same
     *   images would only produce the same verdicts.
     */
    fun save() {
        val current = mutableState.value
        if (!current.canSave) return

        val firstSave = current.extraction == null
        val correctionTyped = current.draft.manualValues != lastSubmitted?.manualValues
        if (!firstSave && !correctionTyped) {
            mutableState.update { it.copy(done = true) }
            return
        }
        submit(continueAfterwards = !firstSave)
    }

    @Suppress("TooGenericExceptionCaught")
    private fun submit(continueAfterwards: Boolean) {
        mutableState.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            val draft = mutableState.value.draft
            try {
                val extraction = profiles.submit(draft)
                lastSubmitted = draft
                mutableState.update {
                    it.copy(extraction = extraction, done = continueAfterwards || !extraction.needsReview)
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            } finally {
                mutableState.update { it.copy(busy = false) }
            }
        }
    }

    private fun apply(target: DocumentCaptureTarget, image: CapturedImage) {
        mutableState.update { current ->
            val draft = when (target) {
                DocumentCaptureTarget.LICENCE_FRONT -> current.draft.copy(licenceFront = image)

                DocumentCaptureTarget.LICENCE_BACK -> current.draft.copy(licenceBack = image)

                // The four vehicle-document slots belong to C069's wizard and never reach here.
                else -> current.draft
            }
            current.copy(draft = draft, error = null)
        }
    }
}

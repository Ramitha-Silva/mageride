package lk.mageride.driver.vehicle

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
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.api.MageRideError
import lk.mageride.shared.data.models.ErrorCode
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VerifyStatus
import lk.mageride.shared.data.models.registry.OnboardingCorrections
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.StepVerdict

/**
 * SCR-DA-004…004c's state — one wizard, four step bodies.
 *
 * @property step Which of the four is on screen. Progress is its position, 1-based.
 * @property vehicleId The vehicle being onboarded; `null` until Step 1/4 has been saved.
 * @property registrationNumber Step 1/4's plate, unique across the active set (D-37).
 * @property vehicleType Step 1/4's canonical type (AL-09). Mode-C types only.
 * @property insurance Step 2/4's capture.
 * @property revenue Step 3/4's capture.
 * @property photoFront Step 4/4's front photo, number plate visible.
 * @property photoBack Step 4/4's back photo.
 * @property savedVerdict The verdict on the step currently shown, once it has been saved. `null`
 *   means nothing has been sent for this step yet — which is what makes the CTA a save rather
 *   than an advance.
 * @property fields Every extracted field on this vehicle; the extract cards filter to their own.
 * @property loading The resume read is in flight — AL-30 decides which step opens, not the screen.
 * @property busy A save is in flight.
 * @property error Resolved copy for the last failure.
 * @property registrationTaken `409 registration-exists` (D-37) — an inline error on the plate
 *   field rather than a screen-level one, because it is that one field that has to change.
 * @property corrections What the driver has retyped over a doubtful extracted value, keyed by
 *   `registry.document_fields` key (Δ MCS-02, BR-25.3). Sent on the next save.
 * @property editingKey The row whose ✎ is open, or `null` when none is.
 * @property submitted Step 4/4 is saved; the wizard hands over to SCR-DA-006.
 * @property exited Back from Step 1/4 — *"back exits the wizard"* (D2' §SCR-DA-004).
 */
internal data class VehicleOnboardingState(
    val step: OnboardingStep = OnboardingStep.DETAILS,
    val vehicleId: Ulid? = null,
    val registrationNumber: String = "",
    val vehicleType: RideVehicleType? = null,
    val insurance: CapturedImage? = null,
    val revenue: CapturedImage? = null,
    val photoFront: CapturedImage? = null,
    val photoBack: CapturedImage? = null,
    val savedVerdict: StepVerdict? = null,
    val fields: List<ExtractedField> = emptyList(),
    val loading: Boolean = true,
    val busy: Boolean = false,
    @param:StringRes val error: Int? = null,
    val registrationTaken: Boolean = false,
    val corrections: Map<String, String> = emptyMap(),
    val editingKey: String? = null,
    val submitted: Boolean = false,
    val exited: Boolean = false,
) {

    /** 1…4 — the wireframe's "Step 2 of 4" and the 25/50/75/100 % progress bar. */
    val stepNumber: Int get() = step.ordinal + 1

    /** How full the progress bar is. */
    val progress: Float get() = stepNumber / OnboardingStep.entries.size.toFloat()

    /** Whether the CTA is live: this step has everything it needs to be saved. */
    val canContinue: Boolean
        get() = !busy && !loading && (hasCorrections || whenStepIsComplete)

    private val whenStepIsComplete: Boolean
        get() = when (step) {
            OnboardingStep.DETAILS -> registrationNumber.isNotBlank() && vehicleType != null

            OnboardingStep.INSURANCE -> insurance != null

            OnboardingStep.REVENUE -> revenue != null

            // Both, and the wireframe says why: one photograph cannot show a vehicle's front and
            // back number plates, and the plate is what step 4 is checked on.
            OnboardingStep.PHOTOS -> photoFront != null && photoBack != null
        }

    /** Whether this step has been saved and came back needing a Verification Officer (BR-25.3). */
    val isPendingReview: Boolean get() = savedVerdict == StepVerdict.PENDING_REVIEW

    /** Whether the driver has retyped something the next save has to carry. */
    val hasCorrections: Boolean get() = corrections.values.any { it.isNotBlank() }

    /** The extract card's rows for the step on screen, in the order the wireframe draws them. */
    val stepFields: List<ExtractedField>
        get() = VehicleFieldKeys.forStep(step).mapNotNull { key -> fields.firstOrNull { it.key == key } }

    /** Whether this step's capture slot already holds an image. */
    fun captured(target: DocumentCaptureTarget): Boolean = when (target) {
        DocumentCaptureTarget.INSURANCE -> insurance != null
        DocumentCaptureTarget.REVENUE_LICENCE -> revenue != null
        DocumentCaptureTarget.VEHICLE_FRONT -> photoFront != null
        DocumentCaptureTarget.VEHICLE_BACK -> photoBack != null
        else -> false
    }
}

/**
 * **SCR-DA-004 → 004c** — the optional, in-app, **Mode-C-only** four-step vehicle wizard
 * (AL-27, AL-30, Change 6/22).
 *
 * Three fences hold this screen up, and each one is visible in the code rather than in a comment
 * alone:
 *
 * * **Mode C only.** `POST /v1/vehicles` pins `mode` to `C` by contract `const` and the type is a
 *   `RideVehicleType`, which has no `bus` and no `train`. **There is no permit field and no
 *   GPS-tracker field** — a Mode A vehicle and its route permit are onboarded in the Fleet Portal
 *   (SCR-FP-004), never here.
 * * **Per-step save** (BR-25.4). Each step is persisted on its own as it is completed, so a driver
 *   who closes the app on Step 3 has lost nothing.
 * * **Resume at the first non-verified step, never Step 1** (AL-30). [ResumePoint] is that rule as
 *   a type, and this view model asks for it on every open rather than deciding for itself.
 *
 * **The CTA is two-beat on a step that comes back Pending**, and that is the wireframe's own
 * sequence rather than an invention: the extract card cannot exist before the upload, because the
 * upload is what queues the Gemini Flash extraction. So the first tap saves; if the step verified
 * there is nothing to read and the wizard moves straight on, and if anything was doubtful or the
 * plate did not match, the driver is left on the card with the ⚑ chip saying an officer will look
 * at it (BR-25.3). A second tap continues — a `pending_review` step is pending **by design** and
 * waiting for it to clear would trap the driver in the wizard forever.
 */
@Suppress("TooManyFunctions") // One wizard, four steps: eight driver actions plus load/save/advance.
internal class VehicleOnboardingViewModel(
    private val vehicles: VehicleOnboardingRepository,
    private val captures: DocumentCaptureCoordinator,
    private val session: VehicleOnboardingSession,
) : ViewModel() {

    private val mutableState = MutableStateFlow(VehicleOnboardingState())

    val state: StateFlow<VehicleOnboardingState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch {
            captures.results.collect { result ->
                apply(result.target, result.image)
                captures.consume()
            }
        }
        load()
    }

    /** Reads AL-30's resume point. Called on open and again after a failure the driver retries. */
    @Suppress("TooGenericExceptionCaught")
    fun load() {
        mutableState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            try {
                when (val point = vehicles.resume()) {
                    ResumePoint.Fresh -> mutableState.update { it.copy(loading = false) }

                    is ResumePoint.Resume -> mutableState.update {
                        it.copy(
                            vehicleId = point.vehicleId,
                            registrationNumber = point.registrationNumber,
                            vehicleType = point.vehicleType,
                            step = point.step,
                            fields = point.fields,
                            loading = false,
                        )
                    }
                }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(loading = false, error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    fun onRegistrationChanged(value: String) {
        mutableState.update {
            it.copy(registrationNumber = value, error = null, registrationTaken = false, savedVerdict = null)
        }
    }

    fun onVehicleTypeChanged(type: RideVehicleType) {
        mutableState.update { it.copy(vehicleType = type, error = null, savedVerdict = null) }
    }

    /** A capture tile was tapped. The scanner is SCR-DA-005's; this only says which slot. */
    fun requestCapture(target: DocumentCaptureTarget) {
        captures.open(target)
    }

    /** The ✎ on an extracted row (Δ MCS-02). Opens it for editing; a second tap closes it. */
    fun toggleEdit(key: String) {
        mutableState.update { it.copy(editingKey = if (it.editingKey == key) null else key) }
    }

    /**
     * The driver retyped a value the scan got wrong or could not read (BR-25.3).
     *
     * Held until the next save. **The client never stamps a provenance** — registry-svc writes
     * `source='manual'`, `verify_status='pending'` and queues the officer review, because a client
     * that could claim `source='ai'` would make AL-29 advisory.
     */
    fun onCorrectionChanged(key: String, value: String) {
        mutableState.update { current ->
            val corrections = current.corrections.toMutableMap()
            if (value.isBlank()) corrections.remove(key) else corrections[key] = value
            current.copy(corrections = corrections, error = null)
        }
    }

    /**
     * The ‹ in the app bar.
     *
     * Step 1/4's back **exits the wizard** (D2' §SCR-DA-004); anything else steps back one, which
     * is how a driver corrects a plate they mistyped before the photos were judged against it.
     */
    fun onBack() {
        val current = mutableState.value
        val previous = OnboardingStep.entries.getOrNull(current.step.ordinal - 1)
        if (previous == null) {
            mutableState.update { it.copy(exited = true) }
        } else {
            mutableState.update {
                it.copy(step = previous, savedVerdict = null, corrections = emptyMap(), editingKey = null, error = null)
            }
        }
    }

    /**
     * The CTA — *"Continue · Insurance ›"*, *"Save & continue"*, *"Save & submit for review"*.
     *
     * @see VehicleOnboardingViewModel for why a Pending step takes two taps and a verified one
     *   takes one.
     */
    fun onContinue() {
        val current = mutableState.value
        if (!current.canContinue) return

        // A correction is a save of its own, even on a step that is already saved — it is the
        // one thing that can change a verdict without a new photograph (Δ MCS-02, BR-25.3).
        if (current.savedVerdict != null && !current.hasCorrections) {
            advance(current.step)
            return
        }
        save(current)
    }

    @Suppress("TooGenericExceptionCaught")
    private fun save(current: VehicleOnboardingState) {
        mutableState.update { it.copy(busy = true, error = null, registrationTaken = false) }
        viewModelScope.launch {
            try {
                val saved = saveStep(current)
                session.open(saved.vehicleId)

                // The save answers a verdict but not the fields behind it — only the
                // onboarding-status read carries those, and the extract card is made of them.
                val fields = runCatching { vehicles.onboardingStatus(saved.vehicleId).fields }
                    .getOrDefault(current.fields)

                mutableState.update {
                    it.copy(
                        vehicleId = saved.vehicleId,
                        savedVerdict = saved.stepStatus,
                        fields = fields,
                        corrections = emptyMap(),
                        editingKey = null,
                        busy = false,
                    )
                }

                // A clean step has nothing for the driver to read, so it does not stop for them.
                if (saved.stepStatus == StepVerdict.VERIFIED) advance(current.step, saved.nextStep)
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update {
                    it.copy(
                        busy = false,
                        registrationTaken = cause.isRegistrationTaken,
                        error = OnboardingErrors.messageFor(cause).takeUnless { _ -> cause.isRegistrationTaken },
                    )
                }
            }
        }
    }

    private suspend fun saveStep(current: VehicleOnboardingState): SavedStep {
        val vehicleId = current.vehicleId
        return when (current.step) {
            OnboardingStep.DETAILS -> {
                val type = requireNotNull(current.vehicleType)
                if (vehicleId == null) {
                    vehicles.start(current.registrationNumber, type)
                } else {
                    vehicles.saveDetails(vehicleId, current.registrationNumber, type)
                }
            }

            OnboardingStep.INSURANCE, OnboardingStep.REVENUE -> {
                val id = requireNotNull(vehicleId)
                val capture = if (current.step == OnboardingStep.INSURANCE) current.insurance else current.revenue

                // A correction with the document already on record needs no upload at all — which
                // is the whole point of it (Δ MCS-02). A first save still carries the image.
                if (current.savedVerdict != null && current.hasCorrections) {
                    vehicles.saveCorrections(id, current.step, current.corrections.asCorrections())
                } else {
                    vehicles.saveDocument(id, current.step, requireNotNull(capture))
                }
            }

            OnboardingStep.PHOTOS -> vehicles.saveDocument(
                vehicleId = requireNotNull(vehicleId),
                step = OnboardingStep.PHOTOS,
                front = requireNotNull(current.photoFront),
                back = requireNotNull(current.photoBack),
            )
        }
    }

    /**
     * Moves the wizard on from [from].
     *
     * [serverNext] is registry-svc's own `nextStep` and wins when it is present — it is derived
     * from all four verdicts, so a driver who resumed at Step 3 and whose Step 2 is still pending
     * is sent back to Step 2 rather than marched forward. Step 4/4 has no next: it hands over to
     * SCR-DA-006, which is where the four verdicts are read.
     */
    private fun advance(from: OnboardingStep, serverNext: OnboardingStep? = null) {
        if (from == OnboardingStep.PHOTOS) {
            mutableState.update { it.copy(submitted = true) }
            return
        }
        val next = serverNext ?: OnboardingStep.entries[from.ordinal + 1]
        mutableState.update {
            it.copy(step = next, savedVerdict = null, corrections = emptyMap(), editingKey = null, error = null)
        }
    }

    private fun apply(target: DocumentCaptureTarget, image: CapturedImage) {
        mutableState.update { current ->
            val updated = when (target) {
                DocumentCaptureTarget.INSURANCE -> current.copy(insurance = image)

                DocumentCaptureTarget.REVENUE_LICENCE -> current.copy(revenue = image)

                DocumentCaptureTarget.VEHICLE_FRONT -> current.copy(photoFront = image)

                DocumentCaptureTarget.VEHICLE_BACK -> current.copy(photoBack = image)

                // The two licence slots belong to C068's Profile Setup and never reach here —
                // AL-27 keeps driver identity and vehicle onboarding apart.
                else -> return@update current
            }
            // A fresh capture un-saves the step: what is on screen is no longer what was sent, so
            // the CTA has to be a save again rather than an advance.
            updated.copy(savedVerdict = null, error = null)
        }
    }
}

/**
 * The driver's retyped values as the client's typed correction set (Δ MCS-02).
 *
 * A map here and a named type on the wire: the screen collects by
 * `registry.document_fields` key, and `OnboardingCorrections` is what stops a key the step does
 * not accept — `reg_no_match` above all — reaching the request at all.
 */
internal fun Map<String, String>.asCorrections(): OnboardingCorrections = OnboardingCorrections(
    insuranceExpiry = this[VehicleFieldKeys.INSURANCE_EXPIRY]?.takeIf(String::isNotBlank),
    insurancePolicyNo = this[VehicleFieldKeys.INSURANCE_POLICY_NO]?.takeIf(String::isNotBlank),
    revenueNo = this[VehicleFieldKeys.REVENUE_NO]?.takeIf(String::isNotBlank),
    revenueExpiry = this[VehicleFieldKeys.REVENUE_EXPIRY]?.takeIf(String::isNotBlank),
)

/** `409 registration-exists` — the plate is already on a live vehicle (D-37, US-2.7). */
private val Throwable.isRegistrationTaken: Boolean
    get() = this is MageRideError && code == ErrorCode.REGISTRATION_EXISTS

/** Whether this field still has to be looked at by a Verification Officer (AL-29). */
internal val ExtractedField.needsOfficerReview: Boolean
    get() = verifyStatus == VerifyStatus.PENDING || value.isNullOrBlank()

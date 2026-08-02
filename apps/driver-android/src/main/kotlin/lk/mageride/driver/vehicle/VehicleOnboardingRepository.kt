package lk.mageride.driver.vehicle

import lk.mageride.driver.capture.CapturedImage
import lk.mageride.shared.data.api.registry.RegistryApi
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.OnboardingStepVerdicts
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.SaveOnboardingStepResponse
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.data.models.registry.VehicleDetail
import lk.mageride.shared.data.models.registry.VehicleOnboardingStatusResponse
import lk.mageride.shared.data.models.registry.VehicleRegistration
import lk.mageride.shared.data.models.registry.VehicleSummary

/**
 * The `registry.document_fields` keys the four Mode-C onboarding steps produce (AL-29,
 * `DocumentFieldKeys` in registry-svc).
 *
 * Machine keys, never display copy — the row labels are `strings.xml`'s and are trilingual. The
 * `details` step has none: D5' §14.1a marks it "(entered)", so the typing **is** the verification
 * and there is no extracted field to show.
 */
internal object VehicleFieldKeys {

    /** Step 2/4. D5' §14.1a verifies insurance on the expiry alone; the other two are extras. */
    const val INSURANCE_EXPIRY = "insurance_expiry"
    const val INSURANCE_POLICY_NO = "insurance_policy_no"
    const val INSURER = "insurer"

    /** Step 3/4 — number **and** expiry, both required. */
    const val REVENUE_NO = "revenue_no"
    const val REVENUE_EXPIRY = "revenue_expiry"

    /** Step 4/4. `reg_no_match` is the plate check; `plate_text` is what the plate was read as. */
    const val REG_NO_MATCH = "reg_no_match"
    const val PLATE_TEXT = "plate_text"

    /** The keys each step's extract card lists, in the order the wireframe draws them. */
    fun forStep(step: OnboardingStep): List<String> = when (step) {
        OnboardingStep.DETAILS -> emptyList()
        OnboardingStep.INSURANCE -> listOf(INSURANCE_EXPIRY, INSURANCE_POLICY_NO, INSURER)
        OnboardingStep.REVENUE -> listOf(REVENUE_NO, REVENUE_EXPIRY)
        OnboardingStep.PHOTOS -> listOf(PLATE_TEXT, REG_NO_MATCH)
    }
}

/**
 * Where the wizard opens.
 *
 * **AL-30's whole rule, as a type.** Re-opening Vehicle Onboarding resumes at the first step that
 * is not verified — *never* Step 1 — and once the current vehicle is approved the entry point
 * starts a **new** one. Returning a sealed value rather than a nullable vehicle id is what stops a
 * screen deciding that for itself.
 */
internal sealed interface ResumePoint {

    /** No vehicle is part-way through. Step 1/4, and `POST /v1/vehicles` will create the vehicle. */
    data object Fresh : ResumePoint

    /**
     * A vehicle with at least one saved step. Opens at [step].
     *
     * @property vehicleId The vehicle being onboarded.
     * @property registrationNumber What Step 1/4 stored, so the driver does not retype it.
     * @property vehicleType Same. `null` only if the plate is on a type this app cannot offer.
     * @property step The first step that is not [StepVerdict.VERIFIED].
     * @property verdicts All four verdicts, for the ⚑ states on the steps already saved.
     * @property fields Every extracted field on the vehicle, for the extract cards.
     */
    data class Resume(
        val vehicleId: Ulid,
        val registrationNumber: String,
        val vehicleType: RideVehicleType?,
        val step: OnboardingStep,
        val verdicts: OnboardingStepVerdicts,
        val fields: List<ExtractedField>,
    ) : ResumePoint
}

/**
 * What one saved step answered.
 *
 * @property vehicleId The vehicle the step belongs to — minted by the first save when the wizard
 *   started fresh.
 * @property stepStatus The verdict on the step just saved. [StepVerdict.PENDING_REVIEW] is what
 *   raises the ⚑ (BR-25.3).
 * @property onboardingStatus Derived incomplete / approved after the save (AL-30).
 * @property registrationStatus The vehicle's status after the save — [RegistrationStatus.APPROVED]
 *   is the auto-approval that happens with no officer step at all (Change 6/22).
 * @property nextStep Where the wizard goes; `null` when every step is verified.
 */
internal data class SavedStep(
    val vehicleId: Ulid,
    val stepStatus: StepVerdict,
    val onboardingStatus: OnboardingStatus,
    val registrationStatus: RegistrationStatus,
    val nextStep: OnboardingStep?,
)

/**
 * The Mode-C wizard's data layer — SCR-DA-004…004c, SCR-DA-006 and SCR-DA-026 all read it.
 *
 * **Mode C, and only Mode C** (AL-27). Nothing here can register a Mode A/B vehicle: `POST
 * /v1/vehicles` pins `mode` to [ServiceMode.C] by contract `const` and the type is a
 * [RideVehicleType], which has no `bus` and no `train`. Permits are not modelled at all — they are
 * the Fleet Portal's (SCR-FP-004).
 *
 * **The wizard's persistence is the server's** (BR-25.4). Nothing about a part-finished vehicle is
 * kept on the handset: each step is saved as it is completed, and a cold start asks
 * `GET /v1/vehicles/mine` + `GET …/onboarding-status` where it left off. A local draft would be
 * a second source of truth for a state machine that already has one, and the one the driver
 * carries in their pocket is the one that gets wiped.
 */
internal class VehicleOnboardingRepository(private val registry: RegistryApi) {

    /** Every vehicle this driver owns or has been assigned (US-2.8, US-13.9). */
    suspend fun myVehicles(): List<VehicleSummary> = registry.listMyVehicles().items

    /**
     * Where the wizard should open (AL-30).
     *
     * The vehicle it resumes is the driver's **own Mode C** one that is still `incomplete`. A
     * temporarily-assigned Mode A/B vehicle is never onboarded here (AL-27) and an approved one is
     * finished, so both fall through to [ResumePoint.Fresh] — which is exactly US-2.27's "the ＋
     * button starts a NEW vehicle when the current one is approved".
     */
    suspend fun resume(): ResumePoint {
        val incomplete = myVehicles().firstOrNull {
            it.isOnboardable && it.onboardingStatus == OnboardingStatus.INCOMPLETE
        } ?: return ResumePoint.Fresh

        val status = registry.getVehicleOnboardingStatus(incomplete.vehicleId)

        return ResumePoint.Resume(
            vehicleId = incomplete.vehicleId,
            registrationNumber = incomplete.registrationNumber,
            vehicleType = RideVehicleType.from(incomplete.vehicleType),
            // `nextStep` is the server's own resume pointer. Falling back to the first
            // non-verified verdict rather than to Step 1 keeps AL-30 true even if it is absent.
            step = status.nextStep ?: status.steps.firstUnverified() ?: OnboardingStep.PHOTOS,
            verdicts = status.steps,
            fields = status.fields,
        )
    }

    /** The four verdicts and every field for one vehicle — half of SCR-DA-006's payload. */
    suspend fun onboardingStatus(vehicleId: Ulid): VehicleOnboardingStatusResponse =
        registry.getVehicleOnboardingStatus(vehicleId)

    /**
     * The vehicle itself — the other half.
     *
     * SCR-DA-006's header is *"Sedan · ABC-1234"* and the onboarding-status read carries neither
     * the type nor the plate, so the two reads go together.
     */
    suspend fun vehicle(vehicleId: Ulid): VehicleDetail = registry.getVehicle(vehicleId)

    /**
     * Step 1/4 for a vehicle that does not exist yet — `POST /v1/vehicles`.
     *
     * **The registration is the `details` step** (Δ C029): this request carries exactly the type
     * and plate that step stores, D5' §14.1a verifies it on entry, and the response's `nextStep`
     * is therefore `insurance`. Asking for the plate again on Step 1 would be a screen the driver
     * has already filled in.
     *
     * `409 registration-exists` when the plate is on a live vehicle (D-37).
     */
    suspend fun start(registrationNumber: String, vehicleType: RideVehicleType): SavedStep {
        val response = registry.registerVehicle(
            VehicleRegistration(
                registrationNumber = registrationNumber.trim(),
                vehicleType = vehicleType,
                mode = ServiceMode.C,
            ),
        )

        return SavedStep(
            vehicleId = response.vehicleId,
            stepStatus = response.verification.vehicleDetails,
            onboardingStatus = response.onboardingStatus,
            registrationStatus = response.status,
            nextStep = response.nextStep ?: OnboardingStep.INSURANCE,
        )
    }

    /** Step 1/4 for a vehicle that already exists — the driver stepped back to change something. */
    suspend fun saveDetails(vehicleId: Ulid, registrationNumber: String, vehicleType: RideVehicleType): SavedStep =
        registry.uploadVehicleOnboardingStep(
            vehicleId = vehicleId,
            step = OnboardingStep.DETAILS,
            registrationNumber = registrationNumber.trim(),
            vehicleType = vehicleType,
        ).asSavedStep(vehicleId)

    /**
     * Steps 2–4 — the captured document goes up with the step in one request (Δ MCS-01).
     *
     * One request and not two because there is no id for a client to mint: registry-svc writes
     * `docs.uploads` and `registry.onboarding_steps` in the same call, and each image carries its
     * own [lk.mageride.shared.data.models.registry.CaptureSource] (AL-43) rather than the form
     * carrying one for both — a driver can scan the front and pick the back out of the gallery.
     *
     * @param back Step 4/4 only. One photograph cannot show a vehicle's front and back plates.
     */
    suspend fun saveDocument(
        vehicleId: Ulid,
        step: OnboardingStep,
        front: CapturedImage,
        back: CapturedImage? = null,
    ): SavedStep = registry.uploadVehicleOnboardingStep(
        vehicleId = vehicleId,
        step = step,
        file = front.asDocument(),
        fileBack = back?.asDocument(),
    ).asSavedStep(vehicleId)

    /** `POST /v1/vehicles/{id}/deactivate` — US-2.16, and it releases the plate (D-37). */
    suspend fun deactivate(vehicleId: Ulid) {
        registry.deactivateVehicle(vehicleId)
    }

    private fun SaveOnboardingStepResponse.asSavedStep(vehicleId: Ulid): SavedStep = SavedStep(
        vehicleId = vehicleId,
        stepStatus = stepStatus,
        onboardingStatus = onboardingStatus,
        registrationStatus = status,
        nextStep = nextStep,
    )
}

/** Whether this vehicle is one the **driver app** onboards — Mode C, owned (AL-27). */
internal val VehicleSummary.isOnboardable: Boolean get() = mode == ServiceMode.C

/**
 * Whether this vehicle can be selected to go live (US-9.6, D5' §14.1a).
 *
 * An owned Mode C vehicle has to be **approved**: AL-30 makes `onboarding_status` the gate and
 * `status` the registration decision, and both have to be good. A shared or temporarily-assigned
 * Mode A/B vehicle carries neither — the Fleet Portal approved it — so it is eligible on the
 * strength of being assigned at all.
 */
internal val VehicleSummary.canGoLive: Boolean
    get() = if (isOnboardable) {
        status == RegistrationStatus.APPROVED && onboardingStatus == OnboardingStatus.APPROVED
    } else {
        status == RegistrationStatus.APPROVED
    }

/** The first step that is not verified, or `null` when all four are (AL-30). */
internal fun OnboardingStepVerdicts.firstUnverified(): OnboardingStep? =
    OnboardingStep.entries.firstOrNull { verdictFor(it) != StepVerdict.VERIFIED }

/** This step's verdict. The read DTO keys them by name, so this is the one place that maps them. */
internal fun OnboardingStepVerdicts.verdictFor(step: OnboardingStep): StepVerdict = when (step) {
    OnboardingStep.DETAILS -> details
    OnboardingStep.INSURANCE -> insurance
    OnboardingStep.REVENUE -> revenue
    OnboardingStep.PHOTOS -> photos
}

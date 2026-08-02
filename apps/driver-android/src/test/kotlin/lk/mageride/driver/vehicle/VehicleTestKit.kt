package lk.mageride.driver.vehicle

import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.FieldSource
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.VerifyStatus
import lk.mageride.shared.data.models.registry.OnboardingStatus
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.OnboardingStepVerdicts
import lk.mageride.shared.data.models.registry.RegisterVehicleResponse
import lk.mageride.shared.data.models.registry.RegistrationStatus
import lk.mageride.shared.data.models.registry.SaveOnboardingStepResponse
import lk.mageride.shared.data.models.registry.StepVerdict
import lk.mageride.shared.data.models.registry.VehicleDetail
import lk.mageride.shared.data.models.registry.VehicleOnboardingStatusResponse
import lk.mageride.shared.data.models.registry.VehicleSummary
import lk.mageride.shared.data.models.registry.VehicleVerificationVerdicts
import lk.mageride.shared.testing.fixture.Fixtures

/** The vehicle every C069 test is about. */
internal const val VEHICLE_ID: Ulid = "01JVEHICLE0000000000000001"

/** A second one, for the "only one can be live" and the two-group rules. */
internal const val OTHER_VEHICLE_ID: Ulid = "01JVEHICLE0000000000000002"

/**
 * A vehicle as `GET /v1/vehicles/mine` returns it.
 *
 * Mode C by default because that is what the Driver App onboards (AL-27); pass [ServiceMode.A] or
 * [ServiceMode.B] for the temporarily-assigned fleet vehicle of US-13.9.
 */
@Suppress("LongParameterList")
internal fun summary(
    vehicleId: Ulid = VEHICLE_ID,
    registrationNumber: String = "ABC-1234",
    vehicleType: VehicleType = VehicleType.SEDAN,
    mode: ServiceMode = ServiceMode.C,
    status: RegistrationStatus = RegistrationStatus.PENDING,
    onboardingStatus: OnboardingStatus = OnboardingStatus.INCOMPLETE,
): VehicleSummary = VehicleSummary(
    vehicleId = vehicleId,
    registrationNumber = registrationNumber,
    vehicleType = vehicleType,
    mode = mode,
    status = status,
    onboardingStatus = onboardingStatus,
)

/** The four step verdicts, defaulting to "nothing saved yet". */
internal fun verdicts(
    details: StepVerdict = StepVerdict.PENDING_INPUT,
    insurance: StepVerdict = StepVerdict.PENDING_INPUT,
    revenue: StepVerdict = StepVerdict.PENDING_INPUT,
    photos: StepVerdict = StepVerdict.PENDING_INPUT,
): OnboardingStepVerdicts = OnboardingStepVerdicts(details, insurance, revenue, photos)

/** `GET /v1/vehicles/{id}/onboarding-status`. */
internal fun onboardingStatus(
    steps: OnboardingStepVerdicts,
    nextStep: OnboardingStep? = null,
    status: RegistrationStatus = RegistrationStatus.PENDING,
    onboardingStatus: OnboardingStatus = OnboardingStatus.INCOMPLETE,
    fields: List<ExtractedField> = emptyList(),
): VehicleOnboardingStatusResponse = VehicleOnboardingStatusResponse(
    status = status,
    onboardingStatus = onboardingStatus,
    nextStep = nextStep,
    steps = steps,
    fields = fields,
)

/** `GET /v1/vehicles/{id}` — SCR-DA-006's header comes from this one. */
internal fun detail(
    registrationNumber: String = "ABC-1234",
    vehicleType: VehicleType = VehicleType.SEDAN,
    status: RegistrationStatus = RegistrationStatus.PENDING,
    onboardingStatus: OnboardingStatus = OnboardingStatus.INCOMPLETE,
    rejectionReason: String? = null,
): VehicleDetail = VehicleDetail(
    vehicleId = VEHICLE_ID,
    registrationNumber = registrationNumber,
    vehicleType = vehicleType,
    mode = ServiceMode.C,
    status = status,
    onboardingStatus = onboardingStatus,
    rejectionReason = rejectionReason,
)

/**
 * `POST /v1/vehicles` — 201.
 *
 * `vehicleDetails` is VERIFIED and `nextStep` is `insurance` because **registration saves Step
 * 1/4** (Δ C029): the request carries exactly what the `details` step stores, and D5' §14.1a
 * verifies that step on entry.
 */
internal fun registered(
    nextStep: OnboardingStep? = OnboardingStep.INSURANCE,
    status: RegistrationStatus = RegistrationStatus.PENDING,
): RegisterVehicleResponse = RegisterVehicleResponse(
    vehicleId = VEHICLE_ID,
    status = status,
    ocrJobId = null,
    registrationNumber = "ABC-1234",
    verification = VehicleVerificationVerdicts(
        vehicleDetails = StepVerdict.VERIFIED,
        insurance = StepVerdict.PENDING_INPUT,
        revenueLicense = StepVerdict.PENDING_INPUT,
        photos = StepVerdict.PENDING_INPUT,
    ),
    onboardingStatus = OnboardingStatus.INCOMPLETE,
    nextStep = nextStep,
    createdAt = Fixtures.NOW,
)

/** `PUT /v1/vehicles/{id}/onboarding/{step}` — 200. */
internal fun stepSaved(
    stepStatus: StepVerdict = StepVerdict.VERIFIED,
    nextStep: OnboardingStep? = null,
    onboardingStatus: OnboardingStatus = OnboardingStatus.INCOMPLETE,
    status: RegistrationStatus = RegistrationStatus.PENDING,
): SaveOnboardingStepResponse = SaveOnboardingStepResponse(
    stepStatus = stepStatus,
    onboardingStatus = onboardingStatus,
    status = status,
    nextStep = nextStep,
)

/** One extracted field, pending or confirmed (AL-29). */
internal fun field(key: String, value: String?, verifyStatus: VerifyStatus = VerifyStatus.CONFIRMED): ExtractedField =
    ExtractedField(
        key = key,
        value = value,
        source = FieldSource.AI,
        confidence = if (verifyStatus == VerifyStatus.CONFIRMED) CONFIDENT else DOUBTFUL,
        verifyStatus = verifyStatus,
    )

/** [ActiveVehicleStore] in memory — a local unit test's `SharedPreferences` answers defaults. */
internal class FakeActiveVehicleStore(override var activeVehicleId: Ulid? = null) : ActiveVehicleStore

private const val CONFIDENT = 0.97
private const val DOUBTFUL = 0.42

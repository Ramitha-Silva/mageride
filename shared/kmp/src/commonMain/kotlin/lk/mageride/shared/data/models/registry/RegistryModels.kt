package lk.mageride.shared.data.models.registry

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.AccessRequestStatus
import lk.mageride.shared.data.models.DocumentKind
import lk.mageride.shared.data.models.DocumentStatus
import lk.mageride.shared.data.models.ExtractedField
import lk.mageride.shared.data.models.PhoneMasked
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType

// registry-svc — driver profile, vehicles, Mode-C onboarding, Mode B sharing, device binding.
// Source: backend/contracts/registry.yaml (D3' "registry-svc — vehicles, sharing, device
// binding, merchant").
//
// Scope fence: the Driver App onboards MODE C ONLY. Mode A/B vehicles, route permits and fleet
// documents are onboarded in the Fleet Portal; a Mode A/B body here is 403 mode-not-allowed.
//
// Vehicle onboarding is a PERSISTED FOUR-STEP STATE MACHINE (AL-30): each step is saved on its
// own and re-opening the wizard resumes at the first non-VERIFIED step, never at step 1. When all
// four are VERIFIED the service auto-approves with no Verification Officer step (user decision
// 6/22); anything PENDING_REVIEW puts the vehicle in the officer queue.

/** 15-digit tracker IMEI (`registry.yaml#/components/schemas/Imei`). */
public typealias Imei = String

/**
 * Vehicle registration status (`registry.vehicles.status` CHECK, C003).
 *
 * [DEACTIVATED] and [REJECTED] both release the plate from the D-37 active-set uniqueness index,
 * so the same registration number can be onboarded again later.
 */
@Serializable
public enum class RegistrationStatus {
    PENDING,
    APPROVED,
    REJECTED,
    DEACTIVATED,
}

/**
 * Whether a vehicle's four onboarding steps are all done
 * (`registry.vehicles.onboarding_status` CHECK, AL-30).
 *
 * Derived, never set directly: [APPROVED] once all four steps are verified or confirmed, and only
 * an approved Mode-C vehicle is go-live eligible.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class OnboardingStatus(public val wire: String) {
    @SerialName("incomplete")
    INCOMPLETE("incomplete"),

    @SerialName("approved")
    APPROVED("approved"),
}

/**
 * One of the four Mode-C onboarding steps (`registry.onboarding_steps.step` CHECK).
 *
 * The order below is the wizard's order and is what `nextStep` walks.
 *
 * @property wire The value as it appears on the wire and in the `{step}` path segment.
 */
@Serializable
public enum class OnboardingStep(public val wire: String) {
    @SerialName("details")
    DETAILS("details"),

    @SerialName("insurance")
    INSURANCE("insurance"),

    @SerialName("revenue")
    REVENUE("revenue"),

    @SerialName("photos")
    PHOTOS("photos"),
}

/**
 * The verdict on one onboarding step (`registry.yaml#/components/schemas/StepVerdict`).
 *
 * - [PENDING_INPUT] — not yet saved.
 * - [PENDING_REVIEW] — saved, but holding a doubtful, driver-entered or plate-mismatched field,
 *   so it sits in the Verification Officer queue.
 * - [VERIFIED] — clean. Four of these auto-approve the vehicle.
 */
@Serializable
public enum class StepVerdict {
    VERIFIED,
    PENDING_REVIEW,
    PENDING_INPUT,
}

/**
 * Whether a vehicle is eligible for dispatch (`registry.vehicles.dispatch_state` CHECK, E-03).
 *
 * [DISPATCH_SUSPENDED] is set automatically when a required document expires — approval and
 * dispatch eligibility are deliberately two different things.
 */
@Serializable
public enum class DispatchState {
    ACTIVE,
    DISPATCH_SUSPENDED,
}

/**
 * Whether a Mode B vehicle charges its subscribers (`registry.vehicles.mode_b_billing` CHECK).
 *
 * The UI label is **"Service payment"** (AL-51); the API name is unchanged for stability. `null`
 * for Mode A and Mode C vehicles — neither has subscribers.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class ModeBBilling(public val wire: String) {
    @SerialName("paid")
    PAID("paid"),

    @SerialName("free")
    FREE("free"),
}

/**
 * A Mode B subscriber's entitlement state (`subscription.grants.status` CHECK, C005).
 *
 * An [UNSUBSCRIBED] row is **not deleted** — it stays on the roster, muted, until the owner
 * hard-deletes it (US-4.12, US-NEW.1).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class GrantStatus(public val wire: String) {
    @SerialName("active")
    ACTIVE("active"),

    @SerialName("unsubscribed")
    UNSUBSCRIBED("unsubscribed"),
}

// ---------------------------------------------------------------------------------------------
// Driver identity
// ---------------------------------------------------------------------------------------------

/**
 * Where one uploaded onboarding image came from
 * (`registry.yaml#/components/schemas/CaptureSource`, AL-43).
 *
 * **Δ MCS-01.** Stored on `docs.uploads.captured_via` and declared **per file part**, never per
 * request: a driver scans the licence through SCR-DA/DI-005 and picks the avatar out of the
 * gallery in one submission, so a single value for the whole form would be wrong about one of
 * them.
 *
 * [GALLERY] is permitted and **flagged** — a file already on the handset is how a document
 * belonging to somebody else arrives, and it is what the Verification-Officer queue sorts on. It
 * has no default for the same reason: claiming [CAMERA_DRAG_CROP] would record a scan that did
 * not happen.
 *
 * The column's third value, `other`, is the Fleet Portal's desktop file picker (AL-50) and is not
 * part of this enum — an app has no way to produce it.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class CaptureSource(public val wire: String) {
    /** The in-app camera document-scanner with the drag-crop quad (SCR-DA/DI-005). */
    @SerialName("camera_dragcrop")
    CAMERA_DRAG_CROP("camera_dragcrop"),

    /** Picked from the handset's gallery. */
    @SerialName("gallery")
    GALLERY("gallery"),
}

/**
 * `PUT /v1/drivers/profile` — Profile Setup (SCR-DA/DI-003a).
 *
 * Precedes Home and needs no vehicle. Queues Gemini Flash 3.0 extraction of licence number,
 * expiry, NIC and allowed vehicle types (AL-29). **The profile photo is required.**
 *
 * Anything the driver supplies because the scan was unclear ([nicNo], [allowedVehicleTypes]) is
 * stored `source='manual'`, `verifyStatus='pending'` and routes to the officer queue (US-2.4a).
 *
 * @property driverName At most 200 characters.
 * @property profilePhotoFileId An already-uploaded `docs.uploads` id.
 * @property licenseFrontFileId Front of the driving licence.
 * @property licenseBackFileId Back of the driving licence.
 * @property nicNo Driver-supplied only when the scan was unclear.
 * @property allowedVehicleTypes Types the licence permits; driver-supplied fallback for OCR.
 */
@Serializable
public data class UpsertDriverProfileRequest(
    val driverName: String,
    val profilePhotoFileId: Ulid,
    val licenseFrontFileId: Ulid,
    val licenseBackFileId: Ulid,
    val nicNo: String? = null,
    val allowedVehicleTypes: List<VehicleType>? = null,
)

/**
 * `GET /v1/drivers/profile` — 200, or a literal `null` when Profile Setup has not been done
 * (Δ MCS-05).
 *
 * The boot router's read. **Not** [UpsertDriverProfileResponse] with an empty `fields`: the AL-29
 * extracted values belong to the licence documents and come back from the write, on the screen
 * that can act on them. Saying they were empty is a different claim from not having fetched them.
 *
 * @property driverId The driver this profile belongs to.
 * @property status `PENDING` while an identity field is still with a Verification Officer.
 * @property displayName The name Profile Setup stored.
 * @property photoUrl The stored profile photo (US-2.12).
 * @property nicNo NIC as stored, whether extracted or driver-supplied.
 * @property allowedVehicleTypes The licence classes as stored.
 */
@Serializable
public data class DriverProfileSummary(
    val driverId: Ulid,
    val status: RegistrationStatus,
    val displayName: String,
    val photoUrl: String? = null,
    val nicNo: String? = null,
    val allowedVehicleTypes: List<VehicleType> = emptyList(),
)

/**
 * `PUT /v1/drivers/profile` — 200.
 *
 * The screen that follows Profile Setup shows the driver their own name and classes back, so the
 * response carries what was **stored** rather than leaving the client to assume its request won —
 * which is untrue whenever OCR supplied a NIC or a licence class the request did not carry
 * (Δ C029).
 *
 * @property driverId The driver whose profile was written.
 * @property status Registration status after the write.
 * @property displayName The name as stored.
 * @property photoUrl The stored profile photo.
 * @property nicNo NIC as stored, whether extracted or driver-supplied.
 * @property allowedVehicleTypes The licence classes as stored.
 * @property fields Per-field OCR verdicts; what the officer screen renders.
 */
@Serializable
public data class UpsertDriverProfileResponse(
    val driverId: Ulid,
    val status: RegistrationStatus,
    val displayName: String,
    val photoUrl: String? = null,
    val nicNo: String? = null,
    val allowedVehicleTypes: List<VehicleType> = emptyList(),
    val fields: List<ExtractedField> = emptyList(),
)

// ---------------------------------------------------------------------------------------------
// Vehicles
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/vehicles` (`registry.yaml#/components/schemas/VehicleRegistration`).
 *
 * Mode A/B and `train` are rejected `403 mode-not-allowed` — they belong to the Fleet Portal and
 * admin-bff. That is why [vehicleType] is a [RideVehicleType] and [mode] is pinned to
 * [ServiceMode.C] by the contract's `const: C`.
 *
 * Once a vehicle is approved, calling this again starts a **new** vehicle at step 1/4 (AL-30).
 *
 * **The four document ids are optional** (Δ C029). Supplied, the vehicle is onboarded in one shot
 * and can come back approved from this single call — the behaviour D3' describes. Absent, this
 * call carries exactly the `details` step (type + plate) and the wizard walks the other three
 * through `PUT /v1/vehicles/{id}/onboarding/{step}` — the behaviour AL-30 describes, and the only
 * one the driver app can produce, because nothing mints a `docs.uploads` id before an upload.
 *
 * @property registrationNumber Plate, at most 32 characters. Unique within the active set (D-37).
 * @property vehicleType Mode-C driver-app types only (AL-09).
 * @property mode Always [ServiceMode.C]; the contract declares it `const`.
 * @property insuranceFileId Uploaded insurance document.
 * @property revenueLicenseFileId Uploaded revenue licence.
 * @property vehiclePhotoFrontFileId Front photo; its plate OCR must match [registrationNumber].
 * @property vehiclePhotoBackFileId Back photo.
 * @property driverName Defaults from `registry.driver_profiles` (Profile Setup).
 * @property driverPhotoFileId Overrides the profile photo for this vehicle.
 */
@OptIn(ExperimentalSerializationApi::class)
@Serializable
public data class VehicleRegistration(
    val registrationNumber: String,
    val vehicleType: RideVehicleType,
    // `const: C` and `required`, so it is forced onto the wire despite `encodeDefaults = false`.
    @EncodeDefault(EncodeDefault.Mode.ALWAYS)
    val mode: ServiceMode = ServiceMode.C,
    val insuranceFileId: Ulid? = null,
    val revenueLicenseFileId: Ulid? = null,
    val vehiclePhotoFrontFileId: Ulid? = null,
    val vehiclePhotoBackFileId: Ulid? = null,
    val driverName: String? = null,
    val driverPhotoFileId: Ulid? = null,
)

/**
 * The four per-document verdicts returned when a vehicle is created.
 *
 * Spelled `revenueLicense` here and `revenue` in [OnboardingStepVerdicts] because the two
 * contract schemas spell them differently — both are landed as written rather than reconciled
 * client-side.
 *
 * @property vehicleDetails Entered registration number and type.
 * @property insurance Insurance document — expiry extraction.
 * @property revenueLicense Revenue licence — number and expiry extraction.
 * @property photos Plate OCR against the entered registration number.
 */
@Serializable
public data class VehicleVerificationVerdicts(
    val vehicleDetails: StepVerdict,
    val insurance: StepVerdict,
    val revenueLicense: StepVerdict,
    val photos: StepVerdict,
)

/**
 * `POST /v1/vehicles` — 201.
 *
 * @property vehicleId The new vehicle.
 * @property status Registration status; [RegistrationStatus.APPROVED] straight away when all four
 *   documents came back verified (Change 6/22).
 * @property ocrJobId The queued extraction job. `null` when the registration carried no documents
 *   — a wizard's Step 1/4 queues no extraction, and inventing an id no service recognises would
 *   leave a client polling it forever (Δ C029).
 * @property registrationNumber The plate as stored.
 * @property verification Per-document verdicts.
 * @property onboardingStatus Derived from the four steps.
 * @property nextStep Where the wizard opens next. Registration **saves Step 1/4** — this request
 *   carries exactly what the `details` step stores — so a fresh vehicle resumes at
 *   [OnboardingStep.INSURANCE] and the client does not have to ask (Δ C029).
 * @property createdAt When the vehicle row was created.
 */
@Serializable
public data class RegisterVehicleResponse(
    val vehicleId: Ulid,
    val status: RegistrationStatus,
    val ocrJobId: Ulid? = null,
    val registrationNumber: String,
    val verification: VehicleVerificationVerdicts,
    val onboardingStatus: OnboardingStatus,
    val nextStep: OnboardingStep? = null,
    val createdAt: Timestamp,
)

/**
 * A vehicle as My Vehicles renders it (`registry.yaml#/components/schemas/VehicleSummary`).
 *
 * @property vehicleId The vehicle.
 * @property registrationNumber Plate.
 * @property vehicleType Canonical type (AL-09).
 * @property mode Operating mode.
 * @property status Registration status.
 * @property onboardingStatus Incomplete / approved (AL-30).
 * @property modeBBilling "Service payment" setting; `null` for Mode A and Mode C.
 * @property defaultMonthlyFareMinor Default per-subscriber monthly fare, minor units.
 * @property fleetName The assigning fleet, for SCR-DA/DI-026's "Temporarily assigned to me"
 *   caption (Δ MCS-02, US-13.9). `null` on an owned vehicle.
 * @property assignedUntil When that assignment lapses. `null` on an owned vehicle **and** on an
 *   open-ended assignment. Display only — the eligibility view stops returning an expired
 *   assignment on its own, so a client never has to check this to decide eligibility.
 */
@Serializable
public data class VehicleSummary(
    val vehicleId: Ulid,
    val registrationNumber: String,
    val vehicleType: VehicleType,
    val mode: ServiceMode,
    val status: RegistrationStatus,
    val onboardingStatus: OnboardingStatus,
    val modeBBilling: ModeBBilling? = null,
    val defaultMonthlyFareMinor: Long? = null,
    val fleetName: String? = null,
    val assignedUntil: Timestamp? = null,
)

/** The driver shown to passengers for a vehicle (US-2.12) — cosmetic, not verified identity. */
@Serializable
public data class VehicleDriverProfile(
    val driverId: Ulid? = null,
    val name: String? = null,
    val photoUrl: String? = null,
)

/**
 * One stored document on a vehicle (`registry.yaml#/components/schemas/VehicleDocument`).
 *
 * @property docId The document.
 * @property kind Which slot it fills.
 * @property status Expiry state; [DocumentStatus.EXPIRED] auto-suspends dispatch (E-03).
 * @property expiresAt Document expiry, where the kind has one.
 */
@Serializable
public data class VehicleDocument(
    val docId: Ulid,
    val kind: DocumentKind,
    val status: DocumentStatus,
    val expiresAt: Timestamp? = null,
)

/**
 * A vehicle with everything the owner, an assigned driver or an internal role may see
 * (`registry.yaml#/components/schemas/VehicleDetail` — `allOf(VehicleSummary, …)`, flattened).
 *
 * @property dispatchState E-03 document-expiry auto-suspend.
 * @property rejectionReason Why the registration was rejected, when it was.
 * @property fleetId The owning fleet, when the vehicle belongs to one.
 * @property driver The driver profile shown to passengers.
 * @property documents Every document on file.
 * @property createdAt When the vehicle row was created.
 */
@Serializable
public data class VehicleDetail(
    val vehicleId: Ulid,
    val registrationNumber: String,
    val vehicleType: VehicleType,
    val mode: ServiceMode,
    val status: RegistrationStatus,
    val onboardingStatus: OnboardingStatus,
    val modeBBilling: ModeBBilling? = null,
    val defaultMonthlyFareMinor: Long? = null,
    val dispatchState: DispatchState? = null,
    val rejectionReason: String? = null,
    val fleetId: Ulid? = null,
    val driver: VehicleDriverProfile? = null,
    val documents: List<VehicleDocument>? = null,
    val createdAt: Timestamp? = null,
)

/**
 * `GET /v1/vehicles/mine` — 200 (US-2.8).
 *
 * @property items The caller's vehicles, each Incomplete or Approved (AL-30).
 */
@Serializable
public data class VehicleListResponse(val items: List<VehicleSummary> = emptyList())

/**
 * `GET /v1/vehicles/{vehicleId}/status` — 200 (US-2.13/2.15).
 *
 * @property status Registration status.
 * @property rejectionReason Present when [status] is [RegistrationStatus.REJECTED].
 */
@Serializable
public data class VehicleStatusResponse(val status: RegistrationStatus, val rejectionReason: String? = null)

/**
 * The four step verdicts keyed as the onboarding-status read spells them.
 *
 * @property details Registration number and vehicle type.
 * @property insurance Insurance document.
 * @property revenue Revenue licence.
 * @property photos Vehicle photos and their plate OCR.
 */
@Serializable
public data class OnboardingStepVerdicts(
    val details: StepVerdict,
    val insurance: StepVerdict,
    val revenue: StepVerdict,
    val photos: StepVerdict,
)

/**
 * `GET /v1/vehicles/{vehicleId}/onboarding-status` — 200 (SCR-DA/DI-006).
 *
 * [nextStep] drives resume: the wizard opens the first step that is not
 * [StepVerdict.VERIFIED], **never step 1**, and is `null` once every step is verified.
 *
 * @property status Registration status.
 * @property onboardingStatus Derived incomplete / approved.
 * @property nextStep Where to resume; `null` when nothing is left.
 * @property steps Per-step verdicts.
 * @property fields Per-field source, confidence and verification state.
 */
@Serializable
public data class VehicleOnboardingStatusResponse(
    val status: RegistrationStatus,
    val onboardingStatus: OnboardingStatus,
    val nextStep: OnboardingStep? = null,
    val steps: OnboardingStepVerdicts,
    val fields: List<ExtractedField> = emptyList(),
)

/**
 * The JSON arm of `PUT /v1/vehicles/{vehicleId}/onboarding/{step}`
 * (`registry.yaml#/components/schemas/OnboardingStepInput`).
 *
 * The `details` step carries [registrationNumber] + [vehicleType]; the document steps carry the
 * uploaded file ids. The multipart arm of the same operation posts the captured images instead —
 * it is the shape the AL-43 drag-crop scanner uses, and C013 builds it directly.
 *
 * @property registrationNumber Plate, at most 32 characters.
 * @property vehicleType Mode-C types only.
 * @property fileId Front/primary document for this step.
 * @property fileIdBack Back of a two-sided document.
 * @property fields Driver-entered corrections; each lands `manual` / `pending`.
 */
@Serializable
public data class OnboardingStepInput(
    val registrationNumber: String? = null,
    val vehicleType: RideVehicleType? = null,
    val fileId: Ulid? = null,
    val fileIdBack: Ulid? = null,
    val fields: Map<String, String>? = null,
)

/**
 * Driver corrections to a step's extracted fields (Δ MCS-02, AL-29 / BR-25.3).
 *
 * **Not a map.** `multipart/form-data` has no object type, and the keys a step accepts are fixed
 * per document kind by `DocumentFieldKeys.AcceptedFor` — so naming them lets the contract say
 * what it takes and the compiler stop a caller sending `reg_no_match`, which is the plate check
 * and not the driver's to answer.
 *
 * Every value here lands `source='manual'`, `verifyStatus='pending'` and takes its step back to
 * [StepVerdict.PENDING_REVIEW]. That is by design: BR-25.2 lets the driver carry on and trusts
 * the value only once a Verification Officer confirms it.
 *
 * @property insuranceExpiry Step 2/4 — the one field D5' §14.1a verifies insurance on.
 * @property insurancePolicyNo Step 2/4, an extra ocr-svc returns.
 * @property revenueNo Step 3/4 — verified together with the expiry.
 * @property revenueExpiry Step 3/4.
 */
public data class OnboardingCorrections(
    val insuranceExpiry: String? = null,
    val insurancePolicyNo: String? = null,
    val revenueNo: String? = null,
    val revenueExpiry: String? = null,
) {
    /** Whether anything is actually being corrected. An empty set must not be sent. */
    public val isEmpty: Boolean
        get() = insuranceExpiry == null && insurancePolicyNo == null &&
            revenueNo == null && revenueExpiry == null
}

/**
 * `PUT /v1/vehicles/{vehicleId}/onboarding/{step}` — 200.
 *
 * @property stepStatus Verdict on the step just saved.
 * @property onboardingStatus Derived status after the save.
 * @property nextStep Where the wizard resumes; `null` when every step is verified.
 * @property status Registration status **after** this save. Saving the fourth verified step
 *   auto-approves the vehicle with no officer step (AL-27), so the response that caused it says so
 *   rather than making the app poll (Δ C029).
 * @property ocrJobId The extraction queued by this step's document, where it queued one.
 */
@Serializable
public data class SaveOnboardingStepResponse(
    val stepStatus: StepVerdict,
    val onboardingStatus: OnboardingStatus,
    val status: RegistrationStatus,
    val nextStep: OnboardingStep? = null,
    val ocrJobId: Ulid? = null,
)

/**
 * `PUT /v1/vehicles/{vehicleId}/driver-profile` (US-2.12). Cosmetic only.
 *
 * @property name Display name shown to passengers.
 * @property photoUrl Display photo shown to passengers.
 */
@Serializable
public data class UpdateVehicleDriverProfileRequest(val name: String? = null, val photoUrl: String? = null)

/**
 * `POST /v1/vehicles/{vehicleId}/device` (US-3.1).
 *
 * A thin owner-facing wrapper over provisioning-svc's `POST /v1/trackers/bind` (T-02) — the
 * credential mint, the anti-clone quarantine and the Redis `imei:{imei}` cache all live there.
 *
 * @property imei The tracker's 15-digit IMEI.
 */
@Serializable
public data class BindVehicleDeviceRequest(val imei: Imei)

/**
 * `POST /v1/vehicles/{vehicleId}/device` — 201.
 *
 * @property bindingId The `prov.tracker_bindings` row.
 */
@Serializable
public data class BindVehicleDeviceResponse(val bindingId: Ulid)

// ---------------------------------------------------------------------------------------------
// Mode B sharing (D-22/D-23)
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/vehicles/{vehicleId}/share` (US-4.1/4.2).
 *
 * @property userId The passenger being granted visibility.
 * @property expiresAt When the grant lapses; open-ended when omitted.
 */
@Serializable
public data class CreateShareGrantRequest(val userId: Ulid, val expiresAt: Timestamp? = null)

/**
 * `POST /v1/vehicles/{vehicleId}/share` — 201. The grant is pending until the sharee accepts.
 *
 * @property grantId The created grant.
 */
@Serializable
public data class CreateShareGrantResponse(val grantId: Ulid)

/**
 * `POST /v1/vehicles/{vehicleId}/share/{grantId}/accept` — 200 (US-4.3b).
 *
 * Visibility begins here, not at grant creation.
 *
 * @property grantId The accepted grant.
 * @property status Always [GrantStatus.ACTIVE]; the contract's enum has that one value.
 */
@Serializable
public data class AcceptShareGrantResponse(val grantId: Ulid, val status: GrantStatus)

/**
 * A passenger entitled to see a Mode B vehicle (`registry.yaml#/components/schemas/Subscriber`).
 *
 * @property userId The passenger.
 * @property name Passenger name.
 * @property phoneMasked Role-masked MSISDN (AL-40/41/42).
 * @property status Whether the grant is live or the passenger has unsubscribed.
 * @property grantedAt When visibility started.
 */
@Serializable
public data class Subscriber(
    val userId: Ulid,
    val name: String? = null,
    val phoneMasked: PhoneMasked? = null,
    val status: GrantStatus,
    val grantedAt: Timestamp? = null,
)

/**
 * `POST /v1/share-requests` (US-4.5) — the generic vehicle-scoped access request.
 *
 * The Mode B marker tap on the passenger map uses subscription-svc's richer
 * `POST /v1/mode-b/{vehicleId}/access-requests` instead, which also starts a subscription on
 * acceptance (AL-24, Epic 23).
 *
 * @property vehicleId The vehicle to request access to.
 */
@Serializable
public data class RequestVehicleAccessRequest(val vehicleId: Ulid)

/**
 * `POST /v1/share-requests` — 201.
 *
 * @property requestId The raised request.
 * @property status Decision state; [AccessRequestStatus.PENDING] on creation.
 */
@Serializable
public data class RequestVehicleAccessResponse(val requestId: Ulid, val status: AccessRequestStatus)

// ---------------------------------------------------------------------------------------------
// Driver payout profile (AL-58/AL-59) — Δ MCS-03
// ---------------------------------------------------------------------------------------------

/**
 * `PUT /v1/drivers/payout-profile` — the driver's bank details.
 *
 * **Any edit re-enters `pending_verification`** (AL-59): the account a payout lands in is not a
 * field an approved profile lets you change quietly.
 *
 * @property bank The bank.
 * @property branch The branch.
 * @property accountNo Account number, at most 40 characters.
 * @property accountHolderName As it appears on the account, at most 200.
 */
@Serializable
public data class UpsertDriverPayoutProfileRequest(
    val bank: String,
    val branch: String,
    val accountNo: String,
    val accountHolderName: String,
)

/**
 * One version of a driver's bank and payout details (Δ AL-58/AL-59).
 *
 * MageRide takes custody of card fares and discharges it through the payout rail, so this is what
 * a payout is actually sent to — and why it is Verification-Officer approved through the AL-39
 * queue rather than trusted as typed.
 *
 * @property bank The bank.
 * @property branch The branch.
 * @property accountNo Account number.
 * @property accountHolderName As it appears on the account.
 * @property proofDocId The bank statement or passbook first page.
 * @property lankaqrDocId The driver's own bank-app LankaQR image (AL-59) — the same code a
 *   passenger scans to pay them directly.
 * @property status Where the version stands.
 * @property rejectionReason Why it was refused, when it was.
 * @property verifiedAt When an officer approved it.
 */
@Serializable
public data class DriverPayoutProfile(
    val bank: String,
    val branch: String,
    val accountNo: String,
    val accountHolderName: String,
    val proofDocId: Ulid? = null,
    val lankaqrDocId: Ulid? = null,
    val status: PayoutProfileStatus,
    val rejectionReason: String? = null,
    val verifiedAt: Timestamp? = null,
)

/**
 * Where a payout profile version stands (`registry.driver_payout_profiles.status`).
 *
 * [SUPERSEDED] is what an edit does to the version before it — versions are kept, not overwritten,
 * because a payout that has already gone out was sent to the details of its day.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class PayoutProfileStatus(public val wire: String) {
    @SerialName("pending_verification")
    PENDING_VERIFICATION("pending_verification"),

    @SerialName("verified")
    VERIFIED("verified"),

    @SerialName("rejected")
    REJECTED("rejected"),

    @SerialName("superseded")
    SUPERSEDED("superseded"),
}

/** Which payout document is being uploaded (AL-58). @property wire The value on the wire. */
@Serializable
public enum class PayoutDocumentKind(public val wire: String) {
    @SerialName("bank_statement")
    BANK_STATEMENT("bank_statement"),

    @SerialName("passbook_first_page")
    PASSBOOK_FIRST_PAGE("passbook_first_page"),

    @SerialName("lankaqr_code")
    LANKAQR_CODE("lankaqr_code"),
}

/**
 * `POST /v1/drivers/payout-profile/documents` — 201.
 *
 * @property docId The stored document.
 * @property kind Which slot it filled.
 */
@Serializable
public data class UploadedPayoutDocument(val docId: Ulid, val kind: PayoutDocumentKind)

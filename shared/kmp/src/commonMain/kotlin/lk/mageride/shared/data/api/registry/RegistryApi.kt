package lk.mageride.shared.data.api.registry

import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.CapturedDocument
import lk.mageride.shared.data.api.apiDelete
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.capturedDocumentPart
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.multipartBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.api.textPart
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.registry.AcceptShareGrantResponse
import lk.mageride.shared.data.models.registry.BindVehicleDeviceRequest
import lk.mageride.shared.data.models.registry.BindVehicleDeviceResponse
import lk.mageride.shared.data.models.registry.CreateShareGrantRequest
import lk.mageride.shared.data.models.registry.CreateShareGrantResponse
import lk.mageride.shared.data.models.registry.OnboardingCorrections
import lk.mageride.shared.data.models.registry.OnboardingStep
import lk.mageride.shared.data.models.registry.OnboardingStepInput
import lk.mageride.shared.data.models.registry.RegisterVehicleResponse
import lk.mageride.shared.data.models.registry.RequestVehicleAccessRequest
import lk.mageride.shared.data.models.registry.RequestVehicleAccessResponse
import lk.mageride.shared.data.models.registry.SaveOnboardingStepResponse
import lk.mageride.shared.data.models.registry.Subscriber
import lk.mageride.shared.data.models.registry.UpdateVehicleDriverProfileRequest
import lk.mageride.shared.data.models.registry.UpsertDriverProfileRequest
import lk.mageride.shared.data.models.registry.UpsertDriverProfileResponse
import lk.mageride.shared.data.models.registry.VehicleDetail
import lk.mageride.shared.data.models.registry.VehicleListResponse
import lk.mageride.shared.data.models.registry.VehicleOnboardingStatusResponse
import lk.mageride.shared.data.models.registry.VehicleRegistration
import lk.mageride.shared.data.models.registry.VehicleStatusResponse

/**
 * registry-svc — driver identity, vehicles, onboarding, sharing and device binding
 * (`backend/contracts/registry.yaml`).
 *
 * The Change 6/22 split is visible in the shape of this interface: [upsertDriverProfile] is
 * driver *identity* (name, photo, licence) and stands alone, while the Mode-C vehicle is
 * onboarded in four steps ([saveVehicleOnboardingStep]) with Gemini Flash 3.0 auto-verify
 * (AL-29/AL-30). Mode A/B vehicles and permits are the Fleet Portal's business, not this
 * client's.
 */
@Suppress("TooManyFunctions")
public interface RegistryApi {

    /**
     * `PUT /v1/drivers/profile` with a JSON body — the driver identity, by upload id.
     *
     * The three `…FileId`s must already be on file. Use [uploadDriverProfile] to send the images
     * themselves instead; on a mobile client that is almost always the one you want, because
     * nothing else mints those ids.
     */
    public suspend fun upsertDriverProfile(request: UpsertDriverProfileRequest): UpsertDriverProfileResponse

    /**
     * `PUT /v1/drivers/profile` with `multipart/form-data` — Profile Setup in one request
     * (SCR-DA/DI-003a).
     *
     * **Δ MCS-01.** The images go up with the name, land in `docs.uploads` and are what the JSON
     * arm's ids refer to. Before this arm existed no route on the platform created a
     * `docs.uploads` row for an onboarding document, so this screen could not be completed at all.
     *
     * Each image carries its own [CapturedDocument.capturedVia] (AL-43): the licence usually comes
     * from the SCR-DA/DI-005 scanner and the avatar from the gallery, in the same submission.
     *
     * `413 payload-too-large` above the per-image ceiling.
     *
     * @param nicNo Sent only when the scan was unclear and the driver typed it — registry-svc is
     *   what stamps it `source='manual'`, `verify_status='pending'` (AL-29, US-2.4a).
     * @param licenceNo Same, for the licence number (Δ MCS-02 — SCR-DA/DI-003a draws a ✎ on it
     *   and until now nothing could carry the correction).
     * @param licenceExpiry Same, for its expiry date.
     */
    public suspend fun uploadDriverProfile(
        driverName: String,
        photo: CapturedDocument,
        licenseFront: CapturedDocument,
        licenseBack: CapturedDocument,
        nicNo: String? = null,
        allowedVehicleTypes: List<VehicleType>? = null,
        licenceNo: String? = null,
        licenceExpiry: String? = null,
    ): UpsertDriverProfileResponse

    /**
     * `POST /v1/vehicles` — register a Mode-C vehicle. Attested (D-30).
     *
     * `409 registration-exists` when the plate is already on the platform.
     */
    public suspend fun registerVehicle(
        request: VehicleRegistration,
        idempotencyKey: String? = null,
    ): RegisterVehicleResponse

    /** `GET /v1/vehicles/mine` — every vehicle this user owns or has been granted. */
    public suspend fun listMyVehicles(): VehicleListResponse

    /** `GET /v1/vehicles/{vehicleId}` — the full record, documents included. */
    public suspend fun getVehicle(vehicleId: Ulid): VehicleDetail

    /** `GET /v1/vehicles/{vehicleId}/status` — approval status and any rejection reason. */
    public suspend fun getVehicleStatus(vehicleId: Ulid): VehicleStatusResponse

    /** `GET /v1/vehicles/{vehicleId}/onboarding-status` — per-step verdicts and the resume point. */
    public suspend fun getVehicleOnboardingStatus(vehicleId: Ulid): VehicleOnboardingStatusResponse

    /**
     * `PUT /v1/vehicles/{vehicleId}/onboarding/{step}` with a JSON body.
     *
     * The JSON arm references files already uploaded (`fileId`, `fileIdBack`). Use
     * [uploadVehicleOnboardingStep] to send the bytes in the same request instead — the contract
     * declares both media types for this one operation.
     */
    public suspend fun saveVehicleOnboardingStep(
        vehicleId: Ulid,
        step: OnboardingStep,
        request: OnboardingStepInput,
    ): SaveOnboardingStepResponse

    /**
     * `PUT /v1/vehicles/{vehicleId}/onboarding/{step}` with `multipart/form-data`.
     *
     * The drag-crop capture path (AL-43): a perspective-corrected document image goes up with
     * the step's fields in one request. `413 payload-too-large` above the ceiling.
     *
     * **Δ MCS-01: registry-svc now implements this arm**, and each image carries its own capture
     * source. The arm was declared from the start and the service bound JSON only, so a form
     * posted here reached a handler that saw no fields at all.
     *
     * **Δ MCS-02: [corrections] may be sent with no [file].** BR-25.3 lets a driver edit a
     * doubtful extracted value, and asking them to re-photograph the document to retype its
     * expiry is the roadside experience that rule exists to avoid. The step must already own a
     * document; correcting one that has none is `400 validation-failed`.
     */
    public suspend fun uploadVehicleOnboardingStep(
        vehicleId: Ulid,
        step: OnboardingStep,
        registrationNumber: String? = null,
        vehicleType: RideVehicleType? = null,
        file: CapturedDocument? = null,
        fileBack: CapturedDocument? = null,
        corrections: OnboardingCorrections? = null,
    ): SaveOnboardingStepResponse

    /** `POST /v1/vehicles/{vehicleId}/deactivate` — take a vehicle out of service. */
    public suspend fun deactivateVehicle(vehicleId: Ulid, idempotencyKey: String? = null)

    /** `PUT /v1/vehicles/{vehicleId}/driver-profile` — the driver shown to passengers. */
    public suspend fun updateVehicleDriverProfile(
        vehicleId: Ulid,
        request: UpdateVehicleDriverProfileRequest,
    ): VehicleDetail

    /**
     * `POST /v1/vehicles/{vehicleId}/device` — bind a hardware tracker by IMEI (T-02/T-03).
     *
     * `409 imei-duplicate` is the anti-clone check (T-08), not a retryable conflict.
     */
    public suspend fun bindVehicleDevice(
        vehicleId: Ulid,
        request: BindVehicleDeviceRequest,
        idempotencyKey: String? = null,
    ): BindVehicleDeviceResponse

    /** `POST /v1/vehicles/{vehicleId}/share` — offer a passenger access to a Mode A/B vehicle. */
    public suspend fun createShareGrant(
        vehicleId: Ulid,
        request: CreateShareGrantRequest,
        idempotencyKey: String? = null,
    ): CreateShareGrantResponse

    /** `POST /v1/vehicles/{vehicleId}/share/{grantId}/accept` — the invited user accepts. */
    public suspend fun acceptShareGrant(
        vehicleId: Ulid,
        grantId: Ulid,
        idempotencyKey: String? = null,
    ): AcceptShareGrantResponse

    /** `DELETE /v1/vehicles/{vehicleId}/share/{grantId}` — the owner withdraws a grant. */
    public suspend fun revokeShareGrant(vehicleId: Ulid, grantId: Ulid)

    /** `GET /v1/vehicles/{vehicleId}/subscribers` — who can see this vehicle, one page at a time. */
    public suspend fun listVehicleSubscribers(vehicleId: Ulid, page: PageRequest = PageRequest.FIRST): Page<Subscriber>

    /** `DELETE /v1/vehicles/{vehicleId}/subscribers/{userId}` — drop a subscriber. */
    public suspend fun unsubscribeFromVehicle(vehicleId: Ulid, userId: Ulid)

    /** `POST /v1/share-requests` — a passenger asks an owner for access. */
    public suspend fun requestVehicleAccess(
        request: RequestVehicleAccessRequest,
        idempotencyKey: String? = null,
    ): RequestVehicleAccessResponse
}

@Suppress("TooManyFunctions")
internal class KtorRegistryApi(private val transport: ApiTransport) : RegistryApi {

    override suspend fun upsertDriverProfile(request: UpsertDriverProfileRequest): UpsertDriverProfileResponse =
        transport.apiPut(SERVICE, "upsertDriverProfile", "/v1/drivers/profile") { jsonBody(request) }.decode()

    override suspend fun uploadDriverProfile(
        driverName: String,
        photo: CapturedDocument,
        licenseFront: CapturedDocument,
        licenseBack: CapturedDocument,
        nicNo: String?,
        allowedVehicleTypes: List<VehicleType>?,
        licenceNo: String?,
        licenceExpiry: String?,
    ): UpsertDriverProfileResponse = transport.apiPut(SERVICE, "upsertDriverProfile", "/v1/drivers/profile") {
        multipartBody {
            textPart("driverName", driverName)
            capturedDocumentPart("photo", photo)
            capturedDocumentPart("licenseFront", licenseFront)
            capturedDocumentPart("licenseBack", licenseBack)
            textPart("nicNo", nicNo)
            textPart("licenceNo", licenceNo)
            textPart("licenceExpiry", licenceExpiry)
            // Repeated rather than joined: `multipart/form-data` has no array type, the contract
            // accepts either, and a repeated field cannot be broken by a value containing a comma.
            allowedVehicleTypes?.forEach { type -> textPart("allowedVehicleTypes", type.wire) }
        }
    }.decode()

    override suspend fun registerVehicle(
        request: VehicleRegistration,
        idempotencyKey: String?,
    ): RegisterVehicleResponse = transport.apiPost(
        service = SERVICE,
        operationId = "registerVehicle",
        path = VEHICLES_PATH,
        idempotencyKey = idempotencyKey,
        attested = true,
    ) { jsonBody(request) }.decode()

    override suspend fun listMyVehicles(): VehicleListResponse =
        transport.apiGet(SERVICE, "listMyVehicles", "$VEHICLES_PATH/mine").decode()

    override suspend fun getVehicle(vehicleId: Ulid): VehicleDetail =
        transport.apiGet(SERVICE, "getVehicle", "$VEHICLES_PATH/$vehicleId").decode()

    override suspend fun getVehicleStatus(vehicleId: Ulid): VehicleStatusResponse =
        transport.apiGet(SERVICE, "getVehicleStatus", "$VEHICLES_PATH/$vehicleId/status").decode()

    override suspend fun getVehicleOnboardingStatus(vehicleId: Ulid): VehicleOnboardingStatusResponse =
        transport.apiGet(SERVICE, "getVehicleOnboardingStatus", "$VEHICLES_PATH/$vehicleId/onboarding-status").decode()

    override suspend fun saveVehicleOnboardingStep(
        vehicleId: Ulid,
        step: OnboardingStep,
        request: OnboardingStepInput,
    ): SaveOnboardingStepResponse = transport.apiPut(SERVICE, "saveVehicleOnboardingStep", stepPath(vehicleId, step)) {
        jsonBody(request)
    }.decode()

    override suspend fun uploadVehicleOnboardingStep(
        vehicleId: Ulid,
        step: OnboardingStep,
        registrationNumber: String?,
        vehicleType: RideVehicleType?,
        file: CapturedDocument?,
        fileBack: CapturedDocument?,
        corrections: OnboardingCorrections?,
    ): SaveOnboardingStepResponse = transport.apiPut(SERVICE, "saveVehicleOnboardingStep", stepPath(vehicleId, step)) {
        multipartBody {
            textPart("registrationNumber", registrationNumber)
            textPart("vehicleType", vehicleType?.wire)
            file?.let { capturedDocumentPart("file", it) }
            fileBack?.let { capturedDocumentPart("fileBack", it) }
            textPart("insuranceExpiry", corrections?.insuranceExpiry)
            textPart("insurancePolicyNo", corrections?.insurancePolicyNo)
            textPart("revenueNo", corrections?.revenueNo)
            textPart("revenueExpiry", corrections?.revenueExpiry)
        }
    }.decode()

    override suspend fun deactivateVehicle(vehicleId: Ulid, idempotencyKey: String?) {
        transport.apiPost(SERVICE, "deactivateVehicle", "$VEHICLES_PATH/$vehicleId/deactivate", idempotencyKey)
    }

    override suspend fun updateVehicleDriverProfile(
        vehicleId: Ulid,
        request: UpdateVehicleDriverProfileRequest,
    ): VehicleDetail =
        transport.apiPut(SERVICE, "updateVehicleDriverProfile", "$VEHICLES_PATH/$vehicleId/driver-profile") {
            jsonBody(request)
        }.decode()

    override suspend fun bindVehicleDevice(
        vehicleId: Ulid,
        request: BindVehicleDeviceRequest,
        idempotencyKey: String?,
    ): BindVehicleDeviceResponse = transport.apiPost(
        service = SERVICE,
        operationId = "bindVehicleDevice",
        path = "$VEHICLES_PATH/$vehicleId/device",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    override suspend fun createShareGrant(
        vehicleId: Ulid,
        request: CreateShareGrantRequest,
        idempotencyKey: String?,
    ): CreateShareGrantResponse = transport.apiPost(
        service = SERVICE,
        operationId = "createShareGrant",
        path = "$VEHICLES_PATH/$vehicleId/share",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    override suspend fun acceptShareGrant(
        vehicleId: Ulid,
        grantId: Ulid,
        idempotencyKey: String?,
    ): AcceptShareGrantResponse = transport.apiPost(
        service = SERVICE,
        operationId = "acceptShareGrant",
        path = "$VEHICLES_PATH/$vehicleId/share/$grantId/accept",
        idempotencyKey = idempotencyKey,
    ).decode()

    override suspend fun revokeShareGrant(vehicleId: Ulid, grantId: Ulid) {
        transport.apiDelete(SERVICE, "revokeShareGrant", "$VEHICLES_PATH/$vehicleId/share/$grantId")
    }

    override suspend fun listVehicleSubscribers(vehicleId: Ulid, page: PageRequest): Page<Subscriber> =
        transport.apiGet(SERVICE, "listVehicleSubscribers", "$VEHICLES_PATH/$vehicleId/subscribers") {
            pageParameters(page)
        }.decode()

    override suspend fun unsubscribeFromVehicle(vehicleId: Ulid, userId: Ulid) {
        transport.apiDelete(SERVICE, "unsubscribeFromVehicle", "$VEHICLES_PATH/$vehicleId/subscribers/$userId")
    }

    override suspend fun requestVehicleAccess(
        request: RequestVehicleAccessRequest,
        idempotencyKey: String?,
    ): RequestVehicleAccessResponse = transport.apiPost(
        service = SERVICE,
        operationId = "requestVehicleAccess",
        path = "/v1/share-requests",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    private fun stepPath(vehicleId: Ulid, step: OnboardingStep): String =
        "$VEHICLES_PATH/$vehicleId/onboarding/${step.wire}"

    private companion object {
        val SERVICE = ApiService.REGISTRY
        const val VEHICLES_PATH = "/v1/vehicles"
    }
}

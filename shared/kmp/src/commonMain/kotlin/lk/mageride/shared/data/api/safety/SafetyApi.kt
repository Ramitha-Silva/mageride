package lk.mageride.shared.data.api.safety

import io.ktor.http.encodeURLPathPart
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.Credential
import lk.mageride.shared.data.api.apiDelete
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.safety.BlockDriverRequest
import lk.mageride.shared.data.models.safety.ReportVehicleRequest
import lk.mageride.shared.data.models.safety.SharedTripView
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosEvent
import lk.mageride.shared.data.models.safety.TriggerSosRequest
import lk.mageride.shared.data.models.safety.TripShareLink
import lk.mageride.shared.data.models.safety.VehicleReport

/**
 * safety-svc — SOS, trip sharing, vehicle reports and driver blocks
 * (`backend/contracts/safety.yaml`, D-33/D-34).
 *
 * [triggerSos] is attested (D-30) and is one of the sensitive mutations D3' §0 names. It is also
 * the call least able to tolerate a slow path: it fans out an SMS to the user's emergency
 * contact, so `400 no-emergency-contact` is a **setup** failure the app should have prevented on
 * the safety screen, not something to surface mid-emergency.
 *
 * [getSharedTrip] is the one route in this module with `security: []` — it is read by a
 * recipient who has only the token, from a browser, with no MageRide account (AL-44).
 */
public interface SafetyApi {

    /** `POST /v1/sos` — raise an SOS from a ride or from standing still. Attested (D-30). */
    public suspend fun triggerSos(request: TriggerSosRequest, idempotencyKey: String? = null): SosDispatched

    /** `GET /v1/sos/{userId}/history` — past SOS events for this user. */
    public suspend fun listSosHistory(userId: Ulid, page: PageRequest = PageRequest.FIRST): Page<SosEvent>

    /**
     * `POST /v1/trip-share/{tripId}` — mint a share token and URL (D-34).
     *
     * `409 ride-terminal` once the trip has ended: there is nothing left to follow.
     */
    public suspend fun createTripShare(tripId: Ulid, idempotencyKey: String? = null): TripShareLink

    /** `DELETE /v1/trip-share/{tripId}` — revoke the link; the token becomes `410 Gone`. */
    public suspend fun revokeTripShare(tripId: Ulid)

    /**
     * `GET /v1/trip-share/public/{token}` — the recipient's read-only live view.
     *
     * **Unauthenticated by contract.** `404 token-unknown`, `410 token-expired-or-revoked` and
     * `429 rate-limited` are the whole error surface — no PII beyond what the shared view carries.
     */
    public suspend fun getSharedTrip(token: String): SharedTripView

    /** `POST /v1/reports/vehicle` — report a vehicle or its driver. */
    public suspend fun reportVehicle(request: ReportVehicleRequest, idempotencyKey: String? = null): VehicleReport

    /**
     * `POST /v1/drivers/{driverId}/block` — never match me with this driver again.
     *
     * Feeds reputation-svc's `block_status` (D-04, E-07), which dispatch consults before it
     * offers.
     */
    public suspend fun blockDriver(driverId: Ulid, request: BlockDriverRequest, idempotencyKey: String? = null)

    /** `DELETE /v1/drivers/{driverId}/block` — lift the block. */
    public suspend fun unblockDriver(driverId: Ulid)
}

internal class KtorSafetyApi(private val transport: ApiTransport) : SafetyApi {

    override suspend fun triggerSos(request: TriggerSosRequest, idempotencyKey: String?): SosDispatched =
        transport.apiPost(
            service = SERVICE,
            operationId = "triggerSos",
            path = "/v1/sos",
            idempotencyKey = idempotencyKey,
            attested = true,
        ) { jsonBody(request) }.decode()

    override suspend fun listSosHistory(userId: Ulid, page: PageRequest): Page<SosEvent> =
        transport.apiGet(SERVICE, "listSosHistory", "/v1/sos/$userId/history") { pageParameters(page) }.decode()

    override suspend fun createTripShare(tripId: Ulid, idempotencyKey: String?): TripShareLink =
        transport.apiPost(SERVICE, "createTripShare", "$TRIP_SHARE_PATH/$tripId", idempotencyKey).decode()

    override suspend fun revokeTripShare(tripId: Ulid) {
        transport.apiDelete(SERVICE, "revokeTripShare", "$TRIP_SHARE_PATH/$tripId")
    }

    override suspend fun getSharedTrip(token: String): SharedTripView = transport.apiGet(
        service = SERVICE,
        operationId = "getSharedTrip",
        path = "$TRIP_SHARE_PATH/public/${token.encodeURLPathPart()}",
        credential = Credential.NONE,
    ).decode()

    override suspend fun reportVehicle(request: ReportVehicleRequest, idempotencyKey: String?): VehicleReport =
        transport.apiPost(SERVICE, "reportVehicle", "/v1/reports/vehicle", idempotencyKey) {
            jsonBody(request)
        }.decode()

    override suspend fun blockDriver(driverId: Ulid, request: BlockDriverRequest, idempotencyKey: String?) {
        transport.apiPost(SERVICE, "blockDriver", blockPath(driverId), idempotencyKey) { jsonBody(request) }
    }

    override suspend fun unblockDriver(driverId: Ulid) {
        transport.apiDelete(SERVICE, "unblockDriver", blockPath(driverId))
    }

    private fun blockPath(driverId: Ulid): String = "/v1/drivers/$driverId/block"

    private companion object {
        val SERVICE = ApiService.SAFETY
        const val TRIP_SHARE_PATH = "/v1/trip-share"
    }
}

package lk.mageride.shared.data.api.dispatch

import io.ktor.client.request.parameter
import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.apiDelete
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.apiPut
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.api.pageParameters
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.DirectionalConfig
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCleared
import lk.mageride.shared.data.models.dispatch.DirectionalFilterCreated
import lk.mageride.shared.data.models.dispatch.DirectionalFilterState
import lk.mageride.shared.data.models.dispatch.DriverLevelAfterNoShow
import lk.mageride.shared.data.models.dispatch.DriverLevelResponse
import lk.mageride.shared.data.models.dispatch.DriverStatsResponse
import lk.mageride.shared.data.models.dispatch.GoOnlineRequest
import lk.mageride.shared.data.models.dispatch.JobBoardIntentResponse
import lk.mageride.shared.data.models.dispatch.LevelConfig
import lk.mageride.shared.data.models.dispatch.OutstandingPenalties
import lk.mageride.shared.data.models.dispatch.PresenceResponse
import lk.mageride.shared.data.models.dispatch.ReportDriverNoShowRequest
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.SetDirectionalFilterRequest
import lk.mageride.shared.data.models.dispatch.SettlePenaltiesRequest
import lk.mageride.shared.data.models.dispatch.SettledPenalties

/**
 * dispatch-svc — driver presence, Directional Travel, the Job Board and the Driver Level System
 * (`backend/contracts/dispatch.yaml`).
 *
 * Offers themselves are not here: a candidate driver is *notified* over MQTT/FCM and accepts
 * through [lk.mageride.shared.data.api.ride.RideApi], because ride-svc is the sole writer of the
 * ride aggregate (R-01). What dispatch owns is everything that decides *who gets offered*.
 *
 * C015 owns the Driver Level rules; the level and stats reads here are just the numbers.
 */
@Suppress("TooManyFunctions")
public interface DispatchApi {

    /**
     * `POST /v1/standby/online` — go on standby with a vehicle and a position.
     *
     * `409 driver-already-live` when another session or ride is already running.
     */
    public suspend fun goOnline(request: GoOnlineRequest, idempotencyKey: String? = null): PresenceResponse

    /** `POST /v1/standby/offline` — stop receiving offers. */
    public suspend fun goOffline(idempotencyKey: String? = null): PresenceResponse

    /** `GET /v1/standby/directional` — the active Directional filter and what is left of it (DT-08). */
    public suspend fun getDirectionalFilter(): DirectionalFilterState

    /**
     * `POST /v1/standby/directional` — only offer rides heading my way (DT-01, DT-03).
     *
     * `403 not-online` off standby; `409 directional-limit-reached` once the day's uses are gone.
     */
    public suspend fun setDirectionalFilter(
        request: SetDirectionalFilterRequest,
        idempotencyKey: String? = null,
    ): DirectionalFilterCreated

    /** `DELETE /v1/standby/directional` — clear the filter and take any ride again. */
    public suspend fun clearDirectionalFilter(): DirectionalFilterCleared

    /** `POST /v1/rides/schedule` — a passenger books ahead onto the Job Board. Attested (D-30). */
    public suspend fun scheduleRide(request: ScheduleRideRequest, idempotencyKey: String? = null): ScheduledRide

    /** `DELETE /v1/rides/schedule/{scheduledRideId}` — withdraw a scheduled ride. */
    public suspend fun cancelScheduledRide(scheduledRideId: Ulid)

    /** `GET /v1/rides/scheduled/{driverId}` — what this driver has already claimed. */
    public suspend fun listDriverScheduledRides(
        driverId: Ulid,
        page: PageRequest = PageRequest.FIRST,
    ): Page<ScheduledRide>

    /**
     * `GET /v1/rides/job-board` — scheduled rides near a point, open to intents.
     *
     * @param radiusMetres Search radius; the contract's own bounds are 1 000–30 000 m.
     */
    public suspend fun listJobBoard(
        lat: Double,
        lng: Double,
        radiusMetres: Int? = null,
        page: PageRequest = PageRequest.FIRST,
    ): Page<ScheduledRide>

    /** `POST /v1/rides/job-board/{rideId}/intent` — register interest in a Job Board ride. */
    public suspend fun postJobBoardIntent(rideId: Ulid, idempotencyKey: String? = null): JobBoardIntentResponse

    /** `GET /v1/drivers/{driverId}/level` — the Driver Level and progress to the next one. */
    public suspend fun getDriverLevel(driverId: Ulid): DriverLevelResponse

    /** `GET /v1/drivers/{driverId}/stats` — acceptance rate, no-shows and points. */
    public suspend fun getDriverStats(driverId: Ulid): DriverStatsResponse

    /**
     * `POST /v1/internal/drivers/{driverId}/no-show` — ride-svc reports a driver no-show.
     *
     * **Service-to-service (mTLS).** Present for contract coverage; not reachable from an app.
     */
    public suspend fun reportDriverNoShow(
        driverId: Ulid,
        request: ReportDriverNoShowRequest,
        idempotencyKey: String? = null,
    ): DriverLevelAfterNoShow

    /** `PUT /v1/admin/dispatch/directional-config` — Admin Portal tuning of DT-01..08. */
    public suspend fun updateDirectionalConfig(request: DirectionalConfig): DirectionalConfig

    /** `PUT /v1/admin/drivers/level-config` — Admin Portal tuning of the Driver Level System. */
    public suspend fun updateDriverLevelConfig(request: LevelConfig): LevelConfig

    /**
     * `GET /v1/internal/passengers/{passengerId}/penalties` — unsettled cross-trip debt (D5' §7.1).
     *
     * **Service-to-service (mTLS).** fare-svc reads this before pricing a passenger's next
     * completed trip; US-6A.10b's re-enablement is evaluated against the same total. Not reachable
     * from an app.
     */
    public suspend fun listOutstandingPenalties(passengerId: Ulid): OutstandingPenalties

    /**
     * `POST /v1/internal/passengers/{passengerId}/penalties/settle` — record the debt collected.
     *
     * **Service-to-service (mTLS).** The write half of D5' §7.1, called by fare-svc **after** it
     * has posted the ledger entries: the money is fare-svc's (D-09) and this row is only the
     * debt's record. A second call with the same ride settles nothing.
     */
    public suspend fun settleOutstandingPenalties(
        passengerId: Ulid,
        request: SettlePenaltiesRequest,
        idempotencyKey: String? = null,
    ): SettledPenalties
}

@Suppress("TooManyFunctions")
internal class KtorDispatchApi(private val transport: ApiTransport) : DispatchApi {

    override suspend fun goOnline(request: GoOnlineRequest, idempotencyKey: String?): PresenceResponse =
        transport.apiPost(SERVICE, "goOnline", "/v1/standby/online", idempotencyKey) { jsonBody(request) }.decode()

    override suspend fun goOffline(idempotencyKey: String?): PresenceResponse =
        transport.apiPost(SERVICE, "goOffline", "/v1/standby/offline", idempotencyKey).decode()

    override suspend fun getDirectionalFilter(): DirectionalFilterState =
        transport.apiGet(SERVICE, "getDirectionalFilter", DIRECTIONAL_PATH).decode()

    override suspend fun setDirectionalFilter(
        request: SetDirectionalFilterRequest,
        idempotencyKey: String?,
    ): DirectionalFilterCreated = transport.apiPost(SERVICE, "setDirectionalFilter", DIRECTIONAL_PATH, idempotencyKey) {
        jsonBody(request)
    }.decode()

    override suspend fun clearDirectionalFilter(): DirectionalFilterCleared =
        transport.apiDelete(SERVICE, "clearDirectionalFilter", DIRECTIONAL_PATH).decode()

    override suspend fun scheduleRide(request: ScheduleRideRequest, idempotencyKey: String?): ScheduledRide =
        transport.apiPost(
            service = SERVICE,
            operationId = "scheduleRide",
            path = SCHEDULE_PATH,
            idempotencyKey = idempotencyKey,
            attested = true,
        ) { jsonBody(request) }.decode()

    override suspend fun cancelScheduledRide(scheduledRideId: Ulid) {
        transport.apiDelete(SERVICE, "cancelScheduledRide", "$SCHEDULE_PATH/$scheduledRideId")
    }

    override suspend fun listDriverScheduledRides(driverId: Ulid, page: PageRequest): Page<ScheduledRide> =
        transport.apiGet(SERVICE, "listDriverScheduledRides", "/v1/rides/scheduled/$driverId") {
            pageParameters(page)
        }.decode()

    override suspend fun listJobBoard(
        lat: Double,
        lng: Double,
        radiusMetres: Int?,
        page: PageRequest,
    ): Page<ScheduledRide> = transport.apiGet(SERVICE, "listJobBoard", "/v1/rides/job-board") {
        parameter("lat", lat)
        parameter("lng", lng)
        parameter("radius", radiusMetres)
        pageParameters(page)
    }.decode()

    override suspend fun postJobBoardIntent(rideId: Ulid, idempotencyKey: String?): JobBoardIntentResponse =
        transport.apiPost(
            service = SERVICE,
            operationId = "postJobBoardIntent",
            path = "/v1/rides/job-board/$rideId/intent",
            idempotencyKey = idempotencyKey,
        ).decode()

    override suspend fun getDriverLevel(driverId: Ulid): DriverLevelResponse =
        transport.apiGet(SERVICE, "getDriverLevel", "$DRIVERS_PATH/$driverId/level").decode()

    override suspend fun getDriverStats(driverId: Ulid): DriverStatsResponse =
        transport.apiGet(SERVICE, "getDriverStats", "$DRIVERS_PATH/$driverId/stats").decode()

    override suspend fun reportDriverNoShow(
        driverId: Ulid,
        request: ReportDriverNoShowRequest,
        idempotencyKey: String?,
    ): DriverLevelAfterNoShow = transport.apiPost(
        service = SERVICE,
        operationId = "reportDriverNoShow",
        path = "/v1/internal/drivers/$driverId/no-show",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    override suspend fun updateDirectionalConfig(request: DirectionalConfig): DirectionalConfig =
        transport.apiPut(SERVICE, "updateDirectionalConfig", "/v1/admin/dispatch/directional-config") {
            jsonBody(request)
        }.decode()

    override suspend fun updateDriverLevelConfig(request: LevelConfig): LevelConfig =
        transport.apiPut(SERVICE, "updateDriverLevelConfig", "/v1/admin/drivers/level-config") {
            jsonBody(request)
        }.decode()

    override suspend fun listOutstandingPenalties(passengerId: Ulid): OutstandingPenalties =
        transport.apiGet(SERVICE, "listOutstandingPenalties", "/v1/internal/passengers/$passengerId/penalties").decode()

    override suspend fun settleOutstandingPenalties(
        passengerId: Ulid,
        request: SettlePenaltiesRequest,
        idempotencyKey: String?,
    ): SettledPenalties = transport.apiPost(
        service = SERVICE,
        operationId = "settleOutstandingPenalties",
        path = "/v1/internal/passengers/$passengerId/penalties/settle",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    private companion object {
        val SERVICE = ApiService.DISPATCH
        const val DIRECTIONAL_PATH = "/v1/standby/directional"
        const val SCHEDULE_PATH = "/v1/rides/schedule"
        const val DRIVERS_PATH = "/v1/drivers"
    }
}

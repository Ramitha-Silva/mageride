package lk.mageride.shared.data.api.trip

import lk.mageride.shared.data.api.ApiService
import lk.mageride.shared.data.api.ApiTransport
import lk.mageride.shared.data.api.apiGet
import lk.mageride.shared.data.api.apiPost
import lk.mageride.shared.data.api.decode
import lk.mageride.shared.data.api.decodeOrNull
import lk.mageride.shared.data.api.jsonBody
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.trip.AutoEndSessionRequest
import lk.mageride.shared.data.models.trip.DriverRatingInput
import lk.mageride.shared.data.models.trip.Rating
import lk.mageride.shared.data.models.trip.RatingInput
import lk.mageride.shared.data.models.trip.Session
import lk.mageride.shared.data.models.trip.StartSessionRequest

/**
 * trip-state-svc — Mode A/B tracking sessions and their ratings
 * (`backend/contracts/trip-state.yaml`).
 *
 * **This is not ride-svc.** trip-state-svc owns Mode A (scheduled public transport) and Mode B
 * (shared private vehicle); Mode C on-demand rides belong to
 * [lk.mageride.shared.data.api.ride.RideApi], and that boundary is never crossed (CLAUDE.md).
 * A session is a *vehicle running its route*, not a booking: it has no fare, no offer and no
 * passenger of its own.
 */
public interface TripStateApi {

    /**
     * `POST /v1/sessions/start` — put a Mode A/B vehicle live on its route.
     *
     * `409 driver-already-live` when this driver already has a session running; a vehicle that
     * has not cleared onboarding is `403 vehicle-not-approved`.
     */
    public suspend fun startSession(request: StartSessionRequest, idempotencyKey: String? = null): Session

    /** `POST /v1/sessions/{sessionId}/end` — stop tracking. */
    public suspend fun endSession(sessionId: Ulid, idempotencyKey: String? = null): Session

    /**
     * `POST /v1/sessions/{sessionId}/restart` — resume a session ended by mistake.
     *
     * Only inside the window the session reports as `restartableUntil`; after that it is
     * `410 Gone`.
     */
    public suspend fun restartSession(sessionId: Ulid, idempotencyKey: String? = null): Session

    /**
     * `GET /v1/sessions/{vehicleId}/active` — the vehicle's live session, or `null`.
     *
     * `null` is the ordinary answer for a parked vehicle: the contract's response is
     * `oneOf(Session, null)`, not a `404`.
     */
    public suspend fun getActiveSession(vehicleId: Ulid): Session?

    /** `POST /v1/sessions/{sessionId}/rating` — a passenger rates the journey. */
    public suspend fun ratePassengerJourney(
        sessionId: Ulid,
        request: RatingInput,
        idempotencyKey: String? = null,
    ): Rating

    /** `POST /v1/sessions/{sessionId}/driver-rating` — the driver rates a named passenger. */
    public suspend fun rateSessionPassenger(
        sessionId: Ulid,
        request: DriverRatingInput,
        idempotencyKey: String? = null,
    ): Rating

    /**
     * `POST /v1/internal/sessions/{sessionId}/auto-end` — tracker-driven auto-end (AL-32).
     *
     * **Service-to-service (mTLS).** Present for contract coverage; not reachable from an app.
     */
    public suspend fun autoEndSession(
        sessionId: Ulid,
        request: AutoEndSessionRequest,
        idempotencyKey: String? = null,
    ): Session
}

internal class KtorTripStateApi(private val transport: ApiTransport) : TripStateApi {

    override suspend fun startSession(request: StartSessionRequest, idempotencyKey: String?): Session =
        transport.apiPost(SERVICE, "startSession", "$SESSIONS_PATH/start", idempotencyKey) {
            jsonBody(request)
        }.decode()

    override suspend fun endSession(sessionId: Ulid, idempotencyKey: String?): Session =
        transport.apiPost(SERVICE, "endSession", "$SESSIONS_PATH/$sessionId/end", idempotencyKey).decode()

    override suspend fun restartSession(sessionId: Ulid, idempotencyKey: String?): Session =
        transport.apiPost(SERVICE, "restartSession", "$SESSIONS_PATH/$sessionId/restart", idempotencyKey).decode()

    override suspend fun getActiveSession(vehicleId: Ulid): Session? =
        transport.apiGet(SERVICE, "getActiveSession", "$SESSIONS_PATH/$vehicleId/active")
            .decodeOrNull(transport.json)

    override suspend fun ratePassengerJourney(sessionId: Ulid, request: RatingInput, idempotencyKey: String?): Rating =
        transport.apiPost(
            service = SERVICE,
            operationId = "ratePassengerJourney",
            path = "$SESSIONS_PATH/$sessionId/rating",
            idempotencyKey = idempotencyKey,
        ) { jsonBody(request) }.decode()

    override suspend fun rateSessionPassenger(
        sessionId: Ulid,
        request: DriverRatingInput,
        idempotencyKey: String?,
    ): Rating = transport.apiPost(
        service = SERVICE,
        operationId = "rateSessionPassenger",
        path = "$SESSIONS_PATH/$sessionId/driver-rating",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    override suspend fun autoEndSession(
        sessionId: Ulid,
        request: AutoEndSessionRequest,
        idempotencyKey: String?,
    ): Session = transport.apiPost(
        service = SERVICE,
        operationId = "autoEndSession",
        path = "/v1/internal/sessions/$sessionId/auto-end",
        idempotencyKey = idempotencyKey,
    ) { jsonBody(request) }.decode()

    private companion object {
        val SERVICE = ApiService.TRIP_STATE
        const val SESSIONS_PATH = "/v1/sessions"
    }
}

package lk.mageride.shared.data.models.trip

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// trip-state-svc — Mode A / Mode B tracking sessions.
// Source: backend/contracts/trip-state.yaml (D3' "trip-state-svc — Mode A/B tracking sessions",
// ADD Appendix C).
//
// SCOPE FENCE (CLAUDE.md, R-01): Mode A/B tracking only. The Mode C ride lifecycle belongs to
// ride-svc and that boundary is never crossed — a session request with `mode: C` is rejected, and
// ck_sessions_mode proves it at the database level. Mode A is free: no daily fee is charged for a
// bus or train journey.
//
// A session is the ACTIVE-SESSION MUTEX (D-03, US-9.6): Redis `lock:driver:{driverId}` SETNX plus
// the ux_sessions_active_driver partial unique index, so a driver can never hold two live
// sessions at once.
//
// SPEC DIVERGENCE — the contract and the landed DDL disagree on two enums, and the contract wins
// for the wire (backend/contracts/CLAUDE.md: "If a service and a contract disagree, the contract
// wins"):
//   * SessionState — trip-state.yaml says ACTIVE | ENDED | AUTO_ENDED; ck_sessions_state (C004,
//     from server_db_schema §4 / D4' §4) says ACTIVE | COMPLETED. The contract's three values are
//     the ones the 5-minute restart grace (US-5.10) needs — `restartableUntil` is only meaningful
//     on an auto-ended session, which COMPLETED cannot express.
//   * endReason — trip-state.yaml says driver_ended | idle_timeout | destination_geofence |
//     mqtt_offline; ck_sessions_end_reason says driver_ended | idle_timeout | geofence | admin.
// Modelled per the contract; a micro-change-set for C031 is recorded in the C012 handoff.

/**
 * The state of a Mode A/B tracking session (`trip-state.yaml#/components/schemas/SessionState`).
 *
 * [AUTO_ENDED] is the state the 30-minute idle timer, the 100-metre destination geofence and the
 * MQTT last-will all produce — and the only one a restart is allowed from (US-5.10).
 *
 * See the divergence note at the top of this file: `ck_sessions_state` currently carries
 * `ACTIVE | COMPLETED` instead.
 */
@Serializable
public enum class SessionState {
    ACTIVE,
    ENDED,
    AUTO_ENDED,
}

/**
 * Why a session ended (`trip-state.yaml#/components/schemas/Session.endReason`).
 *
 * The reason is recorded rather than derived, which is what makes the 5-minute restart grace
 * meaningful — only an automatic end qualifies.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SessionEndReason(public val wire: String) {
    @SerialName("driver_ended")
    DRIVER_ENDED("driver_ended"),

    @SerialName("idle_timeout")
    IDLE_TIMEOUT("idle_timeout"),

    @SerialName("destination_geofence")
    DESTINATION_GEOFENCE("destination_geofence"),

    @SerialName("mqtt_offline")
    MQTT_OFFLINE("mqtt_offline"),
    ;

    /** Whether a durable timer or the broker ended the session rather than the driver (US-5.10). */
    public val isAutomatic: Boolean get() = this != DRIVER_ENDED
}

/**
 * The reason a fired timer or the broker gives to `POST /v1/internal/sessions/{id}/auto-end`.
 *
 * A strict subset of [SessionEndReason] — a driver-ended session does not go through this route.
 * Declared separately because the contract declares it separately.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class AutoEndReason(public val wire: String) {
    @SerialName("idle_timeout")
    IDLE_TIMEOUT("idle_timeout"),

    @SerialName("destination_geofence")
    DESTINATION_GEOFENCE("destination_geofence"),

    @SerialName("mqtt_offline")
    MQTT_OFFLINE("mqtt_offline"),
    ;

    /** The matching [SessionEndReason] recorded on the session. */
    public fun toEndReason(): SessionEndReason = SessionEndReason.entries.first { it.wire == wire }
}

/**
 * A driver's live tracking window (`trip-state.yaml#/components/schemas/Session`).
 *
 * @property sessionId The session.
 * @property vehicleId The vehicle being tracked.
 * @property driverId The driver holding the mutex.
 * @property mode [ServiceMode.A] or [ServiceMode.B] — never [ServiceMode.C] (R-01).
 * @property routeId Binds a Mode A bus journey to a `spatial.routes` row.
 * @property state Where the session is.
 * @property autoEndAtDestination Whether the 100-metre destination geofence is armed (US-5.4).
 * @property startedAt When tracking began.
 * @property endedAt When it stopped.
 * @property endReason Why it stopped.
 * @property restartableUntil End of the 5-minute grace window; present only on an auto-ended
 *   session (US-5.10).
 */
@Serializable
public data class Session(
    val sessionId: Ulid,
    val vehicleId: Ulid,
    val driverId: Ulid? = null,
    val mode: ServiceMode,
    val routeId: Ulid? = null,
    val state: SessionState,
    val autoEndAtDestination: Boolean? = null,
    val startedAt: Timestamp,
    val endedAt: Timestamp? = null,
    val endReason: SessionEndReason? = null,
    val restartableUntil: Timestamp? = null,
)

/**
 * `POST /v1/sessions/start`.
 *
 * Takes the active-session mutex, starts the MQTT publish expectation and arms the idle and
 * geofence timers. A driver who is already live gets `409 driver-already-live`.
 *
 * @property vehicleId The vehicle to track. Must be approved, or `403 vehicle-not-approved`.
 * @property mode [ServiceMode.A] or [ServiceMode.B]. [ServiceMode.C] is rejected (R-01).
 * @property routeId Mode A route this journey runs.
 * @property autoEndAtDestination Arms the 100-metre destination geofence.
 */
@Serializable
public data class StartSessionRequest(
    val vehicleId: Ulid,
    val mode: ServiceMode,
    val routeId: Ulid? = null,
    val autoEndAtDestination: Boolean? = null,
)

/**
 * `POST /v1/internal/sessions/{sessionId}/auto-end`. Internal, mTLS only.
 *
 * US-5.9 — the durable timer fires here rather than in the app, so a backgrounded or crashed
 * driver app still leaves a correctly closed session.
 *
 * @property reason Which timer or signal fired.
 */
@Serializable
public data class AutoEndSessionRequest(val reason: AutoEndReason)

/**
 * Body of `POST /v1/sessions/{sessionId}/rating`
 * (`trip-state.yaml#/components/schemas/RatingInput`).
 *
 * @property stars 1–5.
 * @property text Free-form comment, at most 1000 characters. Passenger-written, not platform copy.
 */
@Serializable
public data class RatingInput(val stars: Int, val text: String? = null)

/**
 * Body of `POST /v1/sessions/{sessionId}/driver-rating` — the reciprocal of [RatingInput]
 * (US-18.2), where the subject is the passenger.
 *
 * On the wire this is `allOf(RatingInput, { passengerId })`, flattened as everywhere else.
 *
 * @property passengerId Who is being rated.
 */
@Serializable
public data class DriverRatingInput(val stars: Int, val text: String? = null, val passengerId: Ulid)

/**
 * A recorded rating (`trip-state.yaml#/components/schemas/Rating`).
 *
 * @property ratingId The stored rating.
 * @property stars 1–5.
 * @property text The comment, when one was left.
 * @property createdAt When it was recorded.
 */
@Serializable
public data class Rating(val ratingId: Ulid, val stars: Int, val text: String? = null, val createdAt: Timestamp)

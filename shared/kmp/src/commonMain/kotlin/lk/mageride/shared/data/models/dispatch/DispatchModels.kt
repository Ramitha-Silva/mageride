package lk.mageride.shared.data.models.dispatch

import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid

// dispatch-svc — standby presence, Directional Travel, Job Board, scheduled rides, Driver Level.
// Source: backend/contracts/dispatch.yaml (D3' "dispatch-svc — standby, directional, Job Board,
// level", ADD Appendix C, Δ 2026-06-28 AL-36).
//
// SCOPE FENCE (R-01): the ride lifecycle endpoints ADD Appendix C still lists under dispatch-svc
// — /rides/request, /offer/{driverId}/accept|reject, /rides/{id}/cancel — MOVED TO ride-svc.
// dispatch-svc consumes ride events and emits offer events; it never writes rides.rides.
//
// DIRECTIONAL TRAVEL (DT-01..DT-08) is enforced as ONE ROW PER ACTIVATION:
// COUNT(*) per (driverId, usedDate) <= maxUsesPerDay, in Asia/Colombo. Turning a filter off early
// THEREFORE STILL CONSUMES ITS USE (US-6A.19, anti-gaming) — that is what
// DELETE /v1/standby/directional means. Going offline clears any active filter (DT-04).
//
// JOB BOARD (D-06, US-6A.5): drivers POST INTENT rather than accept. At T-30 min the ride is
// offered to the closest intent-poster by Level, on the ordinary dispatch screen. LEVEL 1 HAS NO
// JOB BOARD ACCESS (US-6A.8).

/**
 * A driver's presence on the Mode C dispatch plane
 * (`dispatch.driver_presence.state` CHECK, C004).
 *
 * One row per driver: going online on a second vehicle overwrites the vehicle, which is the
 * presence-plane echo of the O2 one-accepted-ride invariant.
 */
@Serializable
public enum class PresenceState {
    OFFLINE,
    AVAILABLE,
    OFFERED,
    ON_RIDE,
}

/**
 * Where a scheduled ride has got to (`dispatch.scheduled_rides.status` CHECK, C004).
 *
 * Once [DISPATCHED], cancellation belongs to ride-svc's `POST /v1/rides/{rideId}/cancel`, which
 * owns the penalty matrix — `DELETE /v1/rides/schedule/{id}` answers `409 illegal-transition`
 * from that point on.
 */
@Serializable
public enum class ScheduledRideStatus {
    SCHEDULED,
    DISPATCHED,
    CANCELLED,
}

/**
 * A future pickup, on the Job Board or on a driver's own list
 * (`dispatch.yaml#/components/schemas/ScheduledRide`).
 *
 * @property scheduledRideId The `dispatch.scheduled_rides` row.
 * @property rideId `null` until dispatch materialises the `rides.rides` row at T-30 min.
 * @property pickup Where it starts.
 * @property dropoff Where it ends. **Required at booking** (AL-36).
 * @property vehicleType The booked type.
 * @property pickupTime When the passenger wants to be collected.
 * @property status Scheduled, dispatched or cancelled.
 * @property distanceM Distance from the querying driver, on Job Board reads.
 * @property intentCount How many drivers have posted intent.
 */
@Serializable
public data class ScheduledRide(
    val scheduledRideId: Ulid,
    val rideId: Ulid? = null,
    val pickup: Place,
    val dropoff: Place,
    val vehicleType: RideVehicleType,
    val pickupTime: Timestamp,
    val status: ScheduledRideStatus,
    val distanceM: Int? = null,
    val intentCount: Int? = null,
)

// ---------------------------------------------------------------------------------------------
// Standby presence
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/standby/online` (US-6A.1).
 *
 * The vehicle must be approved; an unapproved or dispatch-suspended one is
 * `403 vehicle-not-approved` (AL-30, E-03).
 *
 * @property vehicleId The vehicle the driver is going online with.
 * @property position Last known position, written to presence and its Redis mirror.
 * @property driverHome Anchor for the D-06 Job Board 30 km radius, if the driver sets one.
 */
@Serializable
public data class GoOnlineRequest(val vehicleId: Ulid, val position: GeoPoint, val driverHome: GeoPoint? = null)

/**
 * The 200 of `POST /v1/standby/online` and `POST /v1/standby/offline`.
 *
 * @property state Presence after the call.
 */
@Serializable
public data class PresenceResponse(val state: PresenceState)

/**
 * `POST /v1/standby/directional` (DT-01/DT-03).
 *
 * Writes one activation row (Asia/Colombo business date), caches it in Redis with the remaining
 * TTL and arms a durable expiry timer plus the 10-minute pre-expiry reminder (US-10.14).
 * Exhausting the daily budget is `409 directional-limit-reached`; setting one while offline is
 * `403 not-online`.
 *
 * @property destination Where the driver is heading.
 * @property label Driver-written shorthand, at most 60 characters — their own text, not platform
 *   copy.
 */
@Serializable
public data class SetDirectionalFilterRequest(val destination: GeoPoint, val label: String? = null)

/**
 * `POST /v1/standby/directional` — 201.
 *
 * @property filterId The activation row.
 * @property expiresAt When the filter lapses.
 * @property usesRemaining Activations left today (Asia/Colombo).
 * @property maxDurationSec The configured ceiling, so the app can render the countdown scale.
 */
@Serializable
public data class DirectionalFilterCreated(
    val filterId: Ulid,
    val expiresAt: Timestamp,
    val usesRemaining: Int,
    val maxDurationSec: Int,
)

/**
 * `GET /v1/standby/directional` — 200 (DT-08). Drives the driver-app filter card.
 *
 * @property active Whether a filter is live.
 * @property destination Where it points; `null` when inactive.
 * @property label The driver's shorthand.
 * @property expiresAt When it lapses; `null` when inactive.
 * @property timeRemainingSec Seconds left.
 * @property usesRemaining Activations left today.
 */
@Serializable
public data class DirectionalFilterState(
    val active: Boolean,
    val destination: GeoPoint? = null,
    val label: String? = null,
    val expiresAt: Timestamp? = null,
    val timeRemainingSec: Int,
    val usesRemaining: Int,
)

/**
 * `DELETE /v1/standby/directional` — 200 (DT-03, US-6A.19).
 *
 * **Still consumes the daily use.** The activation row keeps its `usedDate` and is marked cleared
 * with reason `manual`; nothing is refunded.
 *
 * @property active Always `false`; the contract declares it `const`.
 * @property usesRemaining Activations left today — unchanged by the clear.
 */
@Serializable
public data class DirectionalFilterCleared(val active: Boolean, val usesRemaining: Int)

/**
 * Platform-wide Directional Travel configuration
 * (`dispatch.yaml#/components/schemas/DirectionalConfig`, DT-02/DT-03).
 *
 * Exactly one row exists (`ck_directional_config_singleton`, C004). The defaults below are the
 * D5' §12.1 values.
 *
 * @property thetaMaxDeg Angular tolerance between a ride's bearing and the driver's destination.
 * @property detourMaxM Pickup detour ceiling, metres.
 * @property progressMinM Minimum progress toward the destination for a ride to qualify.
 * @property maxUsesPerDay Activations a driver gets per Asia/Colombo day.
 * @property maxDurationSec Longest a single activation may last.
 * @property clearOnFirstTrip Whether the first matched trip clears the filter (DT-08).
 */
@Serializable
public data class DirectionalConfig(
    val thetaMaxDeg: Int,
    val detourMaxM: Int,
    val progressMinM: Int,
    val maxUsesPerDay: Int,
    val maxDurationSec: Int,
    val clearOnFirstTrip: Boolean,
)

// ---------------------------------------------------------------------------------------------
// Scheduled rides & Job Board
// ---------------------------------------------------------------------------------------------

/**
 * `POST /v1/rides/schedule` (AL-36, US-24.2).
 *
 * **The destination is required** — a missing one is `400 validation-failed`. Pickup defaults to
 * the passenger's current GPS and is editable, which is why its two fields are optional.
 *
 * @property pickupLat Defaults to the passenger's current GPS when omitted.
 * @property pickupLng Defaults to the passenger's current GPS when omitted.
 * @property destLat Destination latitude. Required.
 * @property destLng Destination longitude. Required.
 * @property pickupTime When the passenger wants to be collected.
 * @property vehicleType The type to dispatch.
 */
@Serializable
public data class ScheduleRideRequest(
    val pickupLat: Double? = null,
    val pickupLng: Double? = null,
    val destLat: Double,
    val destLng: Double,
    val pickupTime: Timestamp,
    val vehicleType: RideVehicleType,
)

/**
 * `POST /v1/rides/job-board/{rideId}/intent` — 200 (US-6A.5).
 *
 * **Not an acceptance.** One intent per driver per ride; a repeat is a no-op replay.
 *
 * @property intentId The recorded intent.
 * @property scheduledRideId The board row it was posted against.
 */
@Serializable
public data class JobBoardIntentResponse(val intentId: Ulid, val scheduledRideId: Ulid)

// ---------------------------------------------------------------------------------------------
// Driver Level
// ---------------------------------------------------------------------------------------------

/**
 * `GET /v1/drivers/{driverId}/level` — 200.
 *
 * Levels run 1–3 and everyone starts at 3. **Level 1 loses Job Board and scheduled-ride access**
 * (US-6A.8). The authoritative read on the dispatch hot path is the `Reputation.GetDriverLevel`
 * gRPC call; this is the driver-facing HTTP view.
 *
 * @property level 1–3.
 * @property ratingPoints Points accumulated toward the next level.
 * @property levelUpThreshold Points needed to move up.
 */
@Serializable
public data class DriverLevelResponse(val level: Int, val ratingPoints: Int? = null, val levelUpThreshold: Int? = null)

/**
 * `GET /v1/drivers/{driverId}/stats` — 200 (US-6A.14).
 *
 * The numbers the driver app shows behind the level badge.
 *
 * @property acceptanceRate 0…1.
 * @property noShows Driver-side no-show count.
 * @property points Current rating points.
 */
@Serializable
public data class DriverStatsResponse(val acceptanceRate: Double, val noShows: Int, val points: Int)

/**
 * `POST /v1/internal/drivers/{driverId}/no-show` (US-6A.7). Internal, mTLS only.
 *
 * Driver-side only; the passenger-side counter lives in `reputation.counters.no_shows`.
 *
 * @property rideId The ride the driver did not turn up for.
 */
@Serializable
public data class ReportDriverNoShowRequest(val rideId: Ulid? = null)

/**
 * `POST /v1/internal/drivers/{driverId}/no-show` — 200.
 *
 * @property driverId The penalised driver.
 * @property level Level after the decrement, 1–3.
 */
@Serializable
public data class DriverLevelAfterNoShow(val driverId: Ulid, val level: Int)

/**
 * Driver Level system configuration (`dispatch.yaml#/components/schemas/LevelConfig`, US-14.12).
 *
 * @property levelUpThreshold Points needed to move up a level.
 * @property noShowPenaltyPoints Points lost per driver no-show.
 * @property cancellationPenaltyPoints Points lost per driver cancellation.
 * @property jobBoardMinLevel Level 1 is excluded from the Job Board (US-6A.8).
 */
@Serializable
public data class LevelConfig(
    val levelUpThreshold: Int,
    val noShowPenaltyPoints: Int? = null,
    val cancellationPenaltyPoints: Int? = null,
    val jobBoardMinLevel: Int? = null,
)

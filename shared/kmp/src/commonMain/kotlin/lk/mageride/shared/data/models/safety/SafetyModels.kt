package lk.mageride.shared.data.models.safety

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType

// safety-svc — SOS, trip sharing, vehicle reports, driver blocking.
// Source: backend/contracts/safety.yaml (D3' "safety-svc — SOS, trip-share, report, block",
// ADD Appendix C).
//
// SOS (D-33). The alert goes out over the PRIMARY AND SECONDARY SMS GATEWAYS IN PARALLEL, not in
// sequence — the p99 budget is 5 seconds and a serial fallback cannot meet it. A user with no
// emergency contact on file gets 400 no-emergency-contact rather than a silent no-op.
//
// TRIP SHARING (D-34). A share token is scoped to one trip, lives until trip end + 1 hour, is
// rate-limited to 60 requests/minute and is revocable. The public view is LIVE ONLY — THERE IS NO
// REPLAY, so a leaked link stops being useful the moment the trip ends.
//
// REPORTS AND BLOCKS. Three confirmed vehicle reports auto-delist (US-12.6). Blocking a driver is
// passenger-scoped; a passenger cannot block themselves (ck_blocked_drivers_not_self, C005).

/**
 * Which side of a ride raised an SOS (`safety.sos_events.role` CHECK, C005).
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SosRole(public val wire: String) {
    @SerialName("passenger")
    PASSENGER("passenger"),

    @SerialName("driver")
    DRIVER("driver"),
}

/**
 * Which surface raised an SOS (`safety.sos_events.source` CHECK, C005).
 *
 * [WEB] is the public-bff equivalent raised from a share link; [APP] is the in-app button.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SosSource(public val wire: String) {
    @SerialName("app")
    APP("app"),

    @SerialName("web")
    WEB("web"),
}

/**
 * Where a vehicle report stands (`safety.vehicle_reports.status` CHECK, C005).
 *
 * **Three [CONFIRMED] reports auto-delist the vehicle** (US-12.6); confirmation is an admin
 * decision made in admin-bff.
 */
@Serializable
public enum class VehicleReportStatus {
    PENDING,
    CONFIRMED,
    DISMISSED,
}

/**
 * `POST /v1/sos`.
 *
 * Either party may raise it, with or without an active ride.
 *
 * @property rideId The ride in progress, when there is one.
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property role Who is raising it.
 */
@Serializable
public data class TriggerSosRequest(val rideId: Ulid? = null, val lat: Double, val lng: Double, val role: SosRole)

/**
 * `POST /v1/sos` — 200.
 *
 * @property sosId The recorded event.
 * @property dispatchedAt When the parallel SMS fan-out went out.
 */
@Serializable
public data class SosDispatched(val sosId: Ulid, val dispatchedAt: Timestamp? = null, val smsStatus: SosSmsStatus)

/**
 * What D-33's parallel gateways managed (`safety.yaml`, Δ C052).
 *
 * [FAILED] is not an error response: the alert **is** recorded and **is** on the admin live feed,
 * and reporting a dispatch that did not happen would tell somebody in trouble that help was on
 * the way. [NO_CONTACT] is AL-13 — nobody on file to send to.
 *
 * @property wire The value as it appears on the wire.
 */
@Serializable
public enum class SosSmsStatus(public val wire: String) {
    @SerialName("Dispatched")
    DISPATCHED("Dispatched"),

    @SerialName("Failed")
    FAILED("Failed"),

    @SerialName("NoContact")
    NO_CONTACT("NoContact"),
}

/**
 * One past SOS (`safety.yaml#/components/schemas/SosEvent`).
 *
 * Own history only; support and admin read the same events through the admin live feed, which is
 * separately RBAC-gated and audited.
 *
 * @property sosId The event.
 * @property rideId The ride it happened on, when there was one.
 * @property role Who raised it.
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property source App or web.
 * @property acknowledgedAt When an operator picked it up.
 * @property dispatchedAt When the alert went out.
 */
@Serializable
public data class SosEvent(
    val sosId: Ulid,
    val rideId: Ulid? = null,
    val role: SosRole,
    val lat: Double,
    val lng: Double,
    val source: SosSource,
    val acknowledgedAt: Timestamp? = null,
    val dispatchedAt: Timestamp,
) {
    /** Where the SOS was raised. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/**
 * `POST /v1/trip-share/{tripId}` — 201 (D-34).
 *
 * Re-issuing while a live token exists **replays it** rather than minting a second.
 *
 * @property token The share token. It is the credential — the public read takes no auth.
 * @property url The shareable link.
 * @property expiresAt Trip end plus one hour.
 */
@Serializable
public data class TripShareLink(val token: String, val url: String, val expiresAt: Timestamp)

/** The vehicle block of a [SharedTripView]. */
@Serializable
public data class SharedTripVehicle(val type: VehicleType? = null, val registrationNumber: String? = null)

/**
 * `GET /v1/trip-share/public/{token}` — 200 (`safety.yaml#/components/schemas/SharedTripView`).
 *
 * **No authentication — the token is the credential** (D-34). Live only: position and status as
 * of now, **never a replay of where the vehicle has been**.
 *
 * This is the `trip_view` scope. The richer package / proxy / pickup-confirm scopes are the
 * public-bff family at `/public/track/{token}` (AL-44), which the web surface owns.
 *
 * @property state The trip's state, as a display string the server chooses.
 * @property position Where the vehicle is now.
 * @property heading Course over ground, 0…359.
 * @property vehicle What to look for.
 * @property driverName Who is driving.
 * @property etaSeconds Seconds to arrival.
 * @property asOf When this snapshot was taken.
 * @property expiresAt When the token stops working.
 */
@Serializable
public data class SharedTripView(
    val state: String,
    val position: GeoPoint,
    val heading: Int? = null,
    val vehicle: SharedTripVehicle? = null,
    val driverName: String? = null,
    val etaSeconds: Int? = null,
    val asOf: Timestamp,
    val expiresAt: Timestamp? = null,
)

/**
 * `POST /v1/reports/vehicle` (US-12.5).
 *
 * @property vehicleId The reported vehicle.
 * @property reason Reporter-written, at most 2000 characters.
 * @property tripId The trip it happened on.
 */
@Serializable
public data class ReportVehicleRequest(val vehicleId: Ulid, val reason: String, val tripId: Ulid? = null)

/**
 * A filed vehicle report (`safety.yaml#/components/schemas/VehicleReport`).
 *
 * @property reportId The report.
 * @property vehicleId The reported vehicle.
 * @property reason What was reported.
 * @property tripId The trip it happened on.
 * @property status Where the admin decision stands.
 * @property createdAt When it was filed.
 */
@Serializable
public data class VehicleReport(
    val reportId: Ulid,
    val vehicleId: Ulid,
    val reason: String? = null,
    val tripId: Ulid? = null,
    val status: VehicleReportStatus,
    val createdAt: Timestamp,
)

/**
 * `POST /v1/drivers/{driverId}/block` (US-12.10).
 *
 * Passenger-scoped: dispatch will not offer this passenger's rides to this driver.
 *
 * @property reason Passenger-written, at most 500 characters.
 */
@Serializable
public data class BlockDriverRequest(val reason: String? = null)

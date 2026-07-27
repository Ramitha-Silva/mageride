package lk.mageride.shared.realtime

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.ServiceMode
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.VehicleType
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.PackageStatus
import lk.mageride.shared.data.models.ride.RideDriver

// The seven server → client payloads (`signalr-hub.md` §3).
//
// FIELD NAMES AND VALUE SETS MATCH THE REST CONTRACTS EXACTLY. `RideState`, `VehicleType`,
// `ServiceMode`, `LocationRequestState`, `PackageStatus` and `RideDriver` are C012's types,
// reused rather than re-declared: the hub carries the deltas and the REST surface carries the
// snapshots, and a client that had two spellings of a ride state would render one of them wrong.
// The hub protocol is JSON with camelCase properties, which is what [MageRideJson] already emits.

/**
 * One vehicle in a `VehiclePositions` batch.
 *
 * The event's argument is a **list** of these, batched per cell every 2–8 s — not one message per
 * fix. A per-fix fan-out would be 5 messages a second per vehicle multiplied by every subscriber
 * of its cell, which is the cost model ADD §7.4 exists to avoid.
 *
 * @property vehicleId The vehicle.
 * @property lat Degrees, −90…90.
 * @property lng Degrees, −180…180.
 * @property heading Course over ground, 0…359.
 * @property speed Metres per second.
 * @property type Canonical vehicle type, so the map can pick its marker.
 * @property mode Operating mode — A, B or C.
 */
@Serializable
public data class VehicleFrame(
    val vehicleId: Ulid,
    val lat: Double,
    val lng: Double,
    val heading: Int? = null,
    val speed: Double? = null,
    val type: VehicleType? = null,
    val mode: ServiceMode? = null,
) {
    /** The frame as a plain coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/** Why a vehicle left the public map (US-7.16/7.17, D-22). */
@Serializable
public enum class VehicleRemovalReason(public val wire: String) {
    /** No fresh sample inside the freshness window. */
    @SerialName("stale")
    STALE("stale"),

    /** The broker's last will fired, or the device disconnected cleanly (R-15, T-04). */
    @SerialName("offline")
    OFFLINE("offline"),

    /**
     * A Mode C vehicle went on active hire.
     *
     * It is removed from every public geocell group and appears only in `ride:{rideId}` from then
     * on (D-22) — an engaged vehicle is not "available", and showing it on the public map is how a
     * passenger ends up chasing a taxi that already has a fare.
     */
    @SerialName("engaged")
    ENGAGED("engaged"),
}

/**
 * A vehicle to drop from the map.
 *
 * @property vehicleId The vehicle.
 * @property reason Why — the three cases are distinguishable on purpose; only [
 *   VehicleRemovalReason.ENGAGED] means "still driving, just not yours to watch".
 */
@Serializable
public data class VehicleRemoved(val vehicleId: Ulid, val reason: VehicleRemovalReason)

/**
 * A ride-aggregate transition.
 *
 * `state` and `version` are the same values the REST responses carry, so a client can feed this
 * straight into `RideProjection.onServerState(…)` (C015) — ride-svc is the sole writer and the
 * socket is just a faster way to hear from it.
 *
 * @property rideId The ride.
 * @property state Where it is now — one of the 18 [RideState] values.
 * @property version The optimistic-concurrency counter to echo on the next mutation.
 * @property driver Present once a driver is attached.
 * @property etaSeconds Seconds to pickup, when the server has one.
 */
@Serializable
public data class RideStateChanged(
    val rideId: Ulid,
    val state: RideState,
    val version: RideVersion,
    val driver: RideDriver? = null,
    val etaSeconds: Int? = null,
)

/**
 * The assigned driver's live position, delivered to `ride:{rideId}` only (US-6A.12).
 *
 * @property rideId The ride this belongs to.
 * @property lat Degrees.
 * @property lng Degrees.
 * @property heading Course over ground, 0…359.
 */
@Serializable
public data class DriverPosition(val rideId: Ulid, val lat: Double, val lng: Double, val heading: Int? = null) {
    /** The driver's position as a plain coordinate. */
    public val point: GeoPoint get() = GeoPoint(lat = lat, lng = lng)
}

/**
 * The proxy round-trip resolving (P-02, P-13).
 *
 * Pushed on expiry and decline as well as confirmation, which is why there is no polling in this
 * flow at all; `GET /v1/location-requests/{requestId}` exists for reconnect and support only.
 *
 * @property requestId The location request.
 * @property state Confirmed, Declined or Expired.
 * @property geo Present only for [LocationRequestState.Confirmed] — declining stores no position.
 */
@Serializable
public data class LocationRequestResolved(
    val requestId: Ulid,
    val state: LocationRequestState,
    val geo: GeoPoint? = null,
)

/**
 * A Mode B share was revoked (D-22).
 *
 * Delivered to the affected passenger by a directed `RemoveFromGroupAsync` in under 200 ms — the
 * platform does not wait for their next cell crossing to stop showing a vehicle they no longer
 * have access to. The client drops the marker rather than letting it go stale.
 *
 * @property vehicleId The vehicle to stop rendering.
 */
@Serializable
public data class ShareRevoked(val vehicleId: Ulid)

/**
 * Package handoff progress (US-20.7).
 *
 * The event is named `PackageStatus` on the wire ([LiveHub.Event.PACKAGE_STATUS]); the type is
 * named for what it is so it does not collide with the [PackageStatus] enum it carries.
 *
 * @property rideId The delivery ride.
 * @property status How far it has got.
 */
@Serializable
public data class PackageStatusChanged(val rideId: Ulid, val status: PackageStatus)

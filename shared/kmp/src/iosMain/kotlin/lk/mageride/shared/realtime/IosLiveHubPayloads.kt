package lk.mageride.shared.realtime

import lk.mageride.shared.serialization.MageRideJson

// Decoding a `/hubs/live` payload from Swift (C094).
//
// WHY THESE EXIST, and it is not a convenience. `MageRideJson.decodeFromString<T>()` is `inline` +
// `reified`, so it is not exported to Objective-C at all — the same wall `Koin.get` and
// `Module.single` hit, and the reason `startIosGraph` exists. Swift can neither name the type
// parameter nor reach the function.
//
// The alternative would be `Codable` mirrors of the seven payloads in the app target, and that is
// exactly what `backend/contracts/realtime/signalr-hub.md` §3 forbids: *"a client can share one set
// of models between the socket and the API"*. C076 hit the same problem from the other side — the
// SignalR **Java** client binds with Gson, which spells an enum by its Kotlin `name()` rather than
// its `@SerialName`, so a Gson-bound `VehicleFrame` throws on the first three-wheeler in Colombo —
// and solved it the same way: bind the argument as raw text and decode it with the platform's own
// `Json`. These functions are that decode, on this platform.
//
// **Every one answers `null` rather than throwing.** A malformed or unrecognised payload must not
// take down the callback every other event also arrives on, and on this platform there is a second
// reason: an exception thrown out of a non-suspend, non-`@Throws` Kotlin function crosses as an
// uncaught Objective-C exception, which Swift **cannot catch** — the C091 finding. A decode that
// threw here would terminate the app on a contract change rather than drop one frame.
//
// `MageRideJson` has `ignoreUnknownKeys`, so a field added server-side is not malformed; what these
// actually guard is a change big enough to change a *shape*, which is a deploy problem rather than a
// reason to lose the map until the app is restarted.

/** `VehiclePositions` — a per-cell batch (US-7.3). */
public fun decodeVehicleFrames(json: String): List<VehicleFrame>? =
    runCatching { MageRideJson.decodeFromString<List<VehicleFrame>>(json) }.getOrNull()

/** `VehicleRemoved` — stale, offline, or gone on hire (US-7.16/7.17). */
public fun decodeVehicleRemoved(json: String): VehicleRemoved? =
    runCatching { MageRideJson.decodeFromString<VehicleRemoved>(json) }.getOrNull()

/** `ShareRevoked` — a Mode B entitlement withdrawn (D-22). */
public fun decodeShareRevoked(json: String): ShareRevoked? =
    runCatching { MageRideJson.decodeFromString<ShareRevoked>(json) }.getOrNull()

/** `RideStateChanged` — every ride-aggregate transition (ADD Appendix B.2). */
public fun decodeRideStateChanged(json: String): RideStateChanged? =
    runCatching { MageRideJson.decodeFromString<RideStateChanged>(json) }.getOrNull()

/** `DriverPosition` — the assigned driver's live position (US-6A.12). */
public fun decodeDriverPosition(json: String): DriverPosition? =
    runCatching { MageRideJson.decodeFromString<DriverPosition>(json) }.getOrNull()

/** `LocationRequestResolved` — the proxy round-trip resolving (P-02, P-13). */
public fun decodeLocationRequestResolved(json: String): LocationRequestResolved? =
    runCatching { MageRideJson.decodeFromString<LocationRequestResolved>(json) }.getOrNull()

/** `PackageStatus` — package handoff progress (US-20.7). */
public fun decodePackageStatusChanged(json: String): PackageStatusChanged? =
    runCatching { MageRideJson.decodeFromString<PackageStatusChanged>(json) }.getOrNull()

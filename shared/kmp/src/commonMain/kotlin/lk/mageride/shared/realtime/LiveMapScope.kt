package lk.mageride.shared.realtime

import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.domain.geo.H3Cell

/**
 * Which groups a client's map subscribes to — and, for the driver, why that set is empty.
 *
 * **AL-31 is a fence, and it is expressed as a type rather than as a rule in a screen.** The
 * driver home map renders the driver's OWN active vehicle and nothing else; other drivers' active
 * vehicles are never on it. [DriverHomeMap] therefore has no groups at all, so there is no
 * plausible edit that accidentally puts a driver into a `cell:` group — it would have to change
 * the type. The driver's own marker comes from the device's own GNSS, which the app already has
 * in hand for the position publisher; it does not need the fan-out plane to tell it where it is.
 *
 * A driver who is *on a ride* still joins `ride:{rideId}` — see [LiveHub.rideGroup]. That is a
 * different subscription with a different membership rule (the ride's own participants), and it
 * carries the passenger's view of the ride, not other vehicles.
 */
public sealed interface LiveMapScope {

    /** The SignalR groups this scope joins. */
    public val groups: Set<String>

    /**
     * The passenger live map: the R-06 cell set.
     *
     * @property cells Normally the 19 res-7 cells from
     *   [lk.mageride.shared.domain.geo.GeoCellSubscription].
     */
    public data class PassengerView(public val cells: Set<H3Cell>) : LiveMapScope {
        override val groups: Set<String> get() = cells.mapTo(LinkedHashSet(), LiveHub::cellGroup)
    }

    /** The driver home map: own vehicle only (AL-31, US-7.18). */
    public data object DriverHomeMap : LiveMapScope {
        override val groups: Set<String> get() = emptySet()
    }
}

/** One action in the post-reconnect recovery sequence. */
public sealed interface RecoveryStep {

    /** `JoinGeocells(cells)` — rejoin the map's groups. */
    public data class JoinGeocells(public val cells: Set<H3Cell>) : RecoveryStep

    /** `SubscribeRide(rideId)` — rejoin an in-flight ride. */
    public data class SubscribeRide(public val rideId: Ulid) : RecoveryStep

    /** `SubscribeLocRequest(requestId)` — rejoin a pending proxy round-trip (P-13). */
    public data class SubscribeLocationRequest(public val requestId: Ulid) : RecoveryStep

    /**
     * `GET /v1/nearby` — the snapshot that fills the gap the socket could not carry.
     *
     * Served from Redis GEO by `query-svc`. This is why the endpoint exists at all: the socket
     * carries deltas, the REST read carries state.
     */
    public data object ResyncNearbySnapshot : RecoveryStep
}

/**
 * The reconnection sequence of D6' §5.4 / `signalr-hub.md` §1.1, as an ordered plan.
 *
 * **The order is the contract, not a preference.** Groups are rejoined *before* the snapshot is
 * fetched: a client that snapshots first and joins afterwards loses every frame published between
 * the two calls, and those are exactly the frames that moved while it was disconnected. A client
 * that only reconnects without resyncing shows a stale map until the next per-cell batch, which
 * can be eight seconds — long enough for a passenger to conclude no vehicles are nearby.
 *
 * Reconnect backoff itself is [lk.mageride.shared.util.ReconnectBackoff]: jittered exponential,
 * 1–60 s, ±25 % (R-09) — the same curve the MQTT client uses, because a regional outage ends for
 * both planes at the same instant.
 */
public object LiveHubRecovery {

    /**
     * What to send once the connection is back.
     *
     * @param scope The map scope to restore. [LiveMapScope.DriverHomeMap] contributes no join
     *   step and no snapshot — there is nothing on the driver home map that the fan-out plane owns.
     * @param activeRides Rides the client was watching.
     * @param pendingLocationRequests Proxy location requests still awaiting an answer.
     */
    public fun plan(
        scope: LiveMapScope,
        activeRides: Set<Ulid> = emptySet(),
        pendingLocationRequests: Set<Ulid> = emptySet(),
    ): List<RecoveryStep> = buildList {
        if (scope is LiveMapScope.PassengerView && scope.cells.isNotEmpty()) {
            add(RecoveryStep.JoinGeocells(scope.cells))
        }
        activeRides.forEach { add(RecoveryStep.SubscribeRide(it)) }
        pendingLocationRequests.forEach { add(RecoveryStep.SubscribeLocationRequest(it)) }
        if (scope is LiveMapScope.PassengerView && scope.cells.isNotEmpty()) {
            add(RecoveryStep.ResyncNearbySnapshot)
        }
    }
}

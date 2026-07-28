package lk.mageride.shared.testing.scenario

import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.RideVersion
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.ride.RequestRideResponse
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideKind
import lk.mageride.shared.data.models.ride.RideRequest
import lk.mageride.shared.data.models.ride.RideStateSnapshot
import lk.mageride.shared.domain.ride.RideProjection
import lk.mageride.shared.domain.ride.RideSnapshot
import lk.mageride.shared.domain.ride.RideTransitions
import lk.mageride.shared.domain.ride.RideTrigger
import lk.mageride.shared.domain.ride.RideUpdate
import lk.mageride.shared.testing.fake.FakeApiBackend
import lk.mageride.shared.testing.fake.FakeReply
import lk.mageride.shared.testing.fixture.Fixtures

/**
 * One server-confirmed move in a ride's life.
 *
 * A step is what ride-svc **wrote**, not what a client decided: the client never advances a ride
 * (R-01), so a scenario is a list of snapshots the server would have published over SignalR, an
 * FCM push or a poll — in order, with the version each was written at. The ride's *starting* state
 * is not a step; it is what `POST /v1/rides/request` answered.
 *
 * @property state Where the ride is after this move.
 * @property version The version ride-svc wrote it at. Strictly increasing across a scenario, which
 *   is what makes the R-14 stale-frame rule assertable by replaying a step out of order.
 * @property trigger The ADD Appendix B.2 edge that got here. Every step in every scenario below is
 *   an edge the table draws exactly once, so [RideProjection] can name it back.
 * @property offerExpiresAt The 15-second deadline, on the one step that has one.
 */
public data class RideStep(
    val state: RideState,
    val version: RideVersion,
    val trigger: RideTrigger,
    val offerExpiresAt: Timestamp? = null,
) {
    /** This step as the cheap `GET /v1/rides/{rideId}/state` poll would report it. */
    public fun snapshot(): RideStateSnapshot =
        RideStateSnapshot(state = state, version = version, offerExpiresAt = offerExpiresAt)
}

/**
 * A whole ride, end to end: what was booked, every state the server went through, and the fake
 * responses that reproduce it.
 *
 * The four scenarios in this package are the four bookings the platform actually supports —
 * [ModeCRide] (a passenger books for themselves), [ProxyRide] (P-01, someone books for a rider who
 * may not have an account), [PackageDelivery] (P-07/P-08, two OTP handoffs and cash on delivery)
 * and, in a separate shape because it is not a ride at all, [ModeBSubscription].
 *
 * They exist so that "the app handles a ride" is one call rather than forty lines of DTO
 * construction in every test that needs a ride to exist:
 *
 * ```kotlin
 * val projection = ModeCRide.projection()
 * ModeCRide.drive(projection)
 * assertEquals(RideState.Paid, projection.state)
 * ```
 *
 * @property name What this journey is, for a failure message.
 * @property kind The booking kind. Fixed for the ride's life (ADD Appendix B.2 invariant 6).
 * @property request The booking body, exactly as the app would POST it.
 * @property booked The `202` that comes back — `Requested` at version 1, and the state the
 *   journey starts from.
 * @property steps Every server-confirmed move after the booking, in order. The last is terminal.
 */
public class RideScenario internal constructor(
    public val name: String,
    public val kind: RideKind,
    public val request: RideRequest,
    public val booked: RequestRideResponse,
    public val steps: List<RideStep>,
    private val detailFor: (RideState, RideVersion) -> RideDetail,
) {
    init {
        require(steps.isNotEmpty()) { "$name has no steps" }
        require(steps.last().state.isTerminal) { "$name does not end in a terminal state" }
    }

    /** Where the ride ends up. */
    public val terminalState: RideState get() = steps.last().state

    /** The full aggregate as it would read at [step]. */
    public fun detail(step: RideStep): RideDetail = detailFor(step.state, step.version)

    /** The full aggregate as it would read at the booking. */
    public fun initialDetail(): RideDetail = detailFor(booked.state, booked.version)

    /** The full aggregate as it would read at the end. */
    public fun finalDetail(): RideDetail = detail(steps.last())

    /**
     * A projection sitting at the booking response, on a clock the caller drives.
     *
     * @param clock What the offer TTL is measured against. Defaults to a stopped clock at
     *   [Fixtures.NOW], which is before every deadline in every scenario here.
     */
    public fun projection(clock: () -> Timestamp = { Fixtures.NOW }): RideProjection = RideProjection(
        initial = RideSnapshot(
            rideId = booked.rideId,
            kind = kind,
            state = booked.state,
            version = booked.version,
        ),
        clock = clock,
    )

    /** Feeds every step into [projection] the way the network would, and answers what each did. */
    public fun drive(projection: RideProjection): List<RideUpdate> =
        steps.map { projection.onServerState(it.state, it.version, it.offerExpiresAt) }

    /**
     * Programs [backend] so the typed clients reproduce this journey.
     *
     * `requestRide` answers [booked]; `getRideState` walks the journey one call at a time and then
     * holds at the terminal state; `getRide` answers the final aggregate. Anything else keeps the
     * fake's synthesised default, so a test only has to think about the calls it is about.
     */
    public fun install(backend: FakeApiBackend): FakeApiBackend {
        backend.returns("requestRide", booked)
        val poll = listOf(RideStateSnapshot(booked.state, booked.version)) + steps.map { it.snapshot() }
        backend.next("getRideState", *poll.map { FakeReply.value(it) }.toTypedArray())
        backend.always("getRideState", FakeReply.value(steps.last().snapshot()))
        backend.returns("getRide", finalDetail())
        return backend
    }

    /**
     * The edges this journey walks, as the transition table sees them.
     *
     * Exposed rather than only checked, so a test can assert *which* path was taken and not only
     * where it ended.
     */
    public fun edges(): List<Triple<RideState, RideTrigger, RideState>> {
        var from = booked.state
        return steps.map { step -> Triple(from, step.trigger, step.state).also { from = step.state } }
    }

    /** Whether ADD Appendix B.2 draws every edge in this journey. Asserted, never assumed. */
    public fun isWellFormed(): Boolean =
        edges().all { (from, trigger, to) -> RideTransitions.next(from, trigger) == to }

    override fun toString(): String = "$name (${booked.state.name} → ${steps.joinToString(" → ") { it.state.name }})"
}

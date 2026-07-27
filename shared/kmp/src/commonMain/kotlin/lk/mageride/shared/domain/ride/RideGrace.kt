package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.RideState
import lk.mageride.shared.data.models.Timestamp
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * The per-state offline grace windows (R-15/R-16, D5' §6.3).
 *
 * EMQX publishes the driver's Last Will (`veh/{vehicleId}/status = offline`) when their MQTT
 * connection drops; dispatch-svc releases any live offer and starts a grace timer whose length —
 * and whose outcome — depend on how far the ride had got. Sixty seconds is generous before pickup
 * and absurd mid-trip, so the window widens as the ride's cost of failure grows:
 *
 * | State | Grace | Beyond it |
 * |---|---|---|
 * | `Accepted` | 60 s | `CancelledByDriver` |
 * | `DriverArrived` | 120 s | `CancelledByDriver` |
 * | `InProgress` | 5 min | `Disputed` — a passenger is in the car, so this is a review, not a cancel |
 * | `PaymentPending` | 10 min | `Disputed` |
 *
 * **The server runs the timer; this is the client's read of it.** A driver app uses it to warn
 * ("you have 40 s of signal grace left"), and a passenger app to say how long the wait can still
 * be. Neither may move the ride — the transition arrives as a `RideStateChanged` like any other.
 */
public object RideGrace {

    private val WINDOWS: Map<RideState, Duration> = mapOf(
        RideState.Accepted to 60.seconds,
        RideState.DriverArrived to 120.seconds,
        RideState.InProgress to 5.minutes,
        RideState.PaymentPending to 10.minutes,
    )

    /** How long a driver may be offline in [state] before the ride is taken off them. */
    public fun windowFor(state: RideState): Duration? = WINDOWS[state]

    /**
     * Where the ride lands when the [state] window runs out.
     *
     * Always an edge [RideTransitions] already draws — this reads the table rather than repeating
     * it, so the two cannot drift.
     */
    public fun outcomeFor(state: RideState): RideState? =
        RideTransitions.next(state, RideTrigger.DRIVER_OFFLINE_GRACE_EXPIRED)

    /**
     * When the grace for [state] runs out, given when the driver dropped off.
     *
     * @param state Where the ride was when the LWT fired.
     * @param offlineSince The instant the broker reported the driver offline.
     * @return The deadline, or `null` in a state that has no grace window (nothing is at stake).
     */
    public fun deadline(state: RideState, offlineSince: Timestamp): Timestamp? =
        windowFor(state)?.let { offlineSince + it }

    /**
     * What is left of the grace, floored at zero.
     *
     * @return [Duration.ZERO] once the window has passed, and `null` in a state that has none.
     */
    public fun remaining(state: RideState, offlineSince: Timestamp, now: Timestamp): Duration? =
        deadline(state, offlineSince)?.let { deadline ->
            val left = deadline - now
            if (left.isNegative()) Duration.ZERO else left
        }
}

package lk.mageride.driver.jobs

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import lk.mageride.shared.domain.dispatch.JobBoard
import kotlin.time.Clock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

/**
 * One row of SCR-DA-018.
 *
 * @property ride The upcoming booking.
 * @property timeToPickup How long until the passenger expects to be collected, floored at zero.
 * @property cancelling A withdrawal is in flight for this row.
 */
@OptIn(ExperimentalTime::class)
internal data class ScheduledRideRow(
    val ride: ScheduledRide,
    val timeToPickup: Duration,
    val cancelling: Boolean = false,
) {

    /** The `dispatch.scheduled_rides` id. */
    val id: Ulid get() = ride.scheduledRideId

    /**
     * **US-6A.15.** Whether the 30-minute reminder is due, which is the wireframe's *"reminder
     * fired"* and its amber *"in 28 min"* pill.
     *
     * Derived from the pickup time against the same [JobBoard.GO_LIVE_LEAD] the board uses, rather
     * than from a flag: the reminder and the go-live are the same instant (D5' §3.7 dispatches at
     * T-30; §14.4 pushes `SCHEDULED_REMINDER` at 30 min for a driver), and nothing on the wire
     * records that a push was sent.
     */
    val reminderFired: Boolean get() = timeToPickup <= JobBoard.GO_LIVE_LEAD

    /** Whether this row is still a booking rather than a ride dispatch has already materialised. */
    val isScheduled: Boolean get() = ride.status == ScheduledRideStatus.SCHEDULED

    /** Minutes left, for the *"in 28 min"* pill. */
    val minutesToPickup: Int get() = timeToPickup.inWholeMinutes.toInt()
}

/**
 * SCR-DA-018's state.
 *
 * @property loading The read is in flight.
 * @property rows The driver's upcoming rides, soonest first.
 * @property error Resolved copy for the last failure.
 */
internal data class ScheduledRidesState(
    val loading: Boolean = true,
    val rows: List<ScheduledRideRow> = emptyList(),
    @param:StringRes val error: Int? = null,
) {

    /** Nothing upcoming — the list's own empty state. */
    val isEmpty: Boolean get() = !loading && rows.isEmpty()
}

/**
 * **SCR-DA-018 · scheduled rides** (US-6A.15).
 *
 * `GET /v1/rides/scheduled/{driverId}` — *"rides this driver has been assigned, ordered by pickup
 * time"*. The list is re-sorted here anyway, because the order is what the screen's whole meaning
 * rests on and a server that stopped sorting would be invisible.
 *
 * **A no-show on one of these costs a level** (US-6A.7, D5' §4.2). That is the reason the countdown
 * ticks rather than being read once: the row that says *"in 28 min"* is the one a driver has to act
 * on, and a screen left open on a stale minute count is how the level is lost.
 *
 * ### Cancellation
 *
 * The deliverable asks for it and `dispatch.yaml` has exactly one route for it —
 * `DELETE /v1/rides/schedule/{scheduledRideId}` — which dispatch-svc maps inside the **passenger**
 * role group. A driver's call is therefore a `403`, and once dispatch has materialised the ride the
 * same route answers `409` for anybody, because cancellation belongs to ride-svc's penalty matrix
 * from that point on. The button is wired, disabled once the row is `DISPATCHED`, and renders the
 * server's refusal as copy rather than pretending to have done something. See the C072 handoff.
 */
@OptIn(ExperimentalTime::class)
internal class ScheduledRidesViewModel(
    private val identity: DriverIdentity,
    private val jobs: JobsRepository,
    private val clock: () -> Timestamp = { Clock.System.now() },
) : ViewModel() {

    private val mutableState = MutableStateFlow(ScheduledRidesState())

    val state: StateFlow<ScheduledRidesState> = mutableState.asStateFlow()

    init {
        refresh()
        tick()
    }

    /** Re-reads the upcoming list. */
    fun refresh() {
        val driverId = identity.driverId ?: return
        mutableState.update { it.copy(loading = true, error = null) }

        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val rides = jobs.upcoming(driverId)
            mutableState.update { it.copy(loading = false, rows = rows(rides, clock())) }
        }
    }

    /**
     * Withdraws a booking that has not been dispatched yet.
     *
     * Refused for a `DISPATCHED` row before the call is made: from T-30 the ride exists and
     * `POST /v1/rides/{rideId}/cancel` owns the outcome, penalties and all.
     */
    fun cancel(row: ScheduledRideRow) {
        if (!row.isScheduled || row.cancelling) return
        mutableState.update { state -> state.copy(rows = state.rows.mark(row.id, cancelling = true)) }

        launchGuarded(
            onFailure = { mutableState.update { it.copy(rows = it.rows.mark(row.id, cancelling = false)) } },
        ) {
            jobs.cancelScheduled(row.id)
            mutableState.update { state -> state.copy(rows = state.rows.filterNot { it.id == row.id }) }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /** Advances every countdown once a second, and drops a pickup whose time has fully passed. */
    private fun tick() {
        viewModelScope.launch {
            while (isActive) {
                delay(TICK)
                val now = clock()
                mutableState.update { state ->
                    if (state.rows.isEmpty()) return@update state
                    state.copy(rows = state.rows.map { it.copy(timeToPickup = remaining(it.ride, now)) })
                }
            }
        }
    }

    private fun rows(rides: List<ScheduledRide>, now: Timestamp): List<ScheduledRideRow> = rides
        .filterNot { it.status == ScheduledRideStatus.CANCELLED }
        .sortedBy { it.pickupTime }
        .map { ScheduledRideRow(ride = it, timeToPickup = remaining(it, now)) }

    private fun remaining(ride: ScheduledRide, now: Timestamp): Duration {
        val left = ride.pickupTime - now
        return if (left.isNegative()) Duration.ZERO else left
    }

    private fun List<ScheduledRideRow>.mark(id: Ulid, cancelling: Boolean): List<ScheduledRideRow> =
        map { if (it.id == id) it.copy(cancelling = cancelling) else it }

    @Suppress("TooGenericExceptionCaught") // Every failure becomes copy; none reaches the driver raw.
    private fun launchGuarded(onFailure: () -> Unit = {}, block: suspend () -> Unit) {
        viewModelScope.launch {
            try {
                block()
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                onFailure()
                mutableState.update { it.copy(error = OnboardingErrors.messageFor(cause)) }
            }
        }
    }

    private companion object {

        /** The countdown is a minute figure, but the minute it turns over on has to be the right one. */
        val TICK: Duration = 1.seconds
    }
}

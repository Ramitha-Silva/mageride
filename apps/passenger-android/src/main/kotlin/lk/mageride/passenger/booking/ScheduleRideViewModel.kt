package lk.mageride.passenger.booking

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.ScheduleRideRequest
import kotlin.time.Clock
import kotlin.time.Duration.Companion.minutes

/**
 * SCR-PA-013's state.
 *
 * @property dropoff **Mandatory** (AL-36). `null` is what keeps Confirm disabled, and it is the
 *   only thing on this screen that can.
 * @property pickup Defaults to the passenger's current location and is editable — a ride booked
 *   tonight for tomorrow morning does not start where they are standing now.
 * @property pickupTime When it should happen, in wall-clock terms the passenger chose.
 * @property scheduled The created row, once Confirm has succeeded.
 */
internal data class ScheduleRideState(
    val pickup: Place? = null,
    val dropoff: Place? = null,
    val pickupTime: Timestamp? = null,
    val vehicleType: RideVehicleType = RideVehicleType.THREE_WHEELER,
    val saving: Boolean = false,
    val scheduled: String? = null,
    @param:StringRes val error: Int? = null,
) {

    /**
     * AL-36, and the Definition-of-Done line that says *"Confirm on Schedule Ride is disabled until
     * a destination is chosen"*.
     *
     * A time alone is not a booking, and the wireframe's own state line is explicit —
     * *"destination is mandatory before scheduling … Confirm disabled until a destination is set"*.
     * The time is separately required because the contract makes `pickupTime` non-null.
     */
    val canConfirm: Boolean get() = !saving && dropoff != null && pickupTime != null

    /** Whether the chosen instant is still in the future. Recomputed against a clock, not stored. */
    fun isFuture(now: Timestamp): Boolean = pickupTime?.let { it > now } == true
}

/**
 * SCR-PA-013 — a ride in the future (US-6A.4).
 *
 * **This is dispatch-svc, not ride-svc.** `POST /v1/rides/schedule` creates a
 * `dispatch.scheduled_rides` row that goes on the Job Board thirty minutes before the pickup
 * (US-6A.4/6A.5); at T-30 dispatch materialises it into a real ride through an internal command.
 * There is deliberately **no fare quote and no `fareEstimateToken` here** — `MaterialiseScheduledRideRequest`'s
 * own contract note says why: *"the price of a ride thirty minutes from now is not the price quoted
 * when it was booked"*, so fare-svc meters it at the time. A screen that showed a price would be
 * showing one nobody promised.
 *
 * **The reminders are the platform's, not this screen's.** US-10.9's 1 h and 15 min notifications
 * are scheduled server-side off the same row and arrive as pushes; the screen states that they are
 * set, which is what the wireframe's line does.
 */
internal class ScheduleRideViewModel(
    private val draft: BookingDraft,
    private val bookings: BookingRepository,
    private val now: () -> Timestamp = { Clock.System.now() },
) : ViewModel() {

    private val mutableState = MutableStateFlow(
        ScheduleRideState(
            pickup = draft.current.pickup,
            dropoff = draft.current.dropoff,
            vehicleType = draft.current.vehicleType ?: RideVehicleType.THREE_WHEELER,
        ),
    )

    val state: StateFlow<ScheduleRideState> = mutableState.asStateFlow()

    /** The destination picker's answer — the wireframe's *"Select destination…"* sheet. */
    fun setDestination(place: Place) {
        mutableState.update { it.copy(dropoff = place, error = null) }
        draft.update { it.copy(dropoff = place) }
    }

    /** The editable pickup row. */
    fun setPickup(place: Place) {
        mutableState.update { it.copy(pickup = place) }
        draft.update { it.copy(pickup = place) }
    }

    /**
     * The M3 date and time pickers' combined answer.
     *
     * **A past instant is refused here rather than only being greyed out** — the wireframe says
     * *"Past time disabled"*, and a picker that was opened at 08:29 and confirmed at 08:31 can
     * produce one that was future when it was chosen.
     */
    fun setPickupTime(at: Timestamp) {
        if (at <= now() + MINIMUM_LEAD) {
            mutableState.update { it.copy(error = lk.mageride.passenger.R.string.schedule_time_past) }
            return
        }
        mutableState.update { it.copy(pickupTime = at, error = null) }
    }

    fun setVehicleType(type: RideVehicleType) {
        mutableState.update { it.copy(vehicleType = type) }
        draft.update { it.copy(vehicleType = type) }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    /** *"Confirm schedule"* — `POST /v1/rides/schedule`. */
    @Suppress("TooGenericExceptionCaught", "ReturnCount") // Three preconditions, each its own guard.
    fun confirm() {
        val current = mutableState.value
        val dropoff = current.dropoff ?: return
        val at = current.pickupTime ?: return
        if (!current.canConfirm) return

        mutableState.update { it.copy(saving = true, error = null) }
        viewModelScope.launch {
            try {
                val row = bookings.scheduleRide(
                    ScheduleRideRequest(
                        // Nullable in the contract: a scheduled ride with no pickup is one that
                        // starts wherever the passenger is at the time, which is a legitimate
                        // booking and not a missing field.
                        pickupLat = current.pickup?.lat,
                        pickupLng = current.pickup?.lng,
                        destLat = dropoff.lat,
                        destLng = dropoff.lng,
                        pickupTime = at,
                        vehicleType = current.vehicleType,
                    ),
                )
                draft.clear()
                mutableState.update { it.copy(saving = false, scheduled = row.scheduledRideId) }
            } catch (cause: CancellationException) {
                throw cause
            } catch (cause: Throwable) {
                mutableState.update { it.copy(saving = false, error = BookingErrors.messageFor(cause)) }
            }
        }
    }

    /** The screen has navigated away from the created row. */
    fun onScheduleConsumed() {
        mutableState.update { it.copy(scheduled = null) }
    }

    private companion object {
        /**
         * How far ahead a scheduled ride has to be.
         *
         * The Job Board opens at T-30 (US-6A.4/6A.5), so anything closer than that would be posted
         * to a board it has already passed — a scheduled ride nobody can claim. Refusing it here is
         * a better answer than accepting a booking that quietly never dispatches.
         */
        val MINIMUM_LEAD = 30.minutes
    }
}

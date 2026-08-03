package lk.mageride.driver.history

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.driver.home.DriverIdentity
import lk.mageride.driver.onboarding.OnboardingErrors
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripPlane
import lk.mageride.shared.data.models.query.TripSummary

/**
 * One row of SCR-DA-030's list.
 *
 * @property summary What `GET /v1/trips/{driverId}` answered — route, fare and when.
 * @property distanceKm From the trip detail; `null` when the read has not landed or the geometry
 *   source does not carry one.
 * @property rating The stars **this driver** left on this trip, or `null` for one not yet rated.
 */
internal data class HistoryTrip(val summary: TripSummary, val distanceKm: Double? = null, val rating: Int? = null) {

    /** The trip id, which is a `rides.rides` id or a `trips.sessions` id depending on the plane. */
    val tripId: Ulid get() = summary.tripId

    /**
     * Whether the wireframe's *"Rate ★"* link is drawn.
     *
     * A **Mode C ride only**, and only once. A Mode A/B session is a vehicle's journey rather than
     * one person's trip — it has no single passenger to rate, and `DriverRatingInput` requires one
     * — so no link is drawn on it. That is not a gap; it is what a bus journey is.
     */
    val isRateable: Boolean get() = summary.plane == TripPlane.RIDE && rating == null
}

/**
 * The rate-passenger sheet's own state (AL-35: *"a modal bottom sheet, not an inline card"*).
 *
 * @property trip The row the sheet was opened from.
 * @property passengerId Who is being rated; `null` on a proxy booking for an unregistered rider
 *   (P-01), which is the one completed ride nobody can be rated for.
 * @property passengerName Their name, when the ride carried one.
 * @property stars 1–5. Five to start, which is the wireframe's five filled stars.
 * @property comment US-18.2's optional free text.
 * @property loading The `GET /v1/rides/{rideId}` that names the passenger is in flight.
 * @property submitting The rating POST is in flight.
 */
internal data class RatePassengerState(
    val trip: HistoryTrip,
    val passengerId: Ulid? = null,
    val passengerName: String? = null,
    val stars: Int = MAX_STARS,
    val comment: String = "",
    val loading: Boolean = true,
    val submitting: Boolean = false,
) {

    /** Whether **Submit rating** is live. */
    val canSubmit: Boolean get() = !loading && !submitting && passengerId != null && stars in MIN_STARS..MAX_STARS

    internal companion object {

        /** `trip-state.yaml#/components/schemas/RatingInput` — `stars` is `1..5`. */
        const val MIN_STARS: Int = 1
        const val MAX_STARS: Int = 5
    }
}

/**
 * SCR-DA-030's state.
 *
 * @property trips The driver's completed journeys, newest first.
 * @property rating The open rate-passenger sheet, or `null`.
 * @property loading The list read is in flight.
 * @property error Resolved copy for the last failure.
 */
internal data class RideHistoryState(
    val trips: List<HistoryTrip> = emptyList(),
    val rating: RatePassengerState? = null,
    val loading: Boolean = true,
    @param:StringRes val error: Int? = null,
) {

    /** Whether the driver has driven nothing at all yet. */
    val isEmpty: Boolean get() = !loading && trips.isEmpty()
}

/**
 * **SCR-DA-030 · ride history, trip details and the rate-passenger sheet** (US-8.8, US-18.2, AL-35).
 *
 * **The list is one read and the rows are one more each, in parallel.** `GET /v1/trips/{driverId}`
 * answers a page of summaries carrying route, fare and date; the wireframe's row also prints
 * *"8 km"* and *"rated ★5"*, and neither is on a summary — `TripSummary` has no distance and no
 * rating, by the contract. So the detail of every row on the page is fetched concurrently and folded
 * in as it lands. It is a fan-out and it is bounded: one page is `PageRequest.DEFAULT_LIMIT` = 20
 * cacheable GETs against a read model, issued once when the screen opens. The alternative —
 * fetching a detail only when a row is opened — would leave the *"Rate ★"* link drawn on a trip the
 * driver had already rated, and a second tap on it would write a second `trips.ratings` row.
 *
 * **A failed detail is not a failed screen.** Each is read best-effort: a row whose detail did not
 * come back keeps its summary and simply shows no distance. Losing the whole history because one
 * trip's polyline query timed out would be the wrong trade on a roadside connection.
 */
internal class RideHistoryViewModel(private val identity: DriverIdentity, private val history: RideHistoryRepository) :
    ViewModel() {

    private val mutableState = MutableStateFlow(RideHistoryState())

    val state: StateFlow<RideHistoryState> = mutableState.asStateFlow()

    init {
        refresh()
    }

    /** Re-reads the trip list, then every row's detail. */
    fun refresh() {
        val driverId = identity.driverId
        if (driverId == null) {
            mutableState.update { it.copy(loading = false) }
            return
        }

        mutableState.update { it.copy(loading = true, error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(loading = false) } }) {
            val summaries = history.trips(driverId)
            mutableState.update {
                it.copy(loading = false, trips = summaries.map { summary -> HistoryTrip(summary) })
            }
            enrich(driverId, summaries)
        }
    }

    /**
     * The wireframe's **Rate ★** on a completed trip — opens the modal sheet (AL-35).
     *
     * The passenger is named by a second read rather than carried on the row, because a trip
     * summary has no passenger on it at all: `GET /v1/trips/{userId}` is the same shape for both
     * parties to a ride and never says who the other one was.
     */
    fun openRating(tripId: Ulid) {
        val trip = mutableState.value.trips.firstOrNull { it.tripId == tripId } ?: return
        if (!trip.isRateable) return

        mutableState.update { it.copy(rating = RatePassengerState(trip = trip), error = null) }
        launchGuarded(onFailure = { mutableState.update { it.copy(rating = null) } }) {
            val ride = history.rideParties(tripId)
            mutableState.update { state ->
                state.copy(
                    rating = state.rating
                        ?.takeIf { it.trip.tripId == tripId }
                        ?.copy(loading = false, passengerId = ride.riderId, passengerName = ride.riderName),
                )
            }
        }
    }

    /** One of the five stars. */
    fun onStarsChange(stars: Int) {
        mutableState.update { state ->
            state.copy(rating = state.rating?.copy(stars = stars.coerceIn(STARS_RANGE)))
        }
    }

    /** The optional comment. */
    fun onCommentChange(raw: String) {
        mutableState.update { state -> state.copy(rating = state.rating?.copy(comment = raw)) }
    }

    /** Closes the sheet without rating. */
    fun dismissRating() {
        mutableState.update { it.copy(rating = null) }
    }

    /**
     * **Submit rating** — US-18.2.
     *
     * On success the row becomes *"rated ★N"* locally rather than through a re-read: the trip is
     * finished and nothing else about it can have changed, and re-reading a page of twenty trips to
     * learn one number the response already carried would be a round trip for nothing.
     */
    fun submitRating() {
        val sheet = mutableState.value.rating?.takeIf(RatePassengerState::canSubmit) ?: return
        val passengerId = sheet.passengerId ?: return

        mutableState.update { state -> state.copy(rating = state.rating?.copy(submitting = true), error = null) }
        launchGuarded(
            onFailure = { mutableState.update { it.copy(rating = it.rating?.copy(submitting = false)) } },
        ) {
            val recorded = history.ratePassenger(
                subjectId = sheet.trip.tripId,
                passengerId = passengerId,
                stars = sheet.stars,
                comment = sheet.comment,
            )
            mutableState.update { state ->
                state.copy(
                    rating = null,
                    trips = state.trips.map { trip ->
                        if (trip.tripId == sheet.trip.tripId) trip.copy(rating = recorded.stars) else trip
                    },
                )
            }
        }
    }

    /** Clears the last failure once its copy has been read. */
    fun dismissError() {
        mutableState.update { it.copy(error = null) }
    }

    /** Reads every listed trip's detail concurrently and folds the two extra facts in. */
    private suspend fun enrich(driverId: Ulid, summaries: List<TripSummary>) = coroutineScope {
        val details = summaries
            .map { summary -> async { summary.tripId to readDetail(driverId, summary.tripId) } }
            .awaitAll()
            .toMap()

        mutableState.update { state ->
            state.copy(
                trips = state.trips.map { trip ->
                    val detail = details[trip.tripId] ?: return@map trip
                    trip.copy(distanceKm = detail.first, rating = detail.second)
                },
            )
        }
    }

    /** One detail, best-effort: `(distanceKm, rating)`, or `null` when the read failed. */
    @Suppress("TooGenericExceptionCaught") // One dead detail must not take the list down with it.
    private suspend fun readDetail(driverId: Ulid, tripId: Ulid): Pair<Double?, Int?>? = try {
        history.detail(driverId, tripId).let { it.distanceKm to it.rating }
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }

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
        val STARS_RANGE = RatePassengerState.MIN_STARS..RatePassengerState.MAX_STARS
    }
}

package lk.mageride.passenger.history

import androidx.annotation.StringRes
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import lk.mageride.passenger.map.EncodedPolyline
import lk.mageride.passenger.ride.RideErrors
import lk.mageride.shared.data.models.GeoPoint
import lk.mageride.shared.data.models.query.GeometrySource
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.domain.auth.AuthSessionManager
import lk.mageride.shared.domain.auth.SessionState

/**
 * SCR-PA-023's state.
 *
 * @property route The Kalman-filtered track (E-04), decoded — the **same** one the fare was
 *   computed from, so the map and the receipt agree about the distance.
 * @property approximate Whether the polyline is the 1/min operational sample rather than full
 *   telemetry, in which case `distanceKm` is a lower bound and the screen should not present it as
 *   exact.
 */
internal data class TripDetailsState(
    val trip: TripDetail? = null,
    val route: List<GeoPoint> = emptyList(),
    val loading: Boolean = true,
    @param:StringRes val error: Int? = null,
) {
    /** `GeometrySource.OPERATIONAL` means the distance is a floor, not a measurement. */
    val approximate: Boolean get() = trip?.geometrySource == GeometrySource.OPERATIONAL
}

/**
 * SCR-PA-023 — one trip, its route and its receipt.
 *
 * **query-svc rather than ride-svc**, because this is the only read that carries the polyline and
 * the filtered distance — and because a Mode A bus journey has a trip detail and no ride at all.
 * `GET /v1/trips/{userId}/{tripId}` spans all three modes, which is what makes one details screen
 * possible.
 *
 * **The polyline is decoded, never re-measured.** `TripDetail.distanceKm` is the number the fare
 * was computed from; summing the decoded points here would produce a second figure that disagreed
 * with the receipt the first time a rounding rule changed server-side.
 */
internal class TripDetailsViewModel(
    private val tripId: String,
    private val history: HistoryRepository,
    private val sessions: AuthSessionManager,
) : ViewModel() {

    private val mutableState = MutableStateFlow(TripDetailsState())

    val state: StateFlow<TripDetailsState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch { load() }
    }

    fun clearError() {
        mutableState.update { it.copy(error = null) }
    }

    @Suppress("TooGenericExceptionCaught")
    private suspend fun load() {
        val userId = (sessions.state.value as? SessionState.SignedIn)?.userId
        if (userId == null) {
            mutableState.update { it.copy(loading = false) }
            return
        }

        try {
            val trip = history.trip(userId, tripId)
            mutableState.update {
                it.copy(trip = trip, route = EncodedPolyline.decode(trip.polyline), loading = false)
            }
        } catch (cause: CancellationException) {
            throw cause
        } catch (cause: Throwable) {
            mutableState.update { it.copy(loading = false, error = RideErrors.messageFor(cause)) }
        }
    }
}

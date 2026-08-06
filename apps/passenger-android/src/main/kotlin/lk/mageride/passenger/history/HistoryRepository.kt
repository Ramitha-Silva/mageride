package lk.mageride.passenger.history

import lk.mageride.shared.data.api.dispatch.DispatchApi
import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.models.Page
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.ride.RideHistoryRow

/**
 * Cluster 5's seam — what a passenger has already done, and the parcel that is still moving.
 *
 * **Two lists come from two services and that is not a mistake.** `GET /v1/rides/history` is
 * ride-svc's and carries the *ride*'s terminal state, its driver and — since AL-36/US-24.4 — the
 * post-trip Call block; `GET /v1/trips/{userId}` is query-svc's and spans **all three modes**,
 * because a Mode A bus journey is a trip a passenger took and is not a ride. SCR-PA-022's **Past**
 * tab is the ride history (it needs the state and the driver), and SCR-PA-023's detail is
 * query-svc's (it needs the polyline and the distance). Collapsing them would lose one or the other.
 */
internal interface HistoryRepository {

    /** `GET /v1/rides/history` — terminal rides, newest first, with the post-trip Call block. */
    suspend fun rides(page: PageRequest = PageRequest.FIRST): Page<RideHistoryRow>

    /** `GET /v1/trips/{userId}/{tripId}` — SCR-PA-023's polyline, distance and rating. */
    suspend fun trip(userId: Ulid, tripId: Ulid): TripDetail

    /**
     * `GET /v1/rides/{rideId}` — the ride behind a history card or a package deep link.
     *
     * This is what SCR-PA-022's **Call** needs: the list row carries `mobileMasked`, which
     * `PhoneMasked`'s own KDoc says must never be parsed back into a dialable number. The clear
     * number is `RideDetail.counterpartyPhone` (AL-48) and only this read has it. See the C081
     * handoff — the contract note claims the row exists so the Call action needs no second round
     * trip, and since AL-48 it does.
     */
    suspend fun ride(rideId: Ulid): RideDetail

    /** `GET /v1/rides/scheduled/{userId}` is the driver's; the passenger's upcoming list is here. */
    suspend fun scheduled(userId: Ulid): List<ScheduledRideRow>
}

/**
 * One row of SCR-PA-022's **Scheduled** tab.
 *
 * A local shape rather than `dispatch.ScheduledRide` because the passenger-facing read does not
 * exist — see [ApiHistoryRepository.scheduled] and the C081 handoff.
 */
internal data class ScheduledRideRow(
    val scheduledRideId: String,
    val pickupLabel: String?,
    val dropoffLabel: String?,
    val pickupTimeIso: String,
)

/** [HistoryRepository] over the generated clients. */
internal class ApiHistoryRepository(
    private val rides: RideApi,
    private val query: QueryApi,
    @Suppress("unused") private val dispatch: DispatchApi,
) : HistoryRepository {

    override suspend fun rides(page: PageRequest): Page<RideHistoryRow> = rides.listRideHistory(page)

    override suspend fun trip(userId: Ulid, tripId: Ulid): TripDetail = query.getTrip(userId, tripId)

    override suspend fun ride(rideId: Ulid): RideDetail = rides.getRide(rideId)

    /**
     * SCR-PA-022's **Scheduled** tab, and a gap.
     *
     * `dispatch.yaml` offers `GET /v1/rides/scheduled/{driverId}` — *"what this driver has already
     * claimed"* — and `DELETE /v1/rides/schedule/{scheduledRideId}` to withdraw one. **There is no
     * read that answers "what has this passenger scheduled".** A passenger can create a scheduled
     * ride (C079) and cancel one by id, and cannot list their own.
     *
     * Answering empty rather than inventing a call: the tab renders its empty state, which is
     * honest, and the moment the route exists this becomes one line. Recorded in the C081 handoff.
     */
    override suspend fun scheduled(userId: Ulid): List<ScheduledRideRow> = emptyList()
}

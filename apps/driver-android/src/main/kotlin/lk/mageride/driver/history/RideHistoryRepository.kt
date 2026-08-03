package lk.mageride.driver.history

import lk.mageride.shared.data.api.query.QueryApi
import lk.mageride.shared.data.api.ride.RideApi
import lk.mageride.shared.data.api.trip.TripStateApi
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.ride.RideDetail
import lk.mageride.shared.data.models.trip.DriverRatingInput
import lk.mageride.shared.data.models.trip.Rating

/**
 * SCR-DA-030's data — the driver's own trips, one trip's detail, and US-18.2's rating.
 *
 * ### The list is query-svc's, not ride-svc's
 *
 * `GET /v1/trips/{userId}` is the read model, it spans **both planes** — its SQL selects rides on
 * `accepted_driver_id = @UserId` and sessions on `driver_id = @UserId` — and it is implemented.
 * `GET /v1/rides/history` looks like the obvious alternative and is not: it is Mode C only, its row
 * carries a `driver` block that exists so a *passenger* can call the driver back (AL-36), and
 * ride-svc leaves it unmapped on purpose (its own CLAUDE.md files it under C048). A driver's
 * history is every journey they drove, which is what the first one answers.
 *
 * ### `rating` is *this caller's* rating, which is exactly the question the screen asks
 *
 * query-svc's trip-detail SQL joins `trips.ratings` on `subject_id = r.id AND rater_id = @UserId`.
 * So for a driver, [detail]'s `rating` means **"the stars I left on this trip"** — not the
 * passenger's rating of them — and a non-null one is what turns the wireframe's *"Rate ★"* link into
 * *"rated ★5"*. `TripSummary` carries no rating and no distance, which is why the screen reads a
 * detail per row; see [RideHistoryViewModel].
 *
 * ### The rating write has one door, and it is session-shaped
 *
 * **C074's headline spec gap.** `trips.ratings.subject_kind` is `CHECK (… IN ('session','ride'))`,
 * query-svc reads ride-subject ratings back, D5' §4.1 says ratings run *"passenger↔driver both
 * directions"* and D3' §Part 3 files US-18.2 against *"trip-state `/sessions/{id}/driver-rating`;
 * **ride**"* — but `ride.yaml` declares **no rating route at all**. The only operation on the
 * platform that writes a driver-to-passenger rating is trip-state-svc's, and its path takes a
 * `sessionId`. So [ratePassenger] sends the subject id it is given to the one route that exists:
 * correct for a Mode A/B session, and refused for a Mode C ride until ride-svc gains the route.
 * Wired rather than omitted, on the same reasoning C072 wired `cancelScheduled` — the refusal is
 * the honest answer to the deliverable, and a screen that silently dropped the tap would hide it.
 */
internal class RideHistoryRepository(
    private val query: QueryApi,
    private val ride: RideApi,
    private val tripState: TripStateApi,
) {

    /** `GET /v1/trips/{driverId}` — every trip this driver drove, newest first (US-8.7). */
    suspend fun trips(driverId: Ulid): List<TripSummary> = query.listTrips(driverId, PageRequest.FIRST).items

    /**
     * `GET /v1/trips/{driverId}/{tripId}` — distance, duration and *"have I rated this?"*.
     *
     * `distanceKm` is absent on `GeometrySource.AGGREGATE_1M` and the screen prints nothing rather
     * than a lower bound presented as a measurement — that source's own KDoc is explicit that the
     * two grains differ by an order of magnitude.
     */
    suspend fun detail(driverId: Ulid, tripId: Ulid): TripDetail = query.getTrip(driverId, tripId)

    /**
     * `GET /v1/rides/{rideId}` — who the driver is about to rate.
     *
     * Only a Mode C ride has one nameable passenger. `riderId` is **null on a proxy booking for an
     * unregistered rider** (P-01) — there is no `iam.users` row to hang a rating on — and
     * `DriverRatingInput.passengerId` is required, so the sheet says so rather than sending a
     * rating about nobody.
     */
    suspend fun rideParties(rideId: Ulid): RideDetail = ride.getRide(rideId)

    /**
     * `POST /v1/sessions/{subjectId}/driver-rating` — 1–5 stars and an optional comment (US-18.2).
     *
     * See the class KDoc: this is the platform's **only** driver-rates-passenger route, and its path
     * is session-scoped.
     */
    suspend fun ratePassenger(subjectId: Ulid, passengerId: Ulid, stars: Int, comment: String?): Rating =
        tripState.rateSessionPassenger(
            subjectId,
            DriverRatingInput(stars = stars, text = comment?.takeIf(String::isNotBlank), passengerId = passengerId),
        )
}

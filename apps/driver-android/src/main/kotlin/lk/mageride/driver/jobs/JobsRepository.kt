package lk.mageride.driver.jobs

import kotlinx.coroutines.CancellationException
import lk.mageride.shared.data.api.dispatch.DispatchApi
import lk.mageride.shared.data.models.PageRequest
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.domain.dispatch.DriverLevelRules
import lk.mageride.shared.domain.dispatch.DriverStanding

/**
 * dispatch-svc as SCR-DA-017, SCR-DA-018 and SCR-DA-019 use it.
 *
 * **The board, the upcoming list and the level are one service and one repository**, because the
 * L1 gate joins them: `GET /v1/rides/job-board` is only worth calling for a driver
 * [DriverLevelRules.hasJobBoardAccess] answers `true` for (US-6A.8), and that answer comes from
 * `GET /v1/drivers/{driverId}/level`. Splitting them would put the gate in a screen.
 *
 * **Nothing here decides a ride's fate.** The board is post-intent only — there is no accept route
 * on dispatch-svc and there is not meant to be (D5' §3.7): at T-30 min the ride is dispatched to
 * the closest intent-poster by Level as an ordinary offer, which SCR-DA-014 accepts through
 * ride-svc. R-01's fence, seen from the client side.
 */
internal class JobsRepository(private val dispatch: DispatchApi) {

    /**
     * The Driver Level and the US-6A.14 counters, as one standing.
     *
     * The two reads are independent and each is best-effort: a driver whose stats read fails still
     * needs the level, because the level is what opens or closes the Job Board. A level that could
     * not be read is `null` rather than [DriverLevelRules.STARTING_LEVEL] — guessing 3 would open
     * the board to a driver the server has demoted.
     */
    suspend fun standing(driverId: Ulid): JobStanding {
        val level = read { dispatch.getDriverLevel(driverId) }
        val stats = read { dispatch.getDriverStats(driverId) }

        return JobStanding(
            standing = level?.let(DriverStanding::of),
            levelUpThreshold = level?.levelUpThreshold,
            acceptanceRate = stats?.acceptanceRate,
            noShows = stats?.noShows,
            // `GET …/level` answers `ratingPoints`, `GET …/stats` answers `points`; they are the
            // same counter. The stats read wins when both answered, because it is the endpoint
            // D3' files US-6A.14 under.
            points = stats?.points ?: level?.ratingPoints,
        )
    }

    /**
     * `GET /v1/rides/job-board` — every future scheduled ride within the D-06 catchment.
     *
     * The radius is [lk.mageride.shared.domain.dispatch.JobBoard.CATCHMENT_METRES] and is sent
     * explicitly: the contract's own default is the same 30 km, but the app bar prints the number
     * and a screen that showed one radius while asking for another would be lying quietly.
     */
    suspend fun board(lat: Double, lng: Double, radiusMetres: Int): List<ScheduledRide> =
        dispatch.listJobBoard(lat = lat, lng = lng, radiusMetres = radiusMetres).items

    /**
     * `POST /v1/rides/job-board/{rideId}/intent` — register interest (US-6A.5).
     *
     * The path parameter is the **`dispatch.scheduled_rides` id**, which is what the board row
     * carries; the contract spells it `rideId` and the service says so at the endpoint.
     */
    suspend fun postIntent(scheduledRideId: Ulid) {
        dispatch.postJobBoardIntent(scheduledRideId)
    }

    /** `GET /v1/rides/scheduled/{driverId}` — SCR-DA-018's list, ordered by pickup time. */
    suspend fun upcoming(driverId: Ulid): List<ScheduledRide> =
        dispatch.listDriverScheduledRides(driverId, PageRequest.FIRST).items

    /**
     * `DELETE /v1/rides/schedule/{scheduledRideId}` — withdraw a booking before it is dispatched.
     *
     * **This route requires the `passenger` role** (`ScheduledRideEndpoints`, which splits the
     * group by role: the passenger books and cancels, the driver browses and posts intent), so a
     * driver's call is a `403`. It is wired anyway rather than left out, because that is the only
     * cancellation route `dispatch.yaml` has and the refusal is the honest answer to the deliverable
     * — see the C072 handoff, spec gap 2.
     */
    suspend fun cancelScheduled(scheduledRideId: Ulid) {
        dispatch.cancelScheduledRide(scheduledRideId)
    }

    /** Attempts [block], answering `null` for anything short of cancellation. */
    @Suppress("TooGenericExceptionCaught") // A dead stats read must not close the Job Board.
    private suspend fun <T> read(block: suspend () -> T): T? = try {
        block()
    } catch (cause: CancellationException) {
        throw cause
    } catch (_: Throwable) {
        null
    }
}

/**
 * Everything SCR-DA-019 prints, and the one fact SCR-DA-017 gates on.
 *
 * @property standing The level and the points banked toward the next one; `null` while reputation
 *   has not answered. **Never defaulted** — see [JobsRepository.standing].
 * @property levelUpThreshold The server's own `levelUpThreshold`, when it sent one. D5' §4.2's 500
 *   is a *fallback* (`DriverLevelRules.D5_DEFAULTS`), not a constant: an admin can retune it
 *   through `PUT /v1/admin/drivers/level-config` and a build that baked it in would disagree with
 *   dispatch the day one does.
 * @property acceptanceRate `0.0`–`1.0` (US-6A.14).
 * @property noShows Scheduled-ride no-shows, each of which cost a level (US-6A.7).
 * @property points Rating points banked, from whichever read answered.
 */
internal data class JobStanding(
    val standing: DriverStanding? = null,
    val levelUpThreshold: Int? = null,
    val acceptanceRate: Double? = null,
    val noShows: Int? = null,
    val points: Int? = null,
) {

    /** The level rules to evaluate against — the server's threshold when there is one (US-14.12). */
    val rules: DriverLevelRules
        get() = levelUpThreshold
            ?.let { DriverLevelRules(DriverLevelRules.D5_DEFAULTS.copy(levelUpThreshold = it)) }
            ?: DriverLevelRules()

    /**
     * **US-6A.8's gate.** Whether the Job Board is open to this driver.
     *
     * `null` while the level read is in flight, so a screen can hold the shimmer rather than
     * choosing between the gate state and the list on no information.
     */
    val hasJobBoardAccess: Boolean?
        get() = standing?.let(rules::hasJobBoardAccess)

    /** The standing with the counters the stats read supplies folded in, for the level screen. */
    val detailed: DriverStanding?
        get() = standing?.copy(points = points ?: standing.points)
}

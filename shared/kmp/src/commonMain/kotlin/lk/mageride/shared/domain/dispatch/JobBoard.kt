package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.Ulid
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes

// The Job Board (D5' §3.7, US-6A.5, D-06).
//
// A scheduled ride sits on the board until T-30 min, then goes live and is dispatched to the
// closest driver who posted intent, ranked by Level (ties → higher level first). Drivers POST
// INTENT; they do not accept. That distinction is the whole design: an acceptance would reserve a
// driver thirty minutes early, and a driver who is mid-ride at T-30 would have to be unwound.
//
// Candidate search is PostGIS `ST_DWithin(pickup, driver_home, 30 km)` on `dispatch.driver_presence`
// (D-06) — NOT an H3 ring, which at 30 km would be thousands of cells. The radius is here so a
// driver app can say "outside your area" rather than showing an empty board.

/** Why a driver may not post intent on a board row. */
public enum class JobBoardRejection {

    /** Level 1 has no Job Board access (US-6A.8). Not a ban — immediate Mode C still works. */
    LEVEL_TOO_LOW,

    /** The row has already gone live and been dispatched; it belongs to ride-svc now. */
    ALREADY_DISPATCHED,

    /** The passenger withdrew it. */
    CANCELLED,

    /**
     * Within the 30-minute go-live window.
     *
     * Intent posted now cannot influence a dispatch that is already choosing, so the board stops
     * taking it rather than accepting something that will not be read.
     */
    GO_LIVE_WINDOW_PASSED,

    /** This driver already posted intent. A repeat is a harmless replay, not a second bid. */
    ALREADY_POSTED,
}

/** Whether intent is worth posting. */
public sealed interface JobBoardVerdict {

    /** Post it. */
    public data object Allowed : JobBoardVerdict

    /**
     * Do not.
     *
     * @property reason What to tell the driver.
     */
    public data class Rejected(val reason: JobBoardRejection) : JobBoardVerdict

    /** Whether this verdict permits the call. */
    public val isAllowed: Boolean get() = this is Allowed
}

/**
 * The Job Board's client-side rules.
 *
 * Ranking is **not** here: which intent wins is decided by dispatch-svc at T-30 from distance and
 * level (D5' §3.7), against every driver's presence row, and a device knows neither the other
 * bidders nor their levels. What a client can answer is whether posting is worth the tap, which
 * is [canPostIntent].
 *
 * @param levels The level rules, for the L1 exclusion.
 */
public class JobBoard(private val levels: DriverLevelRules = DriverLevelRules()) {

    /**
     * Whether [driver] may post intent on [ride] right now.
     *
     * @param postedRideIds Board rows this driver has already bid on, so a duplicate tap is a
     *   [JobBoardRejection.ALREADY_POSTED] rather than a wasted call. The server treats a repeat
     *   as an idempotent replay either way (`POST /v1/rides/job-board/{rideId}/intent`).
     */
    public fun canPostIntent(
        driver: DriverStanding,
        ride: ScheduledRide,
        now: Timestamp,
        postedRideIds: Set<Ulid> = emptySet(),
    ): JobBoardVerdict = when {
        !levels.hasJobBoardAccess(driver) -> JobBoardVerdict.Rejected(JobBoardRejection.LEVEL_TOO_LOW)

        ride.status == ScheduledRideStatus.DISPATCHED ->
            JobBoardVerdict.Rejected(JobBoardRejection.ALREADY_DISPATCHED)

        ride.status == ScheduledRideStatus.CANCELLED -> JobBoardVerdict.Rejected(JobBoardRejection.CANCELLED)

        ride.scheduledRideId in postedRideIds -> JobBoardVerdict.Rejected(JobBoardRejection.ALREADY_POSTED)

        hasGoneLive(ride, now) -> JobBoardVerdict.Rejected(JobBoardRejection.GO_LIVE_WINDOW_PASSED)

        else -> JobBoardVerdict.Allowed
    }

    /** When [ride] leaves the board and dispatch starts offering it (D5' §3.7). */
    public fun goesLiveAt(ride: ScheduledRide): Timestamp = ride.pickupTime - GO_LIVE_LEAD

    /** How long a driver has left to post intent, floored at zero. */
    public fun timeToGoLive(ride: ScheduledRide, now: Timestamp): Duration {
        val left = goesLiveAt(ride) - now
        return if (left.isNegative()) Duration.ZERO else left
    }

    private fun hasGoneLive(ride: ScheduledRide, now: Timestamp): Boolean = now >= goesLiveAt(ride)

    public companion object {

        /** A scheduled ride goes live thirty minutes before pickup (D5' §3.7, US-6A.5). */
        public val GO_LIVE_LEAD: Duration = 30.minutes

        /**
         * The Job Board catchment, in metres (D-06).
         *
         * `ST_DWithin(pickup, driver_home, 30 km)` — anchored on the driver's **home**, not their
         * current position: the board is about where they will be in half an hour.
         */
        public const val CATCHMENT_METRES: Int = 30_000
    }
}

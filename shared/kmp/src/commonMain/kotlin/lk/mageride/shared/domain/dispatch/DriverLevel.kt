package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.dispatch.DriverLevelResponse
import lk.mageride.shared.data.models.dispatch.LevelConfig

// The Driver Level System (D5' §4, URD US-6A.6/6A.7/6A.8).
//
// Three levels, everyone starts at 3, and the level is both a dispatch scoring input (§3.3) and the
// Job Board ranking key (§3.7). reputation-svc owns the numbers; what is here is the arithmetic
// behind the badge, so a driver app can show "310 / 500 to level 3" and say what a rating was worth
// without a round trip per star.
//
// LEVEL 1 IS NOT A BAN (US-6A.8). An L1 driver still takes immediate Mode C rides; what they lose
// is the Job Board and scheduled rides. Anything in a driver app that reads "suspended" off level 1
// is wrong — `reputation.block_state` is what suspends, and it is a different field.

/**
 * A driver's standing in the level system.
 *
 * @property level 1–3. Everyone starts at [DriverLevelRules.STARTING_LEVEL].
 * @property points Rating points banked toward the next level. Reset by 500 on each level-up,
 *   never below zero.
 * @property passengerReports Reports since the last level-down. Three of them cost a level
 *   (D5' §4.2).
 * @property isDelisted Whether reputation-svc has time-boxed this driver out of dispatch. Set from
 *   the server; a level-down *causes* one but the client does not decide when it lifts.
 */
public data class DriverStanding(
    val level: Int = DriverLevelRules.STARTING_LEVEL,
    val points: Int = 0,
    val passengerReports: Int = 0,
    val isDelisted: Boolean = false,
) {
    init {
        require(level in DriverLevelRules.MIN_LEVEL..DriverLevelRules.MAX_LEVEL) {
            "driver level must be ${DriverLevelRules.MIN_LEVEL}..${DriverLevelRules.MAX_LEVEL}, was $level"
        }
    }

    public companion object {

        /** Projects `GET /v1/drivers/{driverId}/level`. */
        public fun of(response: DriverLevelResponse): DriverStanding =
            DriverStanding(level = response.level, points = response.ratingPoints ?: 0)
    }
}

/**
 * What a level change was caused by, so a driver app can explain it.
 *
 * Levels move for four reasons and only four (D5' §4.2). A screen that could only say "your level
 * changed" would leave a driver with no way to know whether to appeal.
 */
public enum class LevelChangeReason {

    /** Rating points crossed the threshold (US-6A.6). */
    POINTS_THRESHOLD,

    /** Three passenger reports (D5' §4.2). Comes with a time-boxed delisting. */
    PASSENGER_REPORTS,

    /** A no-show on an accepted scheduled ride (US-6A.7). */
    SCHEDULED_NO_SHOW,

    /** Nothing moved. */
    NONE,
}

/**
 * The outcome of applying an event to a standing.
 *
 * @property standing Where the driver is now.
 * @property reason Why it changed, or [LevelChangeReason.NONE].
 * @property levelDelta `+1`, `-1` or `0`.
 */
public data class LevelChange(val standing: DriverStanding, val reason: LevelChangeReason, val levelDelta: Int)

/**
 * D5' §4.2's arithmetic.
 *
 * **Only 4★ and 5★ earn points**, and each is worth its own star value — that is what makes "100
 * five-star ratings = 500 points = one level" (US-6A.6) come out right, and why 50×5★ + 65×4★ =
 * 510 also crosses. Three stars are worth nothing and one or two are ignored entirely; the
 * level system rewards good service rather than punishing bad, which is what the reports and
 * no-show counters are for.
 *
 * @param config Server-tuned thresholds (`PUT /v1/admin/drivers/level-config`, US-14.12). The
 *   fallback is [D5_DEFAULTS] — the spec's own numbers, used only until the server has answered.
 *   Do not read the defaults as constants: the whole point of `LevelConfig` is that an admin can
 *   move them, and a build that baked 500 in would disagree with dispatch the day one does.
 */
public class DriverLevelRules(private val config: LevelConfig = D5_DEFAULTS) {

    /** Points needed for a level-up, from config. */
    public val levelUpThreshold: Int get() = config.levelUpThreshold

    /** The lowest level the Job Board is open to (US-6A.8). */
    public val jobBoardMinLevel: Int get() = config.jobBoardMinLevel ?: (MIN_LEVEL + 1)

    /**
     * What a rating is worth (D5' §4.2).
     *
     * @param stars 1–5, as `trips.ratings.stars` stores it.
     */
    public fun pointsFor(stars: Int): Int {
        require(stars in MIN_STARS..MAX_STARS) { "stars must be $MIN_STARS..$MAX_STARS, was $stars" }
        return if (stars >= SCORING_STARS_FLOOR) stars else 0
    }

    /**
     * The standing after a passenger rates a completed ride.
     *
     * A crossing consumes exactly [levelUpThreshold] points and leaves the remainder banked, so a
     * driver who banks 510 keeps ten of them. Capped at [MAX_LEVEL]: the points are still spent,
     * which is what D5' §4.2's `points -= 500` says, and what stops a level-3 driver banking
     * thousands against a future level-down.
     */
    public fun afterRating(standing: DriverStanding, stars: Int): LevelChange {
        val banked = standing.points + pointsFor(stars)
        if (banked < levelUpThreshold) {
            return LevelChange(standing.copy(points = banked), LevelChangeReason.NONE, 0)
        }
        val raised = (standing.level + 1).coerceAtMost(MAX_LEVEL)
        return LevelChange(
            standing = standing.copy(level = raised, points = banked - levelUpThreshold),
            reason = LevelChangeReason.POINTS_THRESHOLD,
            levelDelta = raised - standing.level,
        )
    }

    /**
     * The standing after a passenger reports the driver.
     *
     * The third report costs a level **and** delists them temporarily (D5' §4.2). The counter
     * resets with the level-down; the delisting is time-boxed by reputation-svc and lifts on its
     * own or by admin appeal, which is why nothing here ever clears [DriverStanding.isDelisted].
     */
    public fun afterPassengerReport(standing: DriverStanding): LevelChange {
        val reports = standing.passengerReports + 1
        if (reports < REPORTS_PER_LEVEL_DOWN) {
            return LevelChange(standing.copy(passengerReports = reports), LevelChangeReason.NONE, 0)
        }
        val lowered = (standing.level - 1).coerceAtLeast(MIN_LEVEL)
        return LevelChange(
            standing = standing.copy(level = lowered, passengerReports = 0, isDelisted = true),
            reason = LevelChangeReason.PASSENGER_REPORTS,
            levelDelta = lowered - standing.level,
        )
    }

    /**
     * The standing after a no-show on an accepted **scheduled** ride (US-6A.7,
     * `dispatch.no_show_events`).
     *
     * One event, one level. Immediate Mode C no-shows are the D5' §7 matrix's business and cost
     * reputation, not a level.
     */
    public fun afterScheduledNoShow(standing: DriverStanding): LevelChange {
        val lowered = (standing.level - 1).coerceAtLeast(MIN_LEVEL)
        return LevelChange(
            standing = standing.copy(level = lowered),
            reason = if (lowered == standing.level) LevelChangeReason.NONE else LevelChangeReason.SCHEDULED_NO_SHOW,
            levelDelta = lowered - standing.level,
        )
    }

    /** Whether [standing] may see and bid on the Job Board (US-6A.8, D5' §3.7). */
    public fun hasJobBoardAccess(standing: DriverStanding): Boolean = standing.level >= jobBoardMinLevel

    /** Points still to bank before the next level. Zero at [MAX_LEVEL]. */
    public fun pointsToNextLevel(standing: DriverStanding): Int =
        if (standing.level >= MAX_LEVEL) 0 else (levelUpThreshold - standing.points).coerceAtLeast(0)

    public companion object {

        /** Levels run 1–3 (`dispatch.driver_levels`, D5' §4.2). */
        public const val MIN_LEVEL: Int = 1

        /** The ceiling. `min(level + 1, 3)` on every level-up. */
        public const val MAX_LEVEL: Int = 3

        /** Everyone starts here (`dispatch.driver_levels` default 3). */
        public const val STARTING_LEVEL: Int = 3

        /** Passenger reports that cost a level, with a time-boxed delisting (D5' §4.2). */
        public const val REPORTS_PER_LEVEL_DOWN: Int = 3

        /** Ratings at or above this earn their star value; below it they earn nothing. */
        public const val SCORING_STARS_FLOOR: Int = 4

        private const val MIN_STARS = 1
        private const val MAX_STARS = 5

        /**
         * D5' §4.2's own numbers, for a client that has not yet read
         * `PUT /v1/admin/drivers/level-config`.
         *
         * `noShowPenaltyPoints` and `cancellationPenaltyPoints` are deliberately `null`: D5' §4.2
         * describes both as **level** changes, not point deductions, and the contract's two
         * point-valued knobs have no rule behind them. See the C015 handoff.
         */
        public val D5_DEFAULTS: LevelConfig = LevelConfig(
            levelUpThreshold = 500,
            noShowPenaltyPoints = null,
            cancellationPenaltyPoints = null,
            jobBoardMinLevel = 2,
        )
    }
}

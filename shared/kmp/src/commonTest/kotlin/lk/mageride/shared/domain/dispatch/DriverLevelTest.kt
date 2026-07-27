package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.dispatch.LevelConfig
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The Driver Level System (D5' §4, US-6A.6/6A.7/6A.8).
 *
 * Everyone starts at 3, only 4★ and 5★ earn anything, and level 1 loses the Job Board without
 * losing the ability to work.
 */
class DriverLevelTest {

    private val rules = DriverLevelRules()

    @Test
    fun only_four_and_five_star_ratings_are_worth_anything() {
        // D5' §4.2: "counting only 4★ and 5★ (≤2★ ignored; 3★ counts 0)". The level system rewards
        // good service; bad service is what the reports and no-show counters are for.
        assertEquals(0, rules.pointsFor(1))
        assertEquals(0, rules.pointsFor(2))
        assertEquals(0, rules.pointsFor(3))
        assertEquals(4, rules.pointsFor(4))
        assertEquals(5, rules.pointsFor(5))
    }

    @Test
    fun a_star_count_outside_one_to_five_is_rejected() {
        assertFailsWith<IllegalArgumentException> { rules.pointsFor(0) }
        assertFailsWith<IllegalArgumentException> { rules.pointsFor(6) }
    }

    @Test
    fun a_hundred_five_star_ratings_is_exactly_one_level() {
        // The worked example from US-6A.6.
        var standing = DriverStanding(level = 2, points = 0)
        repeat(99) { standing = rules.afterRating(standing, 5).standing }

        assertEquals(2, standing.level)
        assertEquals(495, standing.points)

        val change = rules.afterRating(standing, 5)

        assertEquals(LevelChangeReason.POINTS_THRESHOLD, change.reason)
        assertEquals(1, change.levelDelta)
        assertEquals(3, change.standing.level)
        assertEquals(0, change.standing.points)
    }

    @Test
    fun four_star_ratings_reach_the_threshold_the_same_way_and_the_remainder_is_kept() {
        // D5' §4.2's second worked example is a batch — "50×5★ + 65×4★ = 250 + 260 = 510 ⇒ +1".
        // A live counter crosses the moment the sum does, so the same ratings arriving one at a
        // time level the driver up on the one that takes the total past 500 rather than at the end
        // of the batch. Same arithmetic, earlier moment, and the overflow is banked either way.
        var standing = DriverStanding(level = 1, points = 0)
        repeat(50) { standing = rules.afterRating(standing, 5).standing }

        assertEquals(250, standing.points)

        repeat(62) { standing = rules.afterRating(standing, 4).standing }

        assertEquals(498, standing.points)
        assertEquals(1, standing.level)

        val change = rules.afterRating(standing, 4)

        assertEquals(2, change.standing.level)
        assertEquals(2, change.standing.points, "502 − 500, banked toward the next level")
    }

    @Test
    fun a_level_three_driver_still_spends_the_points_on_a_crossing() {
        // `level = min(level + 1, 3)` and `points -= 500` are two separate statements in §4.2. The
        // spend is what stops a level-3 driver banking thousands against a future level-down.
        val standing = DriverStanding(level = 3, points = 499)

        val change = rules.afterRating(standing, 5)

        assertEquals(3, change.standing.level)
        assertEquals(4, change.standing.points)
        assertEquals(0, change.levelDelta)
    }

    @Test
    fun three_passenger_reports_cost_a_level_and_delist_the_driver() {
        var standing = DriverStanding(level = 3)

        standing = rules.afterPassengerReport(standing).standing
        standing = rules.afterPassengerReport(standing).standing

        assertEquals(3, standing.level)
        assertFalse(standing.isDelisted)

        val change = rules.afterPassengerReport(standing)

        assertEquals(LevelChangeReason.PASSENGER_REPORTS, change.reason)
        assertEquals(2, change.standing.level)
        assertTrue(change.standing.isDelisted, "the delisting is time-boxed, but it starts here")
        assertEquals(0, change.standing.passengerReports, "the counter resets with the level-down")
    }

    @Test
    fun a_scheduled_no_show_costs_one_level_and_never_goes_below_one() {
        val change = rules.afterScheduledNoShow(DriverStanding(level = 2))

        assertEquals(LevelChangeReason.SCHEDULED_NO_SHOW, change.reason)
        assertEquals(1, change.standing.level)

        // Level 1 is the floor. US-6A.8 is explicit that it is not a permanent ban, so there is
        // nothing below it to fall to.
        val atFloor = rules.afterScheduledNoShow(change.standing)

        assertEquals(1, atFloor.standing.level)
        assertEquals(LevelChangeReason.NONE, atFloor.reason)
    }

    @Test
    fun level_one_loses_the_job_board_and_nothing_else() {
        assertFalse(rules.hasJobBoardAccess(DriverStanding(level = 1)))
        assertTrue(rules.hasJobBoardAccess(DriverStanding(level = 2)))
        assertTrue(rules.hasJobBoardAccess(DriverStanding(level = 3)))
    }

    @Test
    fun the_threshold_comes_from_server_config_and_not_from_a_constant() {
        // US-14.12 lets an admin move it. A build that baked 500 in would disagree with dispatch
        // the day one did.
        val tuned = DriverLevelRules(LevelConfig(levelUpThreshold = 100, jobBoardMinLevel = 3))

        assertEquals(100, tuned.levelUpThreshold)
        assertEquals(1, tuned.afterRating(DriverStanding(level = 1, points = 96), 5).levelDelta)
        assertFalse(tuned.hasJobBoardAccess(DriverStanding(level = 2)), "the board floor moved too")
    }

    @Test
    fun the_badge_can_say_how_far_the_next_level_is() {
        assertEquals(500, rules.pointsToNextLevel(DriverStanding(level = 1, points = 0)))
        assertEquals(190, rules.pointsToNextLevel(DriverStanding(level = 2, points = 310)))
        assertEquals(0, rules.pointsToNextLevel(DriverStanding(level = 3, points = 10)))
    }

    @Test
    fun a_level_outside_one_to_three_cannot_be_constructed() {
        assertFailsWith<IllegalArgumentException> { DriverStanding(level = 0) }
        assertFailsWith<IllegalArgumentException> { DriverStanding(level = 4) }
    }
}

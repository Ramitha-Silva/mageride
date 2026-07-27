package lk.mageride.shared.domain.dispatch

import lk.mageride.shared.data.models.Place
import lk.mageride.shared.data.models.RideVehicleType
import lk.mageride.shared.data.models.Timestamp
import lk.mageride.shared.data.models.dispatch.ScheduledRide
import lk.mageride.shared.data.models.dispatch.ScheduledRideStatus
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.ExperimentalTime

private const val SCHEDULED_RIDE_ID = "01JSCHEDULEDRIDETESTTESTTE"

/**
 * The Job Board (D5' §3.7, US-6A.5, US-6A.8, D-06).
 *
 * Drivers post **intent**, not acceptance: an acceptance would reserve a driver thirty minutes
 * early, and a driver who turns out to be mid-ride at T-30 would have to be unwound. Which intent
 * wins is dispatch-svc's call at T-30 — a device knows neither the other bidders nor their levels
 * — so what is testable here is only whether posting is worth the tap.
 */
@OptIn(ExperimentalTime::class)
class JobBoardTest {

    private val board = JobBoard()

    private fun scheduledRide(
        pickupIn: Duration = 2.hours,
        status: ScheduledRideStatus = ScheduledRideStatus.SCHEDULED,
    ) = ScheduledRide(
        scheduledRideId = SCHEDULED_RIDE_ID,
        pickup = Place(lat = 6.9344, lng = 79.8428),
        dropoff = Place(lat = 7.0, lng = 79.95),
        vehicleType = RideVehicleType.SEDAN,
        pickupTime = OFFER_EPOCH + pickupIn,
        status = status,
    )

    private fun verdict(
        level: Int,
        ride: ScheduledRide = scheduledRide(),
        now: Timestamp = OFFER_EPOCH,
        posted: Set<String> = emptySet(),
    ) = board.canPostIntent(DriverStanding(level = level), ride, now, posted)

    @Test
    fun a_level_two_or_three_driver_may_bid_on_a_scheduled_ride() {
        assertEquals(JobBoardVerdict.Allowed, verdict(level = 3))
        assertEquals(JobBoardVerdict.Allowed, verdict(level = 2))
    }

    @Test
    fun level_one_loses_the_board_but_not_the_ability_to_work() {
        // US-6A.8. This is not a ban — the same driver still takes immediate Mode C offers, which
        // is why the reason is its own value rather than a generic refusal.
        assertEquals(JobBoardVerdict.Rejected(JobBoardRejection.LEVEL_TOO_LOW), verdict(level = 1))
    }

    @Test
    fun a_ride_that_has_gone_live_is_no_longer_taking_intent() {
        val ride = scheduledRide(pickupIn = 2.hours)

        assertEquals(JobBoardVerdict.Allowed, verdict(level = 3, ride = ride, now = OFFER_EPOCH))
        assertEquals(
            JobBoardVerdict.Rejected(JobBoardRejection.GO_LIVE_WINDOW_PASSED),
            verdict(level = 3, ride = ride, now = OFFER_EPOCH + 90.minutes),
        )
    }

    @Test
    fun the_board_row_goes_live_thirty_minutes_before_pickup() {
        val ride = scheduledRide(pickupIn = 2.hours)

        assertEquals(30.minutes, JobBoard.GO_LIVE_LEAD)
        assertEquals(ride.pickupTime - 30.minutes, board.goesLiveAt(ride))
        assertEquals(90.minutes, board.timeToGoLive(ride, OFFER_EPOCH))
        assertEquals(Duration.ZERO, board.timeToGoLive(ride, OFFER_EPOCH + 3.hours))
    }

    @Test
    fun a_dispatched_row_belongs_to_ride_svc_and_not_to_the_board() {
        assertEquals(
            JobBoardVerdict.Rejected(JobBoardRejection.ALREADY_DISPATCHED),
            verdict(level = 3, ride = scheduledRide(status = ScheduledRideStatus.DISPATCHED)),
        )
    }

    @Test
    fun a_withdrawn_row_takes_no_intent() {
        assertEquals(
            JobBoardVerdict.Rejected(JobBoardRejection.CANCELLED),
            verdict(level = 3, ride = scheduledRide(status = ScheduledRideStatus.CANCELLED)),
        )
    }

    @Test
    fun a_second_tap_is_a_replay_rather_than_a_second_bid() {
        assertEquals(
            JobBoardVerdict.Rejected(JobBoardRejection.ALREADY_POSTED),
            verdict(level = 3, posted = setOf(SCHEDULED_RIDE_ID)),
        )
    }

    @Test
    fun the_level_floor_follows_the_server_config() {
        val strict = JobBoard(DriverLevelRules(DriverLevelRules.D5_DEFAULTS.copy(jobBoardMinLevel = 3)))

        assertEquals(
            JobBoardVerdict.Rejected(JobBoardRejection.LEVEL_TOO_LOW),
            strict.canPostIntent(DriverStanding(level = 2), scheduledRide(), OFFER_EPOCH),
        )
        assertTrue(strict.canPostIntent(DriverStanding(level = 3), scheduledRide(), OFFER_EPOCH).isAllowed)
    }

    @Test
    fun the_catchment_is_the_d_06_thirty_kilometre_radius() {
        // `ST_DWithin(pickup, driver_home, 30 km)` on `dispatch.driver_presence` — anchored on the
        // driver's home, not their current position: the board is about where they will be in half
        // an hour. NOT an H3 ring, which at 30 km would be thousands of cells.
        assertEquals(30_000, JobBoard.CATCHMENT_METRES)
    }
}

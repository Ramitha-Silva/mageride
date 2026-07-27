package lk.mageride.shared.domain.ride

import lk.mageride.shared.data.models.GeoPointWithAccuracy
import lk.mageride.shared.data.models.ride.LocationRequestState
import lk.mageride.shared.data.models.ride.RidePaymentMethod
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime

private const val REQUEST_ID = "01JLOCREQTESTTESTTESTTESTT"

/**
 * Proxy booking — the booker/rider split and the "where are you?" round trip
 * (P-01..P-05, P-12, P-13, D5' §10).
 */
@OptIn(ExperimentalTime::class)
class ProxyBookingTest {

    private fun projection(
        state: LocationRequestState,
        geo: GeoPointWithAccuracy? = null,
        expiresIn: Duration = ProxyBooking.LOCATION_REQUEST_TTL,
    ) = LocationRequestProjection(
        requestId = REQUEST_ID,
        state = state,
        geo = geo,
        expiresAt = RIDE_EPOCH + expiresIn,
    )

    @Test
    fun a_declined_request_can_never_carry_coordinates() {
        // P-02: "Decline never leaks GPS." Not a coarse pin, not a stale one — nothing.
        val leakless = listOf(
            LocationRequestState.Declined,
            LocationRequestState.Expired,
            LocationRequestState.Pending,
        )

        for (state in leakless) {
            assertFailsWith<IllegalArgumentException>("$state must not carry a position") {
                projection(state, geo = GeoPointWithAccuracy(lat = 6.9271, lng = 79.8612, accuracy = 12.0))
            }
        }
    }

    @Test
    fun a_confirmed_request_carries_the_pickup_point_and_its_accuracy() {
        val geo = GeoPointWithAccuracy(lat = 6.9271, lng = 79.8612, accuracy = 12.0)

        val confirmed = projection(LocationRequestState.Confirmed, geo = geo)

        assertEquals(geo, confirmed.geo)
        // The accuracy is what lets the booker's map draw a circle rather than imply a perfect pin.
        assertEquals(12.0, confirmed.geo?.accuracy)
        assertTrue(confirmed.isResolved)
    }

    @Test
    fun an_unregistered_rider_is_still_a_live_request() {
        val request = projection(LocationRequestState.RiderNotRegistered)

        // AL-45: the SMS `pickup_confirm` web path, resolving through public-bff. The booker's
        // screen says "waiting", not "no such user".
        assertTrue(request.isUnregisteredRider)
        assertTrue(request.isResolved)
    }

    @Test
    fun the_countdown_is_five_minutes_and_stops_at_zero() {
        val request = projection(LocationRequestState.Pending)

        assertEquals(5.minutes, ProxyBooking.LOCATION_REQUEST_TTL)
        assertEquals(5.minutes, request.remaining(RIDE_EPOCH))
        assertEquals(30.seconds, request.remaining(RIDE_EPOCH + 4.minutes + 30.seconds))
        assertEquals(Duration.ZERO, request.remaining(RIDE_EPOCH + 10.minutes))

        assertFalse(request.hasLapsed(RIDE_EPOCH))
        assertTrue(request.hasLapsed(RIDE_EPOCH + 5.minutes))
    }

    @Test
    fun a_resolved_request_does_not_lapse_however_long_it_sits() {
        val confirmed = projection(
            LocationRequestState.Confirmed,
            geo = GeoPointWithAccuracy(lat = 6.9, lng = 79.8),
        )

        assertFalse(confirmed.hasLapsed(RIDE_EPOCH + 1.minutes * 60))
    }

    @Test
    fun cash_is_paid_by_the_rider_and_a_gateway_by_the_booker() {
        // P-04, US-8.21. The rider is told over FCM that they are expected to pay — a proxy booking
        // whose rider does not know that is how a driver ends up unpaid at a kerb.
        assertEquals(ProxyPayer.RIDER, ProxyBooking.payerFor(RidePaymentMethod.CASH))
        assertEquals(ProxyPayer.BOOKER, ProxyBooking.payerFor(RidePaymentMethod.LANKAQR))
        assertEquals(ProxyPayer.BOOKER, ProxyBooking.payerFor(RidePaymentMethod.ONEPAY))
    }

    @Test
    fun the_abuse_budget_runs_out_hourly_before_it_runs_out_daily() {
        var budget = LocationRequestBudget()

        assertTrue(budget.canRequest)
        assertEquals(ProxyBooking.REQUESTS_PER_HOUR, budget.hourlyRemaining)
        assertEquals(ProxyBooking.REQUESTS_PER_DAY, budget.dailyRemaining)

        repeat(ProxyBooking.REQUESTS_PER_HOUR) { budget = budget.spent() }

        assertFalse(budget.canRequest, "P-12: five an hour")
        assertEquals(0, budget.hourlyRemaining)
        assertEquals(ProxyBooking.REQUESTS_PER_DAY - ProxyBooking.REQUESTS_PER_HOUR, budget.dailyRemaining)
    }

    @Test
    fun the_daily_cap_holds_even_when_the_hour_has_rolled_over() {
        val budget = LocationRequestBudget(inLastHour = 0, today = ProxyBooking.REQUESTS_PER_DAY)

        assertFalse(budget.canRequest)
        assertEquals(0, budget.dailyRemaining)
    }
}

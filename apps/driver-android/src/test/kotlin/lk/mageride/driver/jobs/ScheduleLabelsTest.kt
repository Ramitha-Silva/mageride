package lk.mageride.driver.jobs

import lk.mageride.shared.data.models.Place
import lk.mageride.shared.testing.fixture.Fixtures
import java.util.Locale
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.time.Duration.Companion.hours
import kotlin.time.ExperimentalTime

/**
 * The Colombo clock, and the day boundary a handset in the wrong zone gets wrong.
 *
 * `Fixtures.MIDNIGHT_EDGE` is `2026-07-27T19:00:00Z`, which is **already the 28th in Colombo**
 * (UTC+05:30). Every one of these assertions would flip if the labels were read in UTC — a driver
 * would be shown a pickup card marked "tomorrow" for a ride they are due on in five hours.
 */
@OptIn(ExperimentalTime::class)
class ScheduleLabelsTest {

    private val english = Locale.ENGLISH

    @Test
    fun a_pickup_time_is_the_colombo_wall_clock() {
        // 04:15Z is 09:45 in Colombo — the instant `Fixtures.NOW` is named after.
        assertEquals("09:45", ScheduleLabels.time(Fixtures.NOW))
    }

    @Test
    fun today_and_tomorrow_are_colombo_days_not_a_count_of_hours() {
        val now = Fixtures.NOW

        assertEquals(DayLabel.Today, ScheduleLabels.day(now + 4.hours, now, english), "13:45 Colombo")
        assertEquals(DayLabel.Tomorrow, ScheduleLabels.day(now + 20.hours, now, english), "05:45 the next day")
    }

    @Test
    fun the_day_boundary_is_colombos_midnight_and_not_utcs() {
        // 19:00Z. In Colombo it is 00:30 on the 28th; in UTC it is still the 27th.
        val edge = Fixtures.MIDNIGHT_EDGE

        assertEquals(
            DayLabel.Today,
            ScheduleLabels.day(edge + 4.hours, edge, english),
            "04:30 on the 28th, seen from 00:30 on the 28th",
        )
    }

    @Test
    fun a_further_day_is_rendered_rather_than_named() {
        val label = ScheduleLabels.day(Fixtures.NOW + (24 * 3).hours, Fixtures.NOW, english)

        assertEquals(DayLabel.On("30 Jul"), label)
    }

    @Test
    fun a_ledger_date_is_always_rendered_and_is_still_a_colombo_one() {
        // Δ C073. SCR-DA-025's rows are all in the past, so "Today" on six of them would hide which
        // came first — and the day it prints is Colombo's: `MIDNIGHT_EDGE` is 19:00Z on the 27th,
        // which is already the 28th in Colombo.
        assertEquals("27 Jul", ScheduleLabels.date(Fixtures.NOW, english))
        assertEquals("28 Jul", ScheduleLabels.date(Fixtures.MIDNIGHT_EDGE, english))
    }

    @Test
    fun a_route_falls_back_to_a_dash_rather_than_to_coordinates() {
        // `POST /v1/rides/schedule` takes bare coordinates, so dispatch-svc has no address to echo
        // and every scheduled ride comes back with `address = null`. Decimal degrees on a card
        // would be worse than an honest blank. C072 handoff, spec gap 1.
        val nowhere = Place(lat = Fixtures.PICKUP.lat, lng = Fixtures.PICKUP.lng)

        assertEquals("— → —", ScheduleLabels.route(nowhere, nowhere))
        assertEquals("Colombo Fort → Bambalapitiya", ScheduleLabels.route(Fixtures.PICKUP, Fixtures.DROPOFF))
    }
}

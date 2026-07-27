package lk.mageride.shared.domain.fare

import kotlinx.datetime.LocalTime
import kotlinx.datetime.TimeZone
import lk.mageride.shared.data.models.RideVehicleType
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The peak and night windows (D5' §1.1, `fares.peak_windows`, §20 seed).
 *
 * Two properties matter and both are easy to get wrong: the windows are evaluated in
 * **Asia/Colombo** and not in UTC (D-38), and the night window **wraps midnight**.
 */
class SurchargeWindowsTest {

    private val windows = SurchargeWindows.D5_DEFAULTS

    @Test
    fun the_seeded_windows_are_the_ones_in_the_spec() {
        // §20: peak 07:00-09:00 and 17:00-19:00, night 22:00-05:00.
        val peak = windows.windows.filter { it.kind == SurchargeKind.PEAK }
        val night = windows.windows.filter { it.kind == SurchargeKind.NIGHT }

        assertEquals(
            setOf(LocalTime(7, 0) to LocalTime(9, 0), LocalTime(17, 0) to LocalTime(19, 0)),
            peak.mapTo(mutableSetOf()) { it.startLocal to it.endLocal },
        )
        assertEquals(1, night.size)
        assertEquals(LocalTime(22, 0) to LocalTime(5, 0), night.single().let { it.startLocal to it.endLocal })
    }

    @Test
    fun both_morning_and_evening_peak_windows_apply() {
        assertTrue(windows.isPeak(colombo(8)))
        assertTrue(windows.isPeak(colombo(18)))
        assertFalse(windows.isPeak(colombo(12)))
        assertFalse(windows.isPeak(colombo(20)))
    }

    @Test
    fun a_window_includes_its_start_and_excludes_its_end() {
        // Half-open. Neither spec pins the boundary; half-open is the only reading under which two
        // adjacent windows cannot both claim the same instant. See the C016 handoff.
        assertTrue(windows.isPeak(colombo(7, 0)), "07:00 is peak")
        assertTrue(windows.isPeak(colombo(8, 59)))
        assertFalse(windows.isPeak(colombo(9, 0)), "09:00 is not")
    }

    @Test
    fun the_night_window_wraps_midnight() {
        assertTrue(windows.isNight(colombo(22, 0)), "the window opens at 22:00")
        assertTrue(windows.isNight(colombo(23, 30)))
        assertTrue(windows.isNight(colombo(0, 30)), "past midnight is still the same window")
        assertTrue(windows.isNight(colombo(4, 59)))
        assertFalse(windows.isNight(colombo(5, 0)), "05:00 closes it")
        assertFalse(windows.isNight(colombo(21, 59)))
    }

    @Test
    fun the_windows_are_read_in_colombo_and_not_in_utc() {
        // 02:30Z is 08:00 in Colombo — inside the morning peak. Read as UTC it is the middle of the
        // night window instead, which is the exact failure D-38 exists to prevent: the same instant
        // would be surcharged 15% instead of 20%.
        val eightAmColombo = colombo(8)

        assertTrue(windows.isPeak(eightAmColombo))
        assertFalse(windows.isNight(eightAmColombo))

        assertFalse(windows.isPeak(eightAmColombo, TimeZone.UTC))
        assertTrue(windows.isNight(eightAmColombo, TimeZone.UTC))
    }

    @Test
    fun percentages_come_from_the_tariff_and_are_zero_outside_a_window() {
        // `fares.peak_windows.multiplier_pct` is deliberately not modelled: §1.1 reads the tariff's
        // own columns, and a second source would silently diverge from it. See the C016 handoff.
        val tariff = Tariff(
            vehicleType = RideVehicleType.SEDAN,
            firstKmMinor = 15_000,
            perKmMinor = 10_000,
            peakSurchargePct = 30,
            nightSurchargePct = 25,
        )

        assertEquals(SurchargePercentages(30, 0), windows.percentagesAt(tariff, colombo(8)))
        assertEquals(SurchargePercentages(0, 25), windows.percentagesAt(tariff, colombo(23)))
        assertEquals(SurchargePercentages(0, 0), windows.percentagesAt(tariff, colombo(12)))
    }

    @Test
    fun a_time_inside_two_windows_of_one_kind_is_that_kind_once() {
        val doubled = SurchargeWindows(
            listOf(
                SurchargeWindow(SurchargeKind.PEAK, LocalTime(7, 0), LocalTime(9, 0)),
                SurchargeWindow(SurchargeKind.PEAK, LocalTime(8, 0), LocalTime(10, 0)),
            ),
        )
        val tariff = TariffTable.D5_DEFAULTS.requireOf(RideVehicleType.SEDAN)

        assertEquals(20, doubled.percentagesAt(tariff, colombo(8, 30)).totalPct)
    }

    @Test
    fun a_window_reports_whether_it_wraps() {
        assertTrue(SurchargeWindow(SurchargeKind.NIGHT, LocalTime(22, 0), LocalTime(5, 0)).wrapsMidnight)
        assertFalse(SurchargeWindow(SurchargeKind.PEAK, LocalTime(7, 0), LocalTime(9, 0)).wrapsMidnight)
    }
}

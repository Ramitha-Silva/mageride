package lk.mageride.driver.sharing

import lk.mageride.driver.jobs.ScheduleLabels
import lk.mageride.shared.util.BusinessCalendar
import java.time.LocalDate
import java.time.ZoneOffset
import java.util.Locale
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * The two time-zone hops between a tapped date and a share grant's expiry.
 *
 * Both are easy to get wrong and neither fails loudly: a grant that lapses a day early simply stops
 * showing a passenger the vehicle, and nobody files a bug about a subscription that quietly ended.
 */
@OptIn(ExperimentalTime::class)
class ShareExpiryTest {

    /** What M3's `DatePickerState.selectedDateMillis` answers for 30 June 2026 — UTC midnight. */
    private val thirtiethOfJune = LocalDate.of(2026, 6, 30)
        .atStartOfDay(ZoneOffset.UTC)
        .toInstant()
        .toEpochMilli()

    @Test
    fun the_grant_lapses_at_the_end_of_the_chosen_colombo_day() {
        val expiry = ShareExpiry.endOfDay(thirtiethOfJune)

        // 30 June 23:59:59.999 in Colombo is 18:29:59.999Z — still the 30th there, and the whole of
        // the 30th has been served.
        assertEquals(Instant.parse("2026-06-30T18:29:59.999Z"), expiry)
        assertTrue(
            expiry > Instant.fromEpochMilliseconds(thirtiethOfJune),
            "sending the picker's own instant would revoke the passenger before the day began",
        )
    }

    @Test
    fun the_day_is_read_in_utc_because_that_is_what_the_picker_meant() {
        // Read in Colombo (+05:30) the picker's instant is 05:30 on the 30th, which is the right
        // date by luck; read in a zone west of UTC it would be the 29th. Anchoring on UTC is what
        // makes the conversion independent of the handset's own zone.
        val colomboDate = ShareExpiry.endOfDay(thirtiethOfJune)
            .toEpochMilliseconds()
            .let { java.time.Instant.ofEpochMilli(it) }
            .atZone(ScheduleLabels.ZONE)
            .toLocalDate()

        assertEquals(LocalDate.of(2026, 6, 30), colomboDate)
    }

    @Test
    fun the_zone_is_the_platforms_and_not_a_second_spelling_of_it() {
        // D-38: every clock in this app resolves Asia/Colombo from `:shared`'s BusinessCalendar,
        // so the expiry and the wallet ledger and the job board cannot disagree about a day.
        assertEquals(BusinessCalendar.ZONE.id, ScheduleLabels.ZONE.id)
    }

    @Test
    fun an_expiry_is_printed_with_its_year() {
        // `ScheduleLabels.date` drops the year because a ledger line is always in the past; a grant
        // can be set to lapse in a year's time and "30 Jun" would then be genuinely ambiguous.
        val printed = ShareExpiry.label(ShareExpiry.endOfDay(thirtiethOfJune), Locale.ENGLISH)

        assertEquals("30 Jun 2026", printed)
    }
}

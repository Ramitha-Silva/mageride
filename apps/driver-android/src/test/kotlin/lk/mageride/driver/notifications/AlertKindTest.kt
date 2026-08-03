package lk.mageride.driver.notifications

import lk.mageride.driver.ui.component.StatusTone
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.time.Duration.Companion.days
import kotlin.time.Duration.Companion.hours
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.ExperimentalTime
import kotlin.time.Instant

/**
 * SCR-DA-034's two pure decisions: which row a push type draws as, and how old it says it is.
 *
 * Both are here rather than in the screen because both are tables, and a table is the kind of thing
 * that is right when it is written and quietly wrong after the notification catalogue grows.
 */
class AlertKindTest {

    @Test
    fun the_push_types_the_wireframe_names_all_resolve() {
        // D2' §SCR-DA-034's own list: RIDE_OFFER, DIRECTIONAL_EXPIRING, LOW_BALANCE,
        // TOPUP_CONFIRMED, SHARE_REQUEST, package_*, SOS_*.
        assertEquals(AlertKind.RideOffer, AlertKind.of("ride_offer"))
        assertEquals(AlertKind.Directional, AlertKind.of("DIRECTIONAL_EXPIRING"))
        assertEquals(AlertKind.LowBalance, AlertKind.of("LOW_BALANCE"))
        assertEquals(AlertKind.MoneyIn, AlertKind.of("TOPUP_CONFIRMED"))
        assertEquals(AlertKind.MoneyIn, AlertKind.of("PAYMENT_CONFIRMED"))
        assertEquals(AlertKind.Share, AlertKind.of("SHARE_REQUEST"))
        assertEquals(AlertKind.Package, AlertKind.of("package_delivered"))
        assertEquals(AlertKind.Safety, AlertKind.of("SOS_TRIGGERED"))
        assertEquals(AlertKind.Safety, AlertKind.of("SOS_RESOLVED"))
    }

    @Test
    fun a_type_this_build_has_never_heard_of_still_draws_a_row() {
        // `data.kind` is notification-svc's catalogue name and the list "grows without a contract
        // change" — the same reason `NotificationPreferences` is a map. A driver being shown
        // nothing is worse than being shown a bell.
        assertEquals(AlertKind.Other, AlertKind.of("SOMETHING_ADDED_NEXT_QUARTER"))
        assertEquals(AlertKind.Other, AlertKind.of(""))
    }

    @Test
    fun money_out_and_money_in_do_not_wear_the_same_colour() {
        // The wireframe tints the low-balance row `#FBE2E2` and the top-up row `#E4F5EA`. They are
        // the two rows a driver scans a full inbox for, and telling them apart is the point.
        assertEquals(StatusTone.PENDING, AlertKind.LowBalance.tone)
        assertEquals(StatusTone.DONE, AlertKind.MoneyIn.tone)
    }

    @OptIn(ExperimentalTime::class)
    @Test
    fun the_relative_time_matches_what_the_wireframe_prints() {
        val now = Instant.parse("2026-07-27T04:15:00Z")

        assertEquals(null, AlertAge.of(now - 30.seconds, now).value, "'Just now' takes no number")
        assertEquals(2, AlertAge.of(now - 2.minutes, now).value, "the wireframe's '2 min ago'")
        assertEquals(1, AlertAge.of(now - 1.hours, now).value, "its '1 h ago'")
        assertEquals(null, AlertAge.of(now - 30.hours, now).value, "its 'Yesterday' takes none either")
        assertEquals(3, AlertAge.of(now - 3.days, now).value)
    }

    @OptIn(ExperimentalTime::class)
    @Test
    fun each_band_has_its_own_label() {
        val now = Instant.parse("2026-07-27T04:15:00Z")
        val labels = listOf(30.seconds, 2.minutes, 1.hours, 30.hours, 3.days)
            .map { AlertAge.of(now - it, now).label }

        assertEquals(labels.distinct(), labels, "two bands share a resource")
    }
}

package lk.mageride.shared.domain.subscription

import kotlinx.datetime.LocalDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionBilling
import lk.mageride.shared.data.models.subscription.SubscriptionCycle
import lk.mageride.shared.data.models.subscription.SubscriptionStatus
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Mode B billing cycles and per-subscriber fares (BR-23.8/23.9, AL-24, AL-49).
 */
class ModeBBillingTest {

    private fun subscription(
        billing: SubscriptionBilling,
        monthlyFareMinor: Long? = null,
        cycle: SubscriptionCycle = SubscriptionCycle.JOIN_ANNIVERSARY,
    ) = Subscription(
        subscriptionId = "01JSUB0000000000000000001",
        vehicleId = "01JVEH0000000000000000001",
        passengerId = "01JPAX0000000000000000001",
        billing = billing,
        monthlyFareMinor = monthlyFareMinor,
        currency = Currency.LKR,
        cycle = cycle,
        joinDay = 5,
        status = SubscriptionStatus.ACTIVE,
    )

    // ----------------------------------------------------------------------------------------
    // BR-23.9 — the cycle
    // ----------------------------------------------------------------------------------------

    @Test
    fun the_spec_worked_example_comes_out_exactly() {
        // "joined 5 Jun → next due 6 Jul", stated in D5' BR-23.9, ADD §9.1, D4' §18b,
        // server_db_schema §18b, URD US-23.8 and the functional walkthrough.
        assertEquals(
            LocalDate(2026, 7, 6),
            ModeBBilling.firstDueDate(SubscriptionCycle.JOIN_ANNIVERSARY, LocalDate(2026, 6, 5)),
        )
    }

    @Test
    fun a_first_of_month_cycle_falls_due_on_the_first_of_the_next_month() {
        assertEquals(
            LocalDate(2026, 7, 1),
            ModeBBilling.firstDueDate(SubscriptionCycle.MONTH_FIRST, LocalDate(2026, 6, 5)),
        )
        assertEquals(
            LocalDate(2027, 1, 1),
            ModeBBilling.firstDueDate(SubscriptionCycle.MONTH_FIRST, LocalDate(2026, 12, 31)),
        )
    }

    @Test
    fun subsequent_due_dates_roll_monthly_from_the_previous_one() {
        assertEquals(LocalDate(2026, 8, 6), ModeBBilling.rollForward(LocalDate(2026, 7, 6)))
        assertEquals(LocalDate(2026, 9, 6), ModeBBilling.rollForward(LocalDate(2026, 8, 6)))
        assertEquals(LocalDate(2027, 1, 1), ModeBBilling.rollForward(LocalDate(2026, 12, 1)))
    }

    @Test
    fun a_month_end_join_is_clamped_rather_than_overflowing() {
        // 30 January plus one month is 28 February, so the first due date is 1 March. Rolling a
        // 31st anchor lands on the 28th in February and returns to the 31st in March — because
        // each roll is computed from the previous due date the server persists, not from a clamped
        // intermediate.
        assertEquals(
            LocalDate(2026, 3, 1),
            ModeBBilling.firstDueDate(SubscriptionCycle.JOIN_ANNIVERSARY, LocalDate(2026, 1, 30)),
        )
        assertEquals(LocalDate(2026, 2, 28), ModeBBilling.rollForward(LocalDate(2026, 1, 31)))
        assertEquals(LocalDate(2028, 2, 29), ModeBBilling.rollForward(LocalDate(2028, 1, 31)), "a leap year")
    }

    @Test
    fun a_due_date_is_overdue_only_after_it_has_passed() {
        assertFalse(ModeBBilling.isOverdue(LocalDate(2026, 7, 6), today = LocalDate(2026, 7, 6)))
        assertTrue(ModeBBilling.isOverdue(LocalDate(2026, 7, 6), today = LocalDate(2026, 7, 7)))
    }

    // ----------------------------------------------------------------------------------------
    // BR-23.8 / item 16f — Free versus Paid, and the per-subscriber override
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_free_vehicle_charges_nothing() {
        // Office and staff transport: no fare, no payment UI at all.
        assertNull(ModeBBilling.fareFor(subscription(SubscriptionBilling.FREE)))
        assertNull(ModeBBilling.fareFor(subscription(SubscriptionBilling.FREE, monthlyFareMinor = 300_000)))
    }

    @Test
    fun a_paid_vehicle_charges_its_monthly_fare() {
        assertEquals(
            Money.ofMinor(300_000),
            ModeBBilling.fareFor(subscription(SubscriptionBilling.PAID, monthlyFareMinor = 300_000)),
        )
    }

    @Test
    fun a_per_subscriber_override_wins_including_a_waiver_of_zero() {
        // "Fleet owner may override the monthly fare per subscriber (subscribers may pay different
        // amounts)." An override of zero is an owner waiving one subscriber's fare, which is not
        // the same thing as no override.
        assertEquals(
            Money.ofMinor(250_000),
            ModeBBilling.effectiveFare(
                vehicleDefault = Money.ofMinor(300_000),
                subscriberOverride = Money.ofMinor(250_000),
            ),
        )
        assertEquals(
            Money.ZERO,
            ModeBBilling.effectiveFare(vehicleDefault = Money.ofMinor(300_000), subscriberOverride = Money.ZERO),
        )
        assertEquals(
            Money.ofMinor(300_000),
            ModeBBilling.effectiveFare(vehicleDefault = Money.ofMinor(300_000), subscriberOverride = null),
        )
    }

    // ----------------------------------------------------------------------------------------
    // AL-49 / BR-31.1 — money does not move before the payout profile is verified
    // ----------------------------------------------------------------------------------------

    @Test
    fun a_paid_vehicle_cannot_bill_without_a_verified_payout_profile() {
        // fleet-svc answers `409 payout-profile-not-verified`; the client checks first so the
        // owner is told why rather than shown a failure.
        assertFalse(ModeBBilling.canBill(SubscriptionBilling.PAID, payoutProfileVerified = false))
        assertTrue(ModeBBilling.canBill(SubscriptionBilling.PAID, payoutProfileVerified = true))
    }

    @Test
    fun a_free_vehicle_needs_no_payout_profile() {
        assertTrue(ModeBBilling.canBill(SubscriptionBilling.FREE, payoutProfileVerified = false))
    }
}

package lk.mageride.shared.domain.subscription

import lk.mageride.shared.data.models.BusinessDate
import lk.mageride.shared.data.models.Currency
import lk.mageride.shared.data.models.Money
import lk.mageride.shared.data.models.subscription.Subscription
import lk.mageride.shared.data.models.subscription.SubscriptionBilling
import lk.mageride.shared.data.models.subscription.SubscriptionCycle
import lk.mageride.shared.util.BusinessCalendar

// Mode B subscription billing (Epic 23, AL-23/24/25, D5' BR-23.8/23.9, `subscription.*`).
//
// MODE B IS NOT A FARE. There is no per-trip charge on a Mode B vehicle (D5' §1.1) — a subscriber
// pays a MONTHLY amount to the fleet owner, and the platform's own Mode B charge is a different
// money flow entirely (`billing.monthly_subscriptions`, ~Rs 300/vehicle, first month free, billed
// TO the owner). The two never net against each other (§18b).
//
// THE MONEY IS A PASS-THROUGH TO THE OWNER (AL-24). MageRide holds none of it and takes no
// commission, so `subscription.payments` must never post to `billing.journal_entries` — C005
// deliberately gave the table no column tempting anyone to. That is why this package is separate
// from `domain/wallet` and why nothing in it produces a `LedgerEntry`; `MoneyDomainHygieneTest`
// fails the build if that changes.
//
// "SERVICE PAYMENT" IS THE LABEL, `billing` IS THE FIELD (AL-51/BR-31.3). The rename is UI and
// documentation only: the values stay `Free | Paid` and the API is unchanged.

/**
 * When a subscriber's month rolls over (BR-23.9, `subscription.subscriptions.cycle`).
 */
public object ModeBBilling {

    /**
     * The first due date after a subscriber joins on [joinDate].
     *
     * - [SubscriptionCycle.MONTH_FIRST] — the 1st of the following month.
     * - [SubscriptionCycle.JOIN_ANNIVERSARY] — **the day after one calendar month**: joined 5 June
     *   → next due 6 July. The paid month covers the join day through the same day next month
     *   inclusive, so the next payment falls due the day after that.
     *
     * BR-23.9 writes the anniversary rule twice and the two spellings disagree by a day: the
     * shorthand `next_due = join_date + 1 month` gives 5 July, while the worked example in the
     * same sentence — and in ADD §9.1, D4' §18b, `server_db_schema.md` §18b, URD US-23.8 and the
     * functional walkthrough — says **6 July**. Five statements to one, so the example wins.
     * Recorded in the C016 handoff.
     *
     * Month-end is clamped by [BusinessCalendar.plusMonths]: joined 30 January → one month is 28
     * February (29 in a leap year) → due 1 March.
     */
    public fun firstDueDate(cycle: SubscriptionCycle, joinDate: BusinessDate): BusinessDate = when (cycle) {
        SubscriptionCycle.MONTH_FIRST -> BusinessCalendar.firstOfMonth(BusinessCalendar.plusMonths(joinDate))
        SubscriptionCycle.JOIN_ANNIVERSARY -> BusinessCalendar.plusDays(BusinessCalendar.plusMonths(joinDate))
    }

    /**
     * The due date after [current] — "subsequent due dates roll monthly" (BR-23.9).
     *
     * One rule for both cycles: the 1st rolls to the 1st, the 6th rolls to the 6th. Clamped at
     * month end, so a 31st anchor bills on the 28th in February and is back on the 31st in March —
     * because each roll is computed from the previous **due date**, which the server persists as
     * `next_due`, rather than from a clamped intermediate.
     */
    public fun rollForward(current: BusinessDate, months: Int = 1): BusinessDate =
        BusinessCalendar.plusMonths(current, months)

    /** Whether [nextDue] has passed as of [today] (Asia/Colombo, D-38). */
    public fun isOverdue(nextDue: BusinessDate, today: BusinessDate): Boolean = today > nextDue

    /**
     * What this subscriber owes each month, or `null` when they owe nothing.
     *
     * `null` for a **Free** vehicle — office and staff transport, which has no fare and no payment
     * UI at all (BR-23.8). A Free subscription carries no `monthly_fare_minor` by construction
     * (`ck_subscriptions_fare`, C005), so this is the DTO's own shape rather than an interpretation.
     */
    public fun fareFor(subscription: Subscription): Money? = subscription.monthlyFareMinor
        ?.takeIf { subscription.billing == SubscriptionBilling.PAID }
        ?.let { Money(amountMinor = it, currency = subscription.currency ?: Currency.LKR) }

    /**
     * The fare actually charged to one subscriber (BR-23.9, item 16f).
     *
     * "Fleet owner may override the monthly fare per subscriber (subscribers may pay different
     * amounts)." The override wins whenever there is one — including an override of **zero**,
     * which is a fleet owner waiving a particular subscriber's fare and is not the same thing as
     * no override at all.
     *
     * @param vehicleDefault The vehicle's default monthly fare, set at onboarding.
     * @param subscriberOverride This subscriber's fare, when the owner has set one.
     */
    public fun effectiveFare(vehicleDefault: Money?, subscriberOverride: Money?): Money? =
        subscriberOverride ?: vehicleDefault

    /**
     * Whether a **Paid** classification is allowed to bill at all (AL-49, BR-31.1).
     *
     * "A vehicle cannot be set Service payment = Paid, and Paid subscriptions cannot start
     * billing, while the org has no `verified` payout profile" — fleet-svc answers
     * `409 payout-profile-not-verified`. A Free vehicle collects nothing and needs no profile.
     *
     * @param billing The vehicle's classification.
     * @param payoutProfileVerified Whether the org has a verified payout profile.
     */
    public fun canBill(billing: SubscriptionBilling, payoutProfileVerified: Boolean): Boolean =
        billing == SubscriptionBilling.FREE || payoutProfileVerified
}

using MageRide.Subscriptions.Domain;

namespace MageRide.Subscriptions.Tests.Unit;

/// <summary>
/// BR-23.9 / US-23.8 — the billing cycle's due-date arithmetic, which is the one piece of Epic 23
/// that is pure and therefore testable without a database.
/// </summary>
public sealed class SubscriptionCycleTests
{
    /// <summary>
    /// The example printed in BR-23.9, D4' §18b and this component's definition of done, all three
    /// times with the same answer.
    /// </summary>
    [Fact]
    public void A_join_on_5_June_falls_due_on_6_July()
    {
        var joined = new DateOnly(2026, 6, 5);

        Assert.Equal(
            new DateOnly(2026, 7, 6),
            SubscriptionCycles.FirstDue(joined, SubscriptionCycles.JoinAnniversary));
    }

    /// <summary>
    /// The prose in BR-23.9 says "join_date + 1 month" and its own example says the day after. The
    /// example is what is implemented, and this is the reason: the fare paid on the 5th buys the
    /// month up to and including the 5th of the next, so a due date on the 5th would charge for a
    /// day already paid for.
    /// </summary>
    [Fact]
    public void The_due_date_is_the_day_after_the_month_that_was_paid_for_runs_out()
    {
        var joined = new DateOnly(2026, 6, 5);
        var due = SubscriptionCycles.FirstDue(joined, SubscriptionCycles.JoinAnniversary);

        Assert.Equal(joined.AddMonths(1).AddDays(1), due);
        Assert.Equal(joined.AddMonths(1), due.AddDays(-1));
    }

    [Fact]
    public void Subsequent_anniversaries_roll_a_month_at_a_time()
    {
        var due = SubscriptionCycles.FirstDue(new DateOnly(2026, 6, 5), SubscriptionCycles.JoinAnniversary);

        due = SubscriptionCycles.Advance(due, SubscriptionCycles.JoinAnniversary, joinDay: 5);
        Assert.Equal(new DateOnly(2026, 8, 6), due);

        due = SubscriptionCycles.Advance(due, SubscriptionCycles.JoinAnniversary, joinDay: 5);
        Assert.Equal(new DateOnly(2026, 9, 6), due);
    }

    /// <summary>
    /// A subscriber who joined on the 31st has no anniversary in February. The anniversary is
    /// re-derived from <c>join_day</c> every time rather than advanced from the previous due date,
    /// so February's clamp does not move them to the 28th for ever.
    /// </summary>
    [Fact]
    public void A_month_end_join_survives_February_and_returns_to_its_own_day()
    {
        var joined = new DateOnly(2027, 1, 31);

        var february = SubscriptionCycles.FirstDue(joined, SubscriptionCycles.JoinAnniversary);
        Assert.Equal(new DateOnly(2027, 3, 1), february);

        var march = SubscriptionCycles.Advance(february, SubscriptionCycles.JoinAnniversary, joinDay: 31);
        Assert.Equal(new DateOnly(2027, 4, 1), march);

        var april = SubscriptionCycles.Advance(march, SubscriptionCycles.JoinAnniversary, joinDay: 31);
        Assert.Equal(new DateOnly(2027, 5, 1), april);
    }

    [Fact]
    public void A_month_first_cycle_falls_due_on_the_first_whenever_the_subscriber_joined()
    {
        var due = SubscriptionCycles.FirstDue(new DateOnly(2026, 6, 5), SubscriptionCycles.MonthFirst);

        Assert.Equal(new DateOnly(2026, 7, 1), due);
        Assert.Equal(
            new DateOnly(2026, 8, 1),
            SubscriptionCycles.Advance(due, SubscriptionCycles.MonthFirst, joinDay: 5));
    }

    [Fact]
    public void A_month_first_cycle_rolls_across_a_year_boundary()
    {
        var december = SubscriptionCycles.FirstDue(new DateOnly(2026, 11, 20), SubscriptionCycles.MonthFirst);

        Assert.Equal(new DateOnly(2026, 12, 1), december);
        Assert.Equal(
            new DateOnly(2027, 1, 1),
            SubscriptionCycles.Advance(december, SubscriptionCycles.MonthFirst, joinDay: 20));
    }

    /// <summary>
    /// <c>subscription.payments.period_month</c> is the first of a month
    /// (<c>ck_subscription_payments_period_first_day</c>), whatever the due date's day is.
    /// </summary>
    [Fact]
    public void The_payment_period_is_always_the_first_of_the_due_dates_month()
    {
        Assert.Equal(new DateOnly(2026, 7, 1), SubscriptionCycles.PeriodOf(new DateOnly(2026, 7, 6)));
        Assert.Equal(new DateOnly(2026, 7, 1), SubscriptionCycles.PeriodOf(new DateOnly(2026, 7, 1)));
        Assert.Equal(new DateOnly(2027, 3, 1), SubscriptionCycles.PeriodOf(new DateOnly(2027, 3, 31)));
    }
}

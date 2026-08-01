using MageRide.Analytics.Configuration;
using MageRide.Analytics.Domain;
using MageRide.Shared.Errors;

namespace MageRide.Analytics.Tests.Unit;

/// <summary>
/// Period resolution and the previous-period arithmetic (AL-38, D-38).
/// </summary>
/// <remarks>
/// No container: this is calendar arithmetic, and it is the half of the component most likely to be
/// wrong in a way no integration test would notice — a previous period one day out still returns a
/// plausible percentage.
/// </remarks>
public sealed class StatsPeriodTests
{
    private static readonly AnalyticsOptions Defaults = new();

    /// <summary>15 July 2026 is a Wednesday, which is what makes the week cases interesting.</summary>
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public void Today_is_one_day_and_compares_against_yesterday()
    {
        var period = StatsPeriod.Resolve("today", null, null, Today, Defaults);

        Assert.Equal(StatsPeriods.Today, period.Period);
        Assert.Equal(new StatsRange(Today, Today), period.Range);
        Assert.Equal(1, period.Range.Days);
        Assert.Equal(new StatsRange(new DateOnly(2026, 7, 14), new DateOnly(2026, 7, 14)), period.PreviousRange);
    }

    /// <summary>The contract's default when <c>?period=</c> is absent is <c>today</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_period_is_today(string? period)
    {
        Assert.Equal(StatsPeriods.Today, StatsPeriod.Resolve(period, null, null, Today, Defaults).Period);
    }

    [Fact]
    public void A_period_is_matched_case_insensitively()
    {
        Assert.Equal(StatsPeriods.Month, StatsPeriod.Resolve("MONTH", null, null, Today, Defaults).Period);
    }

    /// <summary>
    /// "This week" is week-to-date from the configured start day, not a rolling seven days — that is
    /// what SCR-AP-002's label says. Wednesday the 15th ⇒ Monday the 13th to Wednesday the 15th.
    /// </summary>
    [Fact]
    public void Week_runs_from_the_configured_week_start_to_today()
    {
        var period = StatsPeriod.Resolve("week", null, null, Today, Defaults);

        Assert.Equal(new StatsRange(new DateOnly(2026, 7, 13), Today), period.Range);
        Assert.Equal(3, period.Range.Days);

        // Three days long, so the comparison window is the three days before it — Friday to Sunday.
        Assert.Equal(new StatsRange(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 12)), period.PreviousRange);
    }

    /// <summary>
    /// The week start is a setting because no spec names one (raised in the C061 handoff). With
    /// Sunday, the same Wednesday is a four-day week-to-date.
    /// </summary>
    [Fact]
    public void Week_honours_a_different_week_start()
    {
        var options = new AnalyticsOptions { WeekStartsOn = DayOfWeek.Sunday };

        var period = StatsPeriod.Resolve("week", null, null, Today, options);

        Assert.Equal(new StatsRange(new DateOnly(2026, 7, 12), Today), period.Range);
        Assert.Equal(4, period.Range.Days);
    }

    /// <summary>On the week's first day, "this week" is one day long.</summary>
    [Fact]
    public void Week_on_its_own_first_day_is_a_single_day()
    {
        var monday = new DateOnly(2026, 7, 13);

        Assert.Equal(new StatsRange(monday, monday), StatsPeriod.Resolve("week", null, null, monday, Defaults).Range);
    }

    /// <summary>"This month" is month-to-date: on the 15th it is fifteen days, not thirty-one.</summary>
    [Fact]
    public void Month_runs_from_the_first_to_today()
    {
        var period = StatsPeriod.Resolve("month", null, null, Today, Defaults);

        Assert.Equal(new StatsRange(new DateOnly(2026, 7, 1), Today), period.Range);
        Assert.Equal(15, period.Range.Days);

        // Fifteen days long, so the window is the fifteen days before the 1st — which reaches back
        // into June. The previous period is "the same length immediately before", not "last month".
        Assert.Equal(new StatsRange(new DateOnly(2026, 6, 16), new DateOnly(2026, 6, 30)), period.PreviousRange);
    }

    /// <summary>
    /// A month-to-date range early in March compares against the end of February — the shorter month
    /// is not a special case because the rule is arithmetic on day numbers.
    /// </summary>
    [Fact]
    public void Month_to_date_crosses_a_short_month_by_subtraction()
    {
        var period = StatsPeriod.Resolve("month", null, null, new DateOnly(2026, 3, 5), Defaults);

        Assert.Equal(new StatsRange(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 5)), period.Range);
        Assert.Equal(new StatsRange(new DateOnly(2026, 2, 24), new DateOnly(2026, 2, 28)), period.PreviousRange);
    }

    /// <summary>
    /// The definition-of-done case: a custom range that spans a month boundary computes the correct
    /// previous period.
    /// </summary>
    /// <remarks>
    /// 15 July to 14 August is 31 days. The window immediately before it is 14 June to 14 July —
    /// also 31 days, ending the day before this one starts, and itself spanning a month boundary. A
    /// "same dates last month" rule would have produced 15 June to 14 July (30 days) and quietly
    /// compared 31 days of trips against 30.
    /// </remarks>
    [Fact]
    public void A_custom_range_spanning_a_month_boundary_gets_an_equally_long_previous_period()
    {
        var period = StatsPeriod.Resolve(
            "custom", new DateOnly(2026, 7, 15), new DateOnly(2026, 8, 14), Today, Defaults);

        Assert.Equal(31, period.Range.Days);
        Assert.Equal(new StatsRange(new DateOnly(2026, 6, 14), new DateOnly(2026, 7, 14)), period.PreviousRange);
        Assert.Equal(period.Range.Days, period.PreviousRange.Days);
        Assert.Equal(period.Range.From.AddDays(-1), period.PreviousRange.To);
    }

    /// <summary>Across a year boundary, and across a leap day, the same subtraction holds.</summary>
    [Theory]
    [InlineData("2026-12-20", "2027-01-10", "2026-11-28", "2026-12-19")]
    [InlineData("2028-02-20", "2028-03-05", "2028-02-05", "2028-02-19")]
    public void The_previous_period_is_always_the_same_length_immediately_before(
        string from, string to, string previousFrom, string previousTo)
    {
        var period = StatsPeriod.Resolve(
            "custom", DateOnly.Parse(from, null), DateOnly.Parse(to, null), Today, Defaults);

        Assert.Equal(DateOnly.Parse(previousFrom, null), period.PreviousRange.From);
        Assert.Equal(DateOnly.Parse(previousTo, null), period.PreviousRange.To);
        Assert.Equal(period.Range.Days, period.PreviousRange.Days);
    }

    /// <summary>A one-day custom range is the same shape as <c>today</c>, just somewhere else.</summary>
    [Fact]
    public void A_single_day_custom_range_compares_against_the_day_before_it()
    {
        var day = new DateOnly(2026, 5, 1);

        var period = StatsPeriod.Resolve("custom", day, day, Today, Defaults);

        Assert.Equal(1, period.Range.Days);
        Assert.Equal(new StatsRange(new DateOnly(2026, 4, 30), new DateOnly(2026, 4, 30)), period.PreviousRange);
    }

    /// <summary>
    /// A custom period with no dates is a 400 naming both parameters — never a silent fall back to
    /// today, which would put the wrong number under the right heading.
    /// </summary>
    [Fact]
    public void Custom_without_dates_names_both_missing_parameters()
    {
        var error = Assert.Throws<MageRideValidationException>(
            () => StatsPeriod.Resolve("custom", null, null, Today, Defaults));

        Assert.Equal(MageRideErrors.ValidationFailed, error.Error);
        Assert.Contains("from", error.Errors.Keys);
        Assert.Contains("to", error.Errors.Keys);
    }

    [Fact]
    public void Custom_with_only_one_end_names_the_missing_one()
    {
        var error = Assert.Throws<MageRideValidationException>(
            () => StatsPeriod.Resolve("custom", new DateOnly(2026, 7, 1), null, Today, Defaults));

        Assert.Equal(["to"], error.Errors.Keys);
    }

    [Fact]
    public void A_backwards_custom_range_is_refused()
    {
        var error = Assert.Throws<MageRideValidationException>(
            () => StatsPeriod.Resolve("custom", new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 1), Today, Defaults));

        Assert.Equal(["to"], error.Errors.Keys);
    }

    [Fact]
    public void A_custom_range_longer_than_the_maximum_is_refused()
    {
        var options = new AnalyticsOptions { MaxRangeDays = 7 };

        var error = Assert.Throws<MageRideValidationException>(
            () => StatsPeriod.Resolve("custom", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 9), Today, options));

        Assert.Contains("at most 7 days", error.Errors["to"][0], StringComparison.Ordinal);
    }

    /// <summary>Exactly the maximum is allowed; the bound is inclusive.</summary>
    [Fact]
    public void A_custom_range_of_exactly_the_maximum_is_allowed()
    {
        var options = new AnalyticsOptions { MaxRangeDays = 7 };

        var period = StatsPeriod.Resolve("custom", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 7), Today, options);

        Assert.Equal(7, period.Range.Days);
    }

    [Fact]
    public void An_unknown_period_is_refused_and_lists_the_four()
    {
        var error = Assert.Throws<MageRideValidationException>(
            () => StatsPeriod.Resolve("quarter", null, null, Today, Defaults));

        Assert.Contains("today", error.Errors["period"][0], StringComparison.Ordinal);
        Assert.Contains("custom", error.Errors["period"][0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The four values are exactly the contract's enum. A fifth added here without the contract
    /// would be an endpoint answering for a period no client can ask for.
    /// </summary>
    [Fact]
    public void The_period_vocabulary_is_the_contracts_enum()
    {
        Assert.Equal(["today", "week", "month", "custom"], StatsPeriods.All);
    }
}

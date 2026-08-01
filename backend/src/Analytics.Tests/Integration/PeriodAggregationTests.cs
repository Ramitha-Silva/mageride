using Dapper;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Integration;

/// <summary>
/// Period aggregation over the rollup, and the vs-previous-period deltas (AL-38, US-24.7).
/// </summary>
[Collection<AnalyticsCollection>]
public sealed class PeriodAggregationTests(PostgresFixture postgres)
{
    private static readonly DateOnly Today = AnalyticsHarness.Today;

    /// <summary>
    /// The same question as the rollup, asked a different way: the Colombo date is derived
    /// <em>in SQL</em> with <c>AT TIME ZONE</c> and the whole range is grouped at once, where the
    /// component computes per-day UTC bounds in C# with <c>BusinessCalendar</c> and materialises one
    /// row per day.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not a copy of the production statement.</b> A reconciliation against the same
    /// SQL would only prove that Postgres is deterministic. Two independent formulations of "what
    /// happened between these two Colombo dates" agreeing is evidence that the read model says what
    /// the source tables say.
    /// </remarks>
    private const string DirectSourceQuery =
        """
        SELECT
            (SELECT count(DISTINCT t.ride_id)
               FROM rides.transitions t
              WHERE t.to_state = 'Completed'
                AND (t.ts AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To) AS completed_trips,

            (SELECT coalesce(sum(x.amount_minor), 0)
               FROM (SELECT DISTINCT ON (p.ride_id) p.amount_minor
                       FROM fares.ride_payments p
                       JOIN rides.transitions t
                         ON t.ride_id = p.ride_id AND t.to_state = 'Completed'
                      WHERE p.state IN ('Succeeded','FellBackToCash','CashOnDeliveryCollected','DriverConfirmedQR')
                        AND (t.ts AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To
                      ORDER BY p.ride_id, p.attempt_no DESC) x)::bigint AS gross_fare_minor,

            (SELECT count(*) FROM iam.user_roles r
              WHERE r.role = 'passenger'
                AND (r.granted_at AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To) AS new_riders,

            (SELECT count(*) FROM iam.user_roles r
              WHERE r.role = 'driver'
                AND (r.granted_at AT TIME ZONE 'Asia/Colombo')::date BETWEEN @From AND @To) AS new_drivers,

            (SELECT coalesce(sum(f.amount_minor), 0) FROM billing.daily_fee_charges f
              WHERE f.fee_date BETWEEN @From AND @To AND f.status = 'PAID')::bigint AS daily_fee_revenue_minor;
        """;

    private async Task<DashboardKpis> DirectAsync(AnalyticsHarness harness, DateOnly from, DateOnly to)
    {
        await using var connection = await harness.OpenAsync();

        return await connection.QuerySingleAsync<DashboardKpis>(DirectSourceQuery, new { From = from, To = to });
    }

    /// <summary>
    /// Seeds a week of mixed activity: four days of trips, sign-ups and fees, spread either side of
    /// the current week's start so a "week" period and its previous period both have content.
    /// </summary>
    private static async Task SeedWeekAsync(AnalyticsHarness harness)
    {
        var passenger = await harness.Seed.CreateUserAsync("passenger", Noon(2026, 7, 9));
        var driver = await harness.Seed.CreateUserAsync("driver", Noon(2026, 7, 10));
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        // Previous week (Fri 10th – Sun 12th): 3 trips, Rs 900 collected, 1 sign-up each.
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 10), 30_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 11), 30_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 12), 30_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, new DateOnly(2026, 7, 11), 10_000);

        // This week so far (Mon 13th – Wed 15th): 5 trips, one of them unpaid.
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 13), 50_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 13), 40_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 14), 60_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 15), 25_000, paymentState: "Disputed");
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 15), 20_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, new DateOnly(2026, 7, 14), 10_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, new DateOnly(2026, 7, 15), 10_000);

        await harness.Seed.CreateUserAsync("passenger", Noon(2026, 7, 14));
        await harness.Seed.CreateUserAsync("passenger", Noon(2026, 7, 15));
        await harness.Seed.CreateUserAsync("driver", Noon(2026, 7, 15));

        await harness.RollupRangeAsync(new DateOnly(2026, 7, 1), Today);
    }

    private static DateTimeOffset Noon(int year, int month, int day) =>
        new(year, month, day, 6, 0, 0, TimeSpan.Zero);   // 11:30 in Colombo

    /// <summary>
    /// The definition-of-done item: period totals reconcile with a direct query over the source
    /// tables, for every period the contract offers.
    /// </summary>
    [Theory]
    [InlineData("today")]
    [InlineData("week")]
    [InlineData("month")]
    public async Task Period_totals_reconcile_with_the_source_tables(string period)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        await SeedWeekAsync(harness);

        var stats = await harness.StatsAsync(period);
        var direct = await DirectAsync(harness, stats.Range.From, stats.Range.To);

        Assert.Equal(direct, stats.Kpis);

        // And the comparison window reconciles too, or the percentage is right over a wrong base.
        Assert.Equal(
            await DirectAsync(harness, stats.PreviousRange.From, stats.PreviousRange.To),
            stats.PreviousKpis);
    }

    /// <summary>
    /// The definition-of-done item, end to end: a custom range spanning a month boundary computes
    /// the correct previous period <em>and</em> the right totals for both.
    /// </summary>
    [Fact]
    public async Task A_custom_range_across_a_month_boundary_reconciles_on_both_sides()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var passenger = await harness.Seed.CreateUserAsync("passenger", Noon(2026, 6, 1));

        // Three trips inside the requested range, which starts in June and ends in July.
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 6, 25), 10_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 1), 20_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 5), 30_000);

        // Two inside the previous period, which starts in May and ends in June.
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 5, 30), 5_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 6, 10), 7_000);

        // One outside both, to prove the ranges are bounded at all.
        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 5, 20), 99_000);

        await harness.RollupRangeAsync(new DateOnly(2026, 5, 1), Today);

        var from = new DateOnly(2026, 6, 20);
        var to = new DateOnly(2026, 7, 10);

        var stats = await harness.StatsAsync("custom", from, to);

        // 21 days, so the previous period is the 21 days immediately before — 30 May to 19 June,
        // itself spanning a month boundary.
        Assert.Equal(21, stats.Range.Days);
        Assert.Equal(new DateOnly(2026, 5, 30), stats.PreviousRange.From);
        Assert.Equal(new DateOnly(2026, 6, 19), stats.PreviousRange.To);

        Assert.Equal(3, stats.Kpis.CompletedTrips);
        Assert.Equal(60_000, stats.Kpis.GrossFareMinor);
        Assert.Equal(2, stats.PreviousKpis.CompletedTrips);
        Assert.Equal(12_000, stats.PreviousKpis.GrossFareMinor);

        Assert.Equal(await DirectAsync(harness, from, to), stats.Kpis);
        Assert.Equal(await DirectAsync(harness, new DateOnly(2026, 5, 30), new DateOnly(2026, 6, 19)), stats.PreviousKpis);

        Assert.Equal(50d, stats.DeltaVsPrev.CompletedTripsPct);
        Assert.Equal(400d, stats.DeltaVsPrev.GrossFarePct);
    }

    /// <summary>The response echoes the resolved range, which is what SCR-AP-002 renders above the cards.</summary>
    [Fact]
    public async Task The_response_carries_the_resolved_period_and_range()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var stats = await harness.StatsAsync("month");

        Assert.Equal("month", stats.Period);
        Assert.Equal(new DateOnly(2026, 7, 1), stats.Range.From);
        Assert.Equal(Today, stats.Range.To);
    }

    /// <summary>
    /// A day the job has not materialised contributes zero rather than an error — which is what
    /// makes a period reaching back before the platform existed a question with an answer.
    /// </summary>
    [Fact]
    public async Task A_period_with_no_materialised_days_is_zero_not_an_error()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var stats = await harness.StatsAsync("custom", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 31));

        Assert.Equal(DashboardKpis.Zero, stats.Kpis);
        Assert.Equal(DashboardKpis.Zero, stats.PreviousKpis);

        // Nothing against nothing is no change, not undefined growth.
        Assert.Equal(0d, stats.DeltaVsPrev.CompletedTripsPct);
    }

    /// <summary>
    /// The period query reads the rollup and never the source tables: a trip completed after the
    /// last pass does not appear until the day is recomputed.
    /// </summary>
    /// <remarks>
    /// Stated as a test because it is the read model's defining property and its one cost. The
    /// freshness of the period figures is <c>Analytics:RollupInterval</c>, and an operator who needs
    /// this-minute numbers is looking at the live cards, which are not served from here.
    /// </remarks>
    [Fact]
    public async Task The_period_figures_come_from_the_rollup_and_move_only_when_it_runs()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var passenger = await harness.Seed.CreateUserAsync("passenger", Noon(2026, 7, 15));

        await harness.RollupAsync(Today);
        Assert.Equal(0, (await harness.StatsAsync("today")).Kpis.CompletedTrips);

        await harness.Seed.CompleteRideAsync(passenger, Noon(2026, 7, 15), 30_000);

        Assert.Equal(0, (await harness.StatsAsync("today")).Kpis.CompletedTrips);

        await harness.RollupAsync(Today);

        Assert.Equal(1, (await harness.StatsAsync("today")).Kpis.CompletedTrips);
        Assert.Equal(30_000, (await harness.StatsAsync("today")).Kpis.GrossFareMinor);
    }

    /// <summary>An invalid query is a 400 from the read model, before any database work happens.</summary>
    [Fact]
    public async Task An_invalid_period_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        await Assert.ThrowsAsync<Shared.Errors.MageRideValidationException>(() => harness.StatsAsync("custom"));
    }
}

using Dapper;
using MageRide.Analytics.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Integration;

/// <summary>
/// The <c>analytics.daily_metrics</c> materialisation job (AL-38, D-38).
/// </summary>
[Collection<AnalyticsCollection>]
public sealed class RollupTests(PostgresFixture postgres)
{
    private static readonly DateOnly Today = AnalyticsHarness.Today;

    /// <summary>
    /// Colombo is UTC+05:30, so the business day that ends on <see cref="Today"/> runs from
    /// 18:30 UTC the previous evening to 18:30 UTC this one.
    /// </summary>
    private static readonly DateTimeOffset MiddayColombo = new(2026, 7, 15, 6, 0, 0, TimeSpan.Zero);

    private async Task<AnalyticsHarness> StartAsync() => await AnalyticsHarness.StartAsync(postgres);

    [Fact]
    public async Task A_days_five_figures_are_materialised_from_the_source_tables()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);
        var driver = await harness.Seed.CreateUserAsync("driver", MiddayColombo);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.CompleteRideAsync(passenger, MiddayColombo, settledFareMinor: 45_000);
        await harness.Seed.CompleteRideAsync(passenger, MiddayColombo.AddHours(1), settledFareMinor: 30_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, Today, 10_000);

        await harness.RollupAsync(Today);

        var metric = await harness.MetricAsync(Today);

        Assert.NotNull(metric);
        Assert.Equal(2, metric.CompletedTrips);
        Assert.Equal(75_000, metric.GrossFareMinor);
        Assert.Equal(1, metric.NewRiders);
        Assert.Equal(1, metric.NewDrivers);
        Assert.Equal(10_000, metric.DailyFeeRevenueMinor);
        Assert.Equal("LKR", metric.Currency);
    }

    /// <summary>
    /// The definition-of-done item: re-running a day changes nothing but <c>refreshed_at</c>.
    /// </summary>
    /// <remarks>
    /// Idempotency here is a primary key and an <c>ON CONFLICT … DO UPDATE</c>, not a guard: the
    /// five figures are recomputed from the sources every pass, so a second run writes the same
    /// numbers. <c>metric_date_tz_at</c> is asserted <em>not</em> to move — migration 1405 defines
    /// it as the instant the day was first rolled up, which is the D-38 audit companion for the
    /// business date and would be lost if the upsert restated it.
    /// </remarks>
    [Fact]
    public async Task Re_running_a_day_is_idempotent()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);
        await harness.Seed.CompleteRideAsync(passenger, MiddayColombo, settledFareMinor: 45_000);

        await harness.RollupAsync(Today);
        var first = await harness.MetricAsync(Today);

        harness.Clock.Advance(TimeSpan.FromMinutes(30));

        await harness.RollupAsync(Today);
        await harness.RollupAsync(Today);
        var third = await harness.MetricAsync(Today);

        Assert.NotNull(first);
        Assert.NotNull(third);

        // One row, whatever happens.
        Assert.Equal(1, await harness.MetricRowCountAsync());

        Assert.Equal(first.CompletedTrips, third.CompletedTrips);
        Assert.Equal(first.GrossFareMinor, third.GrossFareMinor);
        Assert.Equal(first.NewRiders, third.NewRiders);
        Assert.Equal(first.NewDrivers, third.NewDrivers);
        Assert.Equal(first.DailyFeeRevenueMinor, third.DailyFeeRevenueMinor);

        Assert.Equal(first.MetricDateTzAt, third.MetricDateTzAt);
        Assert.True(third.RefreshedAt > first.RefreshedAt, "refreshed_at must move on a recompute.");
    }

    /// <summary>
    /// A day with nothing in it gets a zero row, not no row.
    /// </summary>
    /// <remarks>
    /// Without it, "no row" would mean both "nothing happened" and "not rolled up yet", and a period
    /// sum could not tell a quiet Sunday from a job that has been down since Friday.
    /// </remarks>
    [Fact]
    public async Task A_day_with_no_activity_is_materialised_as_zeroes()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        await harness.RollupAsync(Today);

        var metric = await harness.MetricAsync(Today);

        Assert.NotNull(metric);
        Assert.Equal(0, metric.CompletedTrips);
        Assert.Equal(0, metric.GrossFareMinor);
    }

    /// <summary>
    /// The day boundary is Asia/Colombo, not UTC (D-38). 18:29:59 UTC is still yesterday in Colombo;
    /// one second later is today.
    /// </summary>
    /// <remarks>
    /// This is the assertion the whole component turns on. A rollup that used UTC midnight would put
    /// five and a half hours of every evening's trips on the wrong day, every day, and every total
    /// would still look plausible.
    /// </remarks>
    [Fact]
    public async Task The_day_boundary_is_Colombo_midnight_not_UTC_midnight()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);

        // 2026-07-14T18:29:59Z is 2026-07-14 23:59:59 in Colombo.
        await harness.Seed.CompleteRideAsync(
            passenger, new DateTimeOffset(2026, 7, 14, 18, 29, 59, TimeSpan.Zero), settledFareMinor: 10_000);

        // 2026-07-14T18:30:00Z is 2026-07-15 00:00:00 in Colombo.
        await harness.Seed.CompleteRideAsync(
            passenger, new DateTimeOffset(2026, 7, 14, 18, 30, 0, TimeSpan.Zero), settledFareMinor: 20_000);

        await harness.RollupRangeAsync(new DateOnly(2026, 7, 14), Today);

        var yesterday = await harness.MetricAsync(new DateOnly(2026, 7, 14));
        var today = await harness.MetricAsync(Today);

        Assert.NotNull(yesterday);
        Assert.NotNull(today);
        Assert.Equal(1, yesterday.CompletedTrips);
        Assert.Equal(10_000, yesterday.GrossFareMinor);
        Assert.Equal(1, today.CompletedTrips);
        Assert.Equal(20_000, today.GrossFareMinor);
    }

    /// <summary>
    /// A fee's Colombo <c>fee_date</c> is matched directly, because subscription-svc already decided
    /// which business day it belongs to (D-13). Re-deriving it from <c>charged_at</c> would disagree
    /// with the owning service for every fee charged near midnight.
    /// </summary>
    [Fact]
    public async Task Daily_fee_revenue_follows_the_fee_date_the_charge_carries()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var driver = await harness.Seed.CreateUserAsync("driver", MiddayColombo);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);
        var second = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, Today, 10_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, second, Today, 5_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, Today.AddDays(-1), 20_000);

        await harness.RollupRangeAsync(Today.AddDays(-1), Today);

        Assert.Equal(15_000, (await harness.MetricAsync(Today))!.DailyFeeRevenueMinor);
        Assert.Equal(20_000, (await harness.MetricAsync(Today.AddDays(-1)))!.DailyFeeRevenueMinor);
    }

    /// <summary>
    /// A waived first trip moved no money, so it is not revenue — even though its own CHECK already
    /// pins its amount at zero.
    /// </summary>
    [Fact]
    public async Task A_waived_first_trip_is_not_revenue()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var driver = await harness.Seed.CreateUserAsync("driver", MiddayColombo);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, Today, 0, status: "WAIVED_FIRST_TRIP");

        await harness.RollupAsync(Today);

        Assert.Equal(0, (await harness.MetricAsync(Today))!.DailyFeeRevenueMinor);
    }

    /// <summary>
    /// Only a fare that was actually collected is gross fare (R-05). A disputed one is a terminal of
    /// the ride, not of the money; a refunded one went back.
    /// </summary>
    [Theory]
    [InlineData("Succeeded", 45_000)]
    [InlineData("FellBackToCash", 45_000)]
    [InlineData("DriverConfirmedQR", 45_000)]
    [InlineData("CashOnDeliveryCollected", 45_000)]
    [InlineData("Initiated", 0)]
    [InlineData("Pending", 0)]
    [InlineData("Disputed", 0)]
    [InlineData("Refunded", 0)]
    [InlineData("Overpaid", 0)]
    public async Task Gross_fare_counts_only_a_fare_that_was_collected(string paymentState, long expected)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);

        await harness.Seed.CompleteRideAsync(passenger, MiddayColombo, 45_000, paymentState);

        await harness.RollupAsync(Today);

        var metric = await harness.MetricAsync(Today);

        // The trip happened either way — an uncollected fare is still a completed trip.
        Assert.Equal(1, metric!.CompletedTrips);
        Assert.Equal(expected, metric.GrossFareMinor);
    }

    /// <summary>
    /// A retry chain is several <c>fares.ride_payments</c> rows for one fare (D-10). Summing the
    /// table would bill the dashboard for every attempt; the rollup takes the latest settled one.
    /// </summary>
    [Fact]
    public async Task A_retry_chain_contributes_its_fare_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);

        // Attempt 1 failed and was retried; attempt 2 collected the same fare.
        var ride = await harness.Seed.CompleteRideAsync(passenger, MiddayColombo, 45_000, paymentState: "Retried");
        await harness.Seed.AddPaymentAsync(ride.Id, 45_000, "Succeeded", "onepay", MiddayColombo.AddMinutes(5), attemptNo: 2);

        await harness.RollupAsync(Today);

        Assert.Equal(45_000, (await harness.MetricAsync(Today))!.GrossFareMinor);
    }

    /// <summary>
    /// A trip completed today whose fare settles tomorrow is a trip today with no revenue yet — and
    /// the revenue lands on the trip's day once it arrives.
    /// </summary>
    /// <remarks>
    /// This is why the scheduled pass has a lookback window rather than rolling up one day: a metric
    /// day is not closed when the day ends. A cash ride confirmed the next morning, a driver-QR
    /// attestation claimed overnight (AL-47) or a late gateway callback (R-19) all change a figure
    /// after midnight, and the day has to be recomputed for the dashboard to be right.
    /// </remarks>
    [Fact]
    public async Task A_fare_that_settles_the_next_day_lands_on_the_day_the_trip_ended()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);
        var ride = await harness.Seed.CompleteRideAsync(passenger, MiddayColombo, settledFareMinor: null);

        await harness.RollupAsync(Today);

        var before = await harness.MetricAsync(Today);
        Assert.Equal(1, before!.CompletedTrips);
        Assert.Equal(0, before.GrossFareMinor);

        // The driver confirms the QR payment the next morning.
        await harness.Seed.AddPaymentAsync(
            ride.Id, 45_000, "DriverConfirmedQR", "scan_driver_qr", MiddayColombo.AddDays(1), attemptNo: 1);

        await harness.RollupAsync(Today);

        var after = await harness.MetricAsync(Today);
        Assert.Equal(1, after!.CompletedTrips);
        Assert.Equal(45_000, after.GrossFareMinor);
    }

    /// <summary>
    /// A ride that never completed — cancelled, expired, a no-show — is not a completed trip, and
    /// the rollup finds this out from the absence of a transition rather than from a state list.
    /// </summary>
    [Fact]
    public async Task A_ride_that_never_completed_is_not_counted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var passenger = await harness.Seed.CreateUserAsync("passenger", MiddayColombo);

        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO rides.rides
                    (id, passenger_id, booker_id, client_request_id, vehicle_type,
                     pickup_geo, dropoff_geo, state, created_at, updated_at, terminal_at)
                VALUES
                    (gen_random_uuid(), @Passenger, @Passenger, gen_random_uuid(), 'three_wheeler',
                     ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                     ST_SetSRID(ST_MakePoint(79.884, 6.901), 4326)::geography,
                     'CancelledByRiderAfterAccept', @At, @At, @At);
                """,
                new { Passenger = passenger, At = MiddayColombo });
        }

        await harness.RollupAsync(Today);

        Assert.Equal(0, (await harness.MetricAsync(Today))!.CompletedTrips);
    }

    /// <summary>
    /// New riders and new drivers come from the <c>iam.user_roles</c> grant, not from
    /// <c>iam.users.role</c>.
    /// </summary>
    /// <remarks>
    /// A passenger who signs up to drive three days later is one new rider on the day they joined
    /// and one new driver on the day they were granted the role — and, crucially, is <em>still</em>
    /// a new rider for the first day when that day is recomputed. Counting <c>iam.users.role</c>
    /// would silently move them from one day's figure to another's.
    /// </remarks>
    [Fact]
    public async Task New_riders_are_counted_from_the_role_grant_not_the_accounts_primary_role()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await StartAsync();

        var joined = new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero);
        var user = await harness.Seed.CreateUserAsync("passenger", joined);

        await harness.RollupRangeAsync(new DateOnly(2026, 7, 13), Today);

        Assert.Equal(1, (await harness.MetricAsync(new DateOnly(2026, 7, 13)))!.NewRiders);

        // Two days later they also become a driver; iam.users.role moves to 'driver'.
        await harness.Seed.GrantRoleAsync(user, "driver", MiddayColombo);

        await harness.RollupRangeAsync(new DateOnly(2026, 7, 13), Today);

        Assert.Equal(1, (await harness.MetricAsync(new DateOnly(2026, 7, 13)))!.NewRiders);
        Assert.Equal(0, (await harness.MetricAsync(new DateOnly(2026, 7, 13)))!.NewDrivers);
        Assert.Equal(1, (await harness.MetricAsync(Today))!.NewDrivers);
        Assert.Equal(0, (await harness.MetricAsync(Today))!.NewRiders);
    }

    /// <summary>The scheduled pass covers today and the configured lookback, inclusive of today.</summary>
    [Fact]
    public async Task A_scheduled_pass_covers_todays_lookback_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await AnalyticsHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Analytics:RollupLookbackDays"] = "3" });

        var result = await harness.ScheduledPassAsync();

        Assert.Equal(Today.AddDays(-2), result.From);
        Assert.Equal(Today, result.To);
        Assert.Equal(3, result.DaysRolled);
        Assert.Equal(3, await harness.MetricRowCountAsync());
    }

    /// <summary>A backfill wider than the configured bound is refused rather than run.</summary>
    [Fact]
    public async Task A_backfill_beyond_the_bound_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await AnalyticsHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Analytics:MaxBackfillDays"] = "5" });

        await Assert.ThrowsAsync<Shared.Errors.MageRideValidationException>(
            () => harness.RollupRangeAsync(Today.AddDays(-30), Today));

        Assert.Equal(0, await harness.MetricRowCountAsync());
    }
}

using System.Globalization;
using MageRide.Analytics.Configuration;
using MageRide.Analytics.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Integration;

/// <summary>
/// The definition-of-done item: the job completes within its window for a day of seeded volume.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Its window" is <see cref="AnalyticsOptions.RollupInterval"/></b> — a pass that took longer
/// than the interval would have the next tick arrive before the previous pass finished, and the
/// dashboard would fall further behind with every tick. That is the claim asserted here, against
/// real rows in a real Postgres.
/// </para>
/// <para>
/// <b>The volume is a day of Mode C trips, not a synthetic row count.</b> Each seeded ride carries
/// its <c>rides.transitions</c> completion row and its settled <c>fares.ride_payments</c> attempt,
/// which are the two tables the expensive half of the rollup joins. <c>MAGERIDE_ROLLUP_VOLUME</c>
/// raises it for a soak run; the default is sized to a busy launch-phase day and to a suite that has
/// to stay quick.
/// </para>
/// <para>
/// The second assertion — a hard 30-second ceiling — is the regression guard. The interval is
/// fifteen minutes, so a pass could get two orders of magnitude slower and still satisfy the literal
/// claim while being obviously broken.
/// </para>
/// </remarks>
[Collection<AnalyticsCollection>]
public sealed class RollupWindowTests(PostgresFixture postgres)
{
    private static readonly DateOnly Today = AnalyticsHarness.Today;

    private static int Volume =>
        int.TryParse(
            Environment.GetEnvironmentVariable("MAGERIDE_ROLLUP_VOLUME"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : 2_000;

    [Fact]
    public async Task A_day_of_volume_rolls_up_inside_the_interval()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var options = new AnalyticsOptions();
        var noon = new DateTimeOffset(2026, 7, 15, 6, 0, 0, TimeSpan.Zero);
        var passenger = await harness.Seed.CreateUserAsync("passenger", noon);
        var driver = await harness.Seed.CreateUserAsync("driver", noon);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.BulkCompletedRidesAsync(passenger, noon, Volume, fareMinor: 42_500);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, Today, 10_000);

        var result = await harness.RollupAsync(Today);

        var metric = await harness.MetricAsync(Today);

        Assert.NotNull(metric);
        Assert.Equal(Volume, metric.CompletedTrips);
        Assert.Equal(Volume * 42_500L, metric.GrossFareMinor);

        Assert.True(
            result.Elapsed < options.RollupInterval,
            $"A day of {Volume} completed trips took {result.Elapsed.TotalSeconds:0.00} s, "
            + $"which is outside the {options.RollupInterval.TotalMinutes:0} min rollup interval.");

        Assert.True(
            result.Elapsed < TimeSpan.FromSeconds(30),
            $"A day of {Volume} completed trips took {result.Elapsed.TotalSeconds:0.00} s. "
            + "The pass is five aggregates over one Colombo day and should be far quicker than this.");
    }

    /// <summary>
    /// And the whole scheduled pass — the lookback window, not one day — stays inside the interval
    /// with volume on every day of it. That is what actually runs every tick.
    /// </summary>
    [Fact]
    public async Task A_scheduled_pass_over_a_loaded_lookback_window_stays_inside_the_interval()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await AnalyticsHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Analytics:RollupLookbackDays"] = "3" });

        var options = new AnalyticsOptions();
        var passenger = await harness.Seed.CreateUserAsync("passenger", AnalyticsHarness.DefaultNow);
        var perDay = Math.Max(1, Volume / 2);

        for (var offset = 0; offset < 3; offset++)
        {
            await harness.Seed.BulkCompletedRidesAsync(
                passenger,
                new DateTimeOffset(2026, 7, 15 - offset, 6, 0, 0, TimeSpan.Zero),
                perDay,
                fareMinor: 42_500);
        }

        var result = await harness.ScheduledPassAsync();

        Assert.Equal(3, result.DaysRolled);
        Assert.Equal(perDay, (await harness.MetricAsync(Today))!.CompletedTrips);
        Assert.Equal(perDay, (await harness.MetricAsync(Today.AddDays(-2)))!.CompletedTrips);

        Assert.True(
            result.Elapsed < options.RollupInterval,
            $"A three-day pass over {perDay} trips a day took {result.Elapsed.TotalSeconds:0.00} s, "
            + $"which is outside the {options.RollupInterval.TotalMinutes:0} min rollup interval.");
    }
}

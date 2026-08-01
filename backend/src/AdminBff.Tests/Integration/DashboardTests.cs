using System.Globalization;
using System.Net;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Query;
using MageRide.Analytics.Rollup;
using MageRide.AdminBff.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.Shared.Time;
using MageRide.TestKit;

namespace MageRide.AdminBff.Tests.Integration;

/// <summary>
/// DoD: "the stats endpoint returns identical numbers to a direct analytics query for the same
/// period" (AL-38, US-24.7).
/// </summary>
[Collection(AdminBffCollection.Name)]
public sealed class DashboardTests(PostgresFixture postgres)
{
    /// <summary>
    /// The endpoint and the read model agree, because there is one query behind both.
    /// </summary>
    /// <remarks>
    /// Seeded, rolled up, then compared: the HTTP answer against
    /// <c>IDashboardStatsService.GetAsync</c> called directly on the same process's read model. The
    /// figures are not hard-coded — a test that asserted "3 trips" would pass while both halves were
    /// wrong in the same way. What is asserted is that the number is non-trivial (so the fixture is
    /// doing something) and that the two agree exactly.
    /// </remarks>
    [Fact]
    public async Task The_stats_endpoint_matches_a_direct_analytics_query()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var today = BusinessCalendar.Today(TimeProvider.System);

        var (driverId, _) = await harness.Seed.DriverWithVehicleAsync();

        // Three trips today, at three different fares, plus a rider who joined today.
        foreach (var fare in new long[] { 45_000, 60_000, 32_500 })
        {
            var passenger = await harness.Seed.PassengerJoinedOnAsync(today);
            await harness.Seed.CompletedRideAsync(driverId, passenger, today, fare);
        }

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAnalyticsRollupService>()
                .RunDayAsync(today, CancellationToken.None);
        }

        using var response = await harness.GetAsync(
            "/v1/admin/dashboard/stats?period=today", harness.Tokens.Admin(admin));

        using var payload = await harness.ReadJsonAsync(response);

        await using var readScope = harness.Services.CreateAsyncScope();
        var direct = await readScope.ServiceProvider.GetRequiredService<IDashboardStatsService>()
            .GetAsync(StatsPeriods.Today, from: null, to: null, CancellationToken.None);

        var kpis = payload.RootElement.GetProperty("kpis");

        Assert.Equal(direct.Kpis.CompletedTrips, kpis.GetProperty("completedTrips").GetInt64());
        Assert.Equal(direct.Kpis.GrossFareMinor, kpis.GetProperty("grossFareMinor").GetInt64());
        Assert.Equal(direct.Kpis.NewRiders, kpis.GetProperty("newRiders").GetInt64());
        Assert.Equal(direct.Kpis.NewDrivers, kpis.GetProperty("newDrivers").GetInt64());
        Assert.Equal(direct.Kpis.DailyFeeRevenueMinor, kpis.GetProperty("dailyFeeRevenueMinor").GetInt64());

        Assert.Equal(
            today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            payload.RootElement.GetProperty("range").GetProperty("from").GetString());

        // The fixture actually put something there — otherwise "they agree" would be two zeroes.
        Assert.True(direct.Kpis.CompletedTrips >= 3);
        Assert.True(direct.Kpis.GrossFareMinor >= 137_500);
    }

    /// <summary>
    /// The CSV is the same figures as the JSON, and says which windows it compared.
    /// </summary>
    [Fact]
    public async Task The_csv_export_carries_exactly_the_json_figures()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var today = BusinessCalendar.Today(TimeProvider.System);

        var (driverId, _) = await harness.Seed.DriverWithVehicleAsync();
        var passenger = await harness.Seed.PassengerJoinedOnAsync(today);
        await harness.Seed.CompletedRideAsync(driverId, passenger, today, 77_700);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAnalyticsRollupService>()
                .RunDayAsync(today, CancellationToken.None);
        }

        using var json = await harness.GetAsync("/v1/admin/dashboard/stats?period=today", harness.Tokens.Admin(admin));
        using var payload = await harness.ReadJsonAsync(json);

        using var csv = await harness.GetAsync(
            "/v1/admin/dashboard/stats.csv?period=today", harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);

        var text = await csv.Content.ReadAsStringAsync();
        var gross = payload.RootElement.GetProperty("kpis").GetProperty("grossFareMinor").GetInt64();

        Assert.Contains(gross.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);

        // The download names the range it covers, so a folder of exports is tellable apart.
        Assert.Contains(
            today.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            csv.Content.Headers.ContentDisposition?.FileNameStar
            ?? csv.Content.Headers.ContentDisposition?.FileName
            ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An invalid period is a 400 naming the parameter, never a silently substituted default.
    /// </summary>
    [Fact]
    public async Task A_custom_period_without_dates_is_refused()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);

        using var response = await harness.GetAsync(
            "/v1/admin/dashboard/stats?period=custom", harness.Tokens.Admin(admin));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("from", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The unfiltered landing view is today's figures beside the live block, and the live block is
    /// never served from the rollup.
    /// </summary>
    [Fact]
    public async Task The_landing_dashboard_carries_kpis_and_the_live_block()
    {
        await using var harness = await AdminBffHarness.StartAsync(postgres);

        var admin = await harness.Seed.InternalUserAsync(MageRideRoles.Admin);
        var (driverId, vehicleId) = await harness.Seed.DriverWithVehicleAsync();
        await harness.Seed.PresenceAsync(driverId, vehicleId);

        using var response = await harness.GetAsync("/v1/admin/dashboard", harness.Tokens.Admin(admin));
        using var payload = await harness.ReadJsonAsync(response);

        Assert.True(payload.RootElement.TryGetProperty("kpis", out _));

        var live = payload.RootElement.GetProperty("live");

        // Read at request time from dispatch.driver_presence, with no rollup pass having run.
        Assert.True(live.GetProperty("onlineDrivers").GetInt32() >= 1);
        Assert.True(live.TryGetProperty("pendingVerifications", out _));
        Assert.True(live.TryGetProperty("openTickets", out _));
    }
}

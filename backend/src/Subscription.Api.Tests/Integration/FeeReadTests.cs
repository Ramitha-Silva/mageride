using System.Net;
using MageRide.Shared.Primitives;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>The three driver-facing reads: the rate ladder, today's status, the history.</summary>
[Collection<SubscriptionCollection>]
public sealed class FeeReadTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>URD §Daily Platform Fee Structure, as the Driver App draws it.</summary>
    [Fact]
    public async Task The_rate_ladder_is_the_seven_tiers_with_mode_a_free()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();
        var rates = await harness.GetAsync<DailyFeeRatesResponse>("/v1/fees/rates", driver.Bearer);

        var byType = rates.Items.ToDictionary(rate => rate.VehicleType, StringComparer.Ordinal);

        Assert.Equal(0, byType["bus"].DailyFeeMinor);
        Assert.Equal(0, byType["train"].DailyFeeMinor);
        Assert.Equal(5_000, byType["motorbike"].DailyFeeMinor);
        Assert.Equal(10_000, byType["three_wheeler"].DailyFeeMinor);
        Assert.Equal(15_000, byType["flex"].DailyFeeMinor);
        Assert.Equal(20_000, byType["sedan"].DailyFeeMinor);
        Assert.Equal(25_000, byType["mini_van"].DailyFeeMinor);
        Assert.Equal(30_000, byType["van"].DailyFeeMinor);

        Assert.Equal("A", byType["bus"].Mode);
        Assert.All(rates.Items, rate => Assert.Equal("LKR", rate.Currency));

        // §20 seeds no rate for the two package-delivery types: Finance configures them before a
        // delivery vehicle can go online, and an absent row is how that is said.
        Assert.DoesNotContain(rates.Items, rate => rate.VehicleType is "truck" or "mini_truck");
    }

    [Fact]
    public async Task Today_reports_the_selected_vehicles_rate_and_an_unpaid_day()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id, "sedan");
        await harness.Seed.SelectLiveAsync(driver.Id, vehicle.Id);

        var today = await harness.GetAsync<TodaysFeeResponse>($"/v1/fees/{driver.Id}/today", driver.Bearer);

        Assert.Equal("sedan", today.VehicleType);
        Assert.Equal(vehicle.Id, today.VehicleId);
        Assert.Equal(20_000, today.DailyRateMinor);
        Assert.Equal(FeeStatuses.Unpaid, today.Status);
        Assert.Equal(0, today.DeductedMinor);
        Assert.Equal(0, today.TripsToday);
        Assert.True(today.FirstTripFree);
        Assert.Equal(new DateOnly(2026, 7, 30), today.FeeDate);
    }

    [Fact]
    public async Task Today_reports_paid_and_what_was_deducted_once_the_fee_is_taken()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        await harness.Seed.SelectLiveAsync(driver.Id, vehicle.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        var today = await harness.GetAsync<TodaysFeeResponse>($"/v1/fees/{driver.Id}/today", driver.Bearer);

        Assert.Equal(FeeStatuses.Paid, today.Status);
        Assert.Equal(10_000, today.DeductedMinor);
        Assert.Equal(2, today.TripsToday);
    }

    /// <summary>
    /// A waived day still reads as <c>UNPAID</c>: the driver's first trip was free and the day's fee
    /// has not been taken.
    /// </summary>
    [Fact]
    public async Task Today_reports_unpaid_while_only_the_free_trip_has_happened()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        await harness.Seed.SelectLiveAsync(driver.Id, vehicle.Id);

        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        var today = await harness.GetAsync<TodaysFeeResponse>($"/v1/fees/{driver.Id}/today", driver.Bearer);

        Assert.Equal(FeeStatuses.Unpaid, today.Status);
        Assert.Equal(0, today.DeductedMinor);
    }

    [Fact]
    public async Task Today_is_a_404_when_no_vehicle_is_selected_and_none_was_charged()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();

        using var response = await harness.GetAsync($"/v1/fees/{driver.Id}/today", driver.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Going offline does not make the day's fee unreportable.</summary>
    [Fact]
    public async Task Today_falls_back_to_the_day_s_charge_when_no_vehicle_is_selected()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id, "van");

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        var today = await harness.GetAsync<TodaysFeeResponse>($"/v1/fees/{driver.Id}/today", driver.Bearer);

        Assert.Equal("van", today.VehicleType);
        Assert.Equal(FeeStatuses.Paid, today.Status);
        Assert.Equal(30_000, today.DeductedMinor);
    }

    [Fact]
    public async Task History_returns_one_row_per_colombo_day_newest_first()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        for (var day = 0; day < 4; day++)
        {
            await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
            await harness.ChargeOkAsync(driver.Id, vehicle.Id);
            harness.Clock.Advance(TimeSpan.FromDays(1));
        }

        var page = await harness.GetAsync<CursorPage<DailyFeeChargeResponse>>(
            $"/v1/fees/{driver.Id}/history", driver.Bearer);

        Assert.Equal(4, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.Equal(new DateOnly(2026, 8, 2), page.Items[0].FeeDate);
        Assert.Equal(new DateOnly(2026, 7, 30), page.Items[^1].FeeDate);
        Assert.All(page.Items, row => Assert.Equal(10_000, row.AmountMinor));
    }

    [Fact]
    public async Task History_pages_and_the_cursor_does_not_drop_a_two_vehicle_day()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 200_000);
        var first = await harness.Seed.VehicleAsync(driver.Id);
        var second = await harness.Seed.VehicleAsync(driver.Id, "motorbike");

        // Two vehicles on one Colombo day, then two more days. Five rows sharing three dates.
        await harness.Seed.RideAsync(driver.Id, first.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, first.Id);
        await harness.ChargeOkAsync(driver.Id, second.Id);

        for (var day = 0; day < 2; day++)
        {
            harness.Clock.Advance(TimeSpan.FromDays(1));
            await harness.Seed.RideAsync(driver.Id, first.Id, harness.Clock.GetUtcNow());
            await harness.ChargeOkAsync(driver.Id, first.Id);
        }

        var seen = new List<DailyFeeChargeResponse>();
        string? cursor = null;

        do
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await harness.GetAsync<CursorPage<DailyFeeChargeResponse>>(
                $"/v1/fees/{driver.Id}/history{query}", driver.Bearer);

            seen.AddRange(page.Items);
            cursor = page.Cursor;
        }
        while (cursor is not null);

        Assert.Equal(4, seen.Count);
        Assert.Equal(4, seen.Select(row => (row.FeeDate, row.VehicleId)).Distinct().Count());
    }

    [Fact]
    public async Task History_honours_an_inclusive_colombo_date_window()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        for (var day = 0; day < 4; day++)
        {
            await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
            await harness.ChargeOkAsync(driver.Id, vehicle.Id);
            harness.Clock.Advance(TimeSpan.FromDays(1));
        }

        var page = await harness.GetAsync<CursorPage<DailyFeeChargeResponse>>(
            $"/v1/fees/{driver.Id}/history?from=2026-07-31&to=2026-08-01", driver.Bearer);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new DateOnly(2026, 8, 1), page.Items[0].FeeDate);
        Assert.Equal(new DateOnly(2026, 7, 31), page.Items[1].FeeDate);
    }

    [Fact]
    public async Task A_malformed_date_window_is_a_field_error()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();

        using var response = await harness.GetAsync(
            $"/v1/fees/{driver.Id}/history?from=31%2F07%2F2026", driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, _) = await SubscriptionHarness.ProblemAsync(response);
        Assert.Equal("validation-failed", code);
    }

    /// <summary>Another driver's fees are not readable, whatever the path says.</summary>
    [Fact]
    public async Task One_driver_cannot_read_another_drivers_fees()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();
        var other = await harness.Seed.DriverAsync();

        using var today = await harness.GetAsync($"/v1/fees/{other.Id}/today", driver.Bearer);
        using var history = await harness.GetAsync($"/v1/fees/{other.Id}/history", driver.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, today.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
    }

    /// <summary>
    /// Finance answers fee disputes from the Admin Portal, so a back-office bearer reads any driver.
    /// </summary>
    [Fact]
    public async Task A_back_office_role_may_read_a_drivers_fee_history()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        var page = await harness.GetAsync<CursorPage<DailyFeeChargeResponse>>(
            $"/v1/fees/{driver.Id}/history", finance);

        Assert.Single(page.Items);
        Assert.Equal(10_000, page.Items[0].AmountMinor);
    }

    [Fact]
    public async Task Every_fee_read_demands_a_bearer()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();

        foreach (var path in new[] { "/v1/fees/rates", $"/v1/fees/{driver.Id}/today", $"/v1/fees/{driver.Id}/history" })
        {
            using var response = await harness.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}

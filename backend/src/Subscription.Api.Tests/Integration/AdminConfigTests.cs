using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// Admin Portal Config (SCR-AP-007): the rate ladder and the bulk-voucher discount ladder.
/// </summary>
[Collection<SubscriptionCollection>]
public sealed class AdminConfigTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>DoD: "a rate change through admin applies from the next charge without retro-billing."</summary>
    [Fact]
    public async Task A_rate_change_applies_from_the_next_charge_and_never_backwards()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 200_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        var atOldRate = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(10_000, atOldRate.AmountMinor);

        using (var update = await harness.PutAsync(
                   "/v1/admin/fees/rates",
                   new { items = new[] { new { vehicleType = "three_wheeler", dailyFeeMinor = 12_500, mode = "C" } } },
                   finance))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        // Yesterday's row is untouched: the amount recorded is the amount that actually moved, and it
        // is what wallet-svc's entry says too.
        var rows = await harness.ChargeRowsAsync(driver.Id);
        Assert.Single(rows);
        Assert.Equal(10_000, rows[0].AmountMinor);

        harness.Clock.Advance(TimeSpan.FromDays(1));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        var atNewRate = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(12_500, atNewRate.AmountMinor);
        Assert.Equal(200_000 - 10_000 - 12_500, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>
    /// The same Colombo day is not re-priced either: the day's fee was settled before the change.
    /// </summary>
    [Fact]
    public async Task A_rate_change_does_not_re_price_a_day_already_paid()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 200_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        using (var update = await harness.PutAsync(
                   "/v1/admin/fees/rates",
                   new { items = new[] { new { vehicleType = "three_wheeler", dailyFeeMinor = 30_000, mode = "C" } } },
                   finance))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        var again = await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        Assert.Equal(10_000, again.AmountMinor);
        Assert.Equal(200_000 - 10_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>A PUT edits what it was sent and un-configures nothing.</summary>
    [Fact]
    public async Task Updating_one_rate_leaves_the_rest_of_the_ladder_alone()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        using var response = await harness.PutAsync(
            "/v1/admin/fees/rates",
            new { items = new[] { new { vehicleType = "van", dailyFeeMinor = 35_000, mode = "C" } } },
            finance);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rates = await Read<DailyFeeRatesResponse>(response);
        var byType = rates.Items.ToDictionary(rate => rate.VehicleType, StringComparer.Ordinal);

        Assert.Equal(35_000, byType["van"].DailyFeeMinor);
        Assert.Equal(10_000, byType["three_wheeler"].DailyFeeMinor);
        Assert.Equal(8, rates.Items.Count);
    }

    /// <summary>
    /// A rate for a vehicle type Finance has not priced is configurable, and unlocks the type.
    /// </summary>
    [Fact]
    public async Task Configuring_a_truck_rate_lets_a_delivery_vehicle_go_online()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var truck = await harness.Seed.VehicleAsync(driver.Id, "truck");
        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        await harness.Seed.RideAsync(driver.Id, truck.Id, harness.Clock.GetUtcNow());

        using (var before = await harness.ChargeAsync(driver.Id, truck.Id))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        using (var update = await harness.PutAsync(
                   "/v1/admin/fees/rates",
                   new { items = new[] { new { vehicleType = "truck", dailyFeeMinor = 40_000, mode = "C" } } },
                   finance))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        var charge = await harness.ChargeOkAsync(driver.Id, truck.Id);
        Assert.Equal(40_000, charge.AmountMinor);
    }

    /// <summary>The Mode A fence, held where the number is written.</summary>
    [Fact]
    public async Task A_non_zero_mode_a_rate_is_refused()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        using var response = await harness.PutAsync(
            "/v1/admin/fees/rates",
            new { items = new[] { new { vehicleType = "bus", dailyFeeMinor = 30_000, mode = "A" } } },
            finance);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var (code, body) = await SubscriptionHarness.ProblemAsync(response);
        Assert.Equal("validation-failed", code);
        Assert.Contains("Mode A is free", body.GetProperty("errors").ToString(), StringComparison.Ordinal);
    }

    /// <summary>A type outside AL-09's vocabulary is refused rather than stored and never matched.</summary>
    [Theory]
    [InlineData("car")]
    [InlineData("lorry")]
    [InlineData("")]
    public async Task A_vehicle_type_outside_al_09_is_refused(string vehicleType)
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        using var response = await harness.PutAsync(
            "/v1/admin/fees/rates",
            new { items = new[] { new { vehicleType, dailyFeeMinor = 10_000, mode = "C" } } },
            finance);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Voucher_discount_tiers_are_set_per_denomination_and_recorded_against_the_admin()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var adminId = await harness.Seed.UserAsync("admin");
        var admin = harness.Tokens.Admin(adminId);

        using var response = await harness.PutAsync(
            "/v1/admin/voucher-discount-tiers",
            new
            {
                tiers = new[]
                {
                    new { denominationMinor = 100_000, discountBps = 1_250, active = true },
                    new { denominationMinor = 2_000_000, discountBps = 1_800, active = true },
                },
            },
            admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tiers = await Read<VoucherDiscountTiersResponse>(response);
        var byDenomination = tiers.Tiers.ToDictionary(tier => tier.DenominationMinor);

        Assert.Equal(1_250, byDenomination[100_000].DiscountBps);
        Assert.Equal(1_800, byDenomination[2_000_000].DiscountBps);

        // Untouched rungs survive: this is an upsert, not a replacement of the ladder.
        Assert.Equal(1_500, byDenomination[1_000_000].DiscountBps);

        await using var connection = await harness.OpenAsync();
        var updatedBy = await Dapper.SqlMapper.ExecuteScalarAsync<Guid?>(
            connection,
            "SELECT updated_by FROM billing.voucher_discount_tiers WHERE denomination_minor = 100000;");

        Assert.Equal(adminId, updatedBy);
    }

    /// <summary>
    /// The same table wallet-svc's admin route writes — both spellings are in D3' Part 2 and both are
    /// landed, so a write through one has to be visible through the other.
    /// </summary>
    [Fact]
    public async Task The_tier_ladder_is_the_same_rows_wallet_svc_serves()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var admin = harness.Tokens.Admin(await harness.Seed.UserAsync("admin"));

        using (var update = await harness.PutAsync(
                   "/v1/admin/voucher-discount-tiers",
                   new { tiers = new[] { new { denominationMinor = 500_000, discountBps = 2_000, active = true } } },
                   admin))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/wallet/admin/voucher-discount-tiers");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);

        using var walletResponse = await harness.Wallet.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, walletResponse.StatusCode);

        var text = await walletResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);

        var tier = document.RootElement.GetProperty("tiers").EnumerateArray()
            .Single(row => row.GetProperty("denominationMinor").GetInt64() == 500_000);

        Assert.Equal(2_000, tier.GetProperty("discountBps").GetInt32());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task A_discount_outside_zero_to_one_hundred_percent_is_refused(int discountBps)
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var admin = harness.Tokens.Admin(await harness.Seed.UserAsync("admin"));

        using var response = await harness.PutAsync(
            "/v1/admin/voucher-discount-tiers",
            new { tiers = new[] { new { denominationMinor = 100_000, discountBps, active = true } } },
            admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Only Finance, Admin and Super Admin. A Support CSR who could reprice the platform is not a
    /// permission any spec grants.
    /// </summary>
    [Fact]
    public async Task The_config_surface_is_closed_to_drivers_and_to_the_other_back_office_roles()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();
        var support = harness.Tokens.SupportCsr(await harness.Seed.UserAsync("support_csr"));

        var body = new { items = new[] { new { vehicleType = "van", dailyFeeMinor = 0, mode = "C" } } };

        using var byDriver = await harness.PutAsync("/v1/admin/fees/rates", body, driver.Bearer);
        using var byCsr = await harness.PutAsync("/v1/admin/fees/rates", body, support);

        Assert.Equal(HttpStatusCode.Forbidden, byDriver.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byCsr.StatusCode);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(MageRideJson.Options))!;
}

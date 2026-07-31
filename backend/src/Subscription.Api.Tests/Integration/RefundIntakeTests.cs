using System.Net;
using System.Net.Http.Json;
using MageRide.Shared.Http;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// US-9.23's fee-refund intake: the driver raises it, Support and Finance answer it, and the reversal
/// itself is admin-bff's (US-14.11, C065).
/// </summary>
[Collection<SubscriptionCollection>]
public sealed class RefundIntakeTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task A_driver_charged_in_error_raises_a_support_ticket_carrying_the_charge()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        using var response = await harness.PostAsync(
            $"/v1/fees/{driver.Id}/refund-requests",
            new { feeDate = "2026-07-30", reason = "The app crashed on Go Online and I never got a hire." },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var request = (await response.Content.ReadFromJsonAsync<FeeRefundRequestResponse>(MageRideJson.Options))!;

        Assert.Equal("OPEN", request.Status);
        Assert.Equal(new DateOnly(2026, 7, 30), request.FeeDate);
        Assert.Equal(10_000, request.AmountMinor);
        Assert.Equal("LKR", request.Currency);

        await using var connection = await harness.OpenAsync();

        var ticket = await Dapper.SqlMapper.QuerySingleAsync<(string Category, string Description, string Status)>(
            connection,
            "SELECT category, description, status FROM support.tickets WHERE id = @Id;",
            new { Id = request.RequestId });

        Assert.Equal("daily_fee_refund", ticket.Category);
        Assert.Equal("OPEN", ticket.Status);
        Assert.Contains("2026-07-30", ticket.Description, StringComparison.Ordinal);
        Assert.Contains("app crashed", ticket.Description, StringComparison.Ordinal);

        // The intake moves nothing. The reversal is a wallet credit admin-bff makes (US-14.11).
        Assert.Equal(100_000 - 10_000, await harness.BalanceAsync(driver.Id));
    }

    /// <summary>A day with no deduction has nothing to refund, and says so.</summary>
    [Fact]
    public async Task A_day_that_was_never_charged_is_refused()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();

        using var response = await harness.PostAsync(
            $"/v1/fees/{driver.Id}/refund-requests",
            new { feeDate = "2026-07-30", reason = "I think I was charged." },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await TicketCountAsync(harness));
    }

    /// <summary>The free first trip is not a deduction, so it cannot be disputed as one.</summary>
    [Fact]
    public async Task A_waived_day_cannot_be_disputed()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        using var response = await harness.PostAsync(
            $"/v1/fees/{driver.Id}/refund-requests",
            new { feeDate = "2026-07-30", reason = "I want this back." },
            driver.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await TicketCountAsync(harness));
    }

    /// <summary>
    /// R-14: a retried POST replays instead of putting a second identical claim on the queue.
    /// </summary>
    [Fact]
    public async Task A_retried_request_replays_instead_of_raising_a_second_ticket()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        var key = Guid.NewGuid().ToString();
        var body = new { feeDate = "2026-07-30", reason = "Charged twice, I think." };

        using var first = await harness.PostWithKeyAsync(
            $"/v1/fees/{driver.Id}/refund-requests", body, driver.Bearer, key);
        using var second = await harness.PostWithKeyAsync(
            $"/v1/fees/{driver.Id}/refund-requests", body, driver.Bearer, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        Assert.Equal(1, await TicketCountAsync(harness));
    }

    [Fact]
    public async Task A_request_with_no_reason_is_refused()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        using var response = await harness.PostAsync(
            $"/v1/fees/{driver.Id}/refund-requests", new { feeDate = "2026-07-30" }, driver.Bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Nobody raises a claim in a driver's name — not another driver, and not a back-office role.
    /// </summary>
    [Fact]
    public async Task Only_the_driver_may_raise_their_own_refund_request()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);
        var other = await harness.Seed.DriverAsync();
        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        var body = new { feeDate = "2026-07-30", reason = "Not mine to raise." };

        using var byOtherDriver = await harness.PostAsync(
            $"/v1/fees/{driver.Id}/refund-requests", body, other.Bearer);
        using var byFinance = await harness.PostAsync(
            $"/v1/fees/{driver.Id}/refund-requests", body, finance);

        Assert.Equal(HttpStatusCode.Forbidden, byOtherDriver.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byFinance.StatusCode);
        Assert.Equal(0, await TicketCountAsync(harness));
    }

    /// <summary>A driver sees their own claims; Finance may read them for the queue.</summary>
    [Fact]
    public async Task A_driver_can_list_their_own_requests()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync(openingBalanceMinor: 100_000);
        var vehicle = await harness.Seed.VehicleAsync(driver.Id);

        await harness.Seed.RideAsync(driver.Id, vehicle.Id, harness.Clock.GetUtcNow());
        await harness.ChargeOkAsync(driver.Id, vehicle.Id);

        using (var created = await harness.PostAsync(
                   $"/v1/fees/{driver.Id}/refund-requests",
                   new { feeDate = "2026-07-30", reason = "Charged in error." },
                   driver.Bearer))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var mine = await harness.GetAsync<FeeRefundRequestsResponse>(
            $"/v1/fees/{driver.Id}/refund-requests", driver.Bearer);

        var request = Assert.Single(mine.Items);
        Assert.Equal("OPEN", request.Status);

        // support.tickets holds no column for the disputed day or amount, so the list does not claim to.
        Assert.Null(request.FeeDate);
        Assert.Null(request.AmountMinor);
    }

    private static async Task<int> TicketCountAsync(SubscriptionHarness harness)
    {
        await using var connection = await harness.OpenAsync();

        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            connection, "SELECT count(*)::int FROM support.tickets;");
    }
}

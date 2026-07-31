using System.Net;
using System.Net.Http.Json;
using MageRide.Shared.Http;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Subscriptions.Tests.Integration;

/// <summary>
/// The platform's Mode B monthly charge (~Rs 300 per vehicle, first month free) and the AL-03
/// consolidated-invoicing hand-off to fleet-billing-svc (C060).
/// </summary>
[Collection<SubscriptionCollection>]
public sealed class ModeBBillingTests(PostgresFixture postgres, RedisFixture redis)
{
    private const long MonthlyFee = 30_000;

    /// <summary>A vehicle registered before this month owes the monthly fee.</summary>
    [Fact]
    public async Task An_established_mode_b_vehicle_is_charged_the_monthly_platform_fee()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        var vehicle = await harness.Seed.VehicleAsync(
            owner, "mini_van", mode: "B", createdAt: new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero));

        var run = await RunAsync(harness);

        Assert.Equal(new DateOnly(2026, 7, 1), run.PeriodMonth);
        Assert.Equal(1, run.Raised);
        Assert.Equal(0, run.FreeMonths);
        Assert.Equal(MonthlyFee, run.TotalMinor);

        var charges = await ChargesAsync(harness);
        var line = Assert.Single(charges.Items, item => item.VehicleId == vehicle.Id);

        Assert.Equal("DUE", line.Status);
        Assert.Equal(MonthlyFee, line.AmountMinor);
        Assert.Equal(new DateOnly(2026, 7, 1), line.PeriodMonth);
    }

    /// <summary>"First month free" is anchored to the vehicle's own registration month.</summary>
    [Fact]
    public async Task A_vehicle_registered_this_month_is_free_for_this_month_and_due_the_next()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        var vehicle = await harness.Seed.VehicleAsync(
            owner, "van", mode: "B", createdAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));

        var july = await RunAsync(harness);

        Assert.Equal(1, july.Raised);
        Assert.Equal(1, july.FreeMonths);
        Assert.Equal(0, july.TotalMinor);

        var free = Assert.Single((await ChargesAsync(harness)).Items, item => item.VehicleId == vehicle.Id);
        Assert.Equal("FREE", free.Status);
        Assert.Equal(0, free.AmountMinor);

        var august = await RunAsync(harness, "2026-08-01");

        Assert.Equal(1, august.Raised);
        Assert.Equal(0, august.FreeMonths);
        Assert.Equal(MonthlyFee, august.TotalMinor);

        var due = Assert.Single(
            (await ChargesAsync(harness, "2026-08-01")).Items, item => item.VehicleId == vehicle.Id);
        Assert.Equal("DUE", due.Status);
        Assert.Equal(MonthlyFee, due.AmountMinor);
    }

    /// <summary>Nothing is billed for a month that ended before the vehicle existed.</summary>
    [Fact]
    public async Task A_month_before_the_vehicle_existed_raises_nothing()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        await harness.Seed.VehicleAsync(
            owner, "van", mode: "B", createdAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));

        var june = await RunAsync(harness, "2026-06-01");

        Assert.Equal(0, june.Raised);
        Assert.Empty((await ChargesAsync(harness, "2026-06-01")).Items);
    }

    /// <summary>DoD-adjacent (C060's): "re-running invoice generation for a month is idempotent."</summary>
    [Fact]
    public async Task Re_running_a_month_raises_nothing_and_restates_nothing()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        await harness.Seed.VehicleAsync(
            owner, "van", mode: "B", createdAt: new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        var first = await RunAsync(harness);
        Assert.Equal(1, first.Raised);

        var second = await RunAsync(harness);
        Assert.Equal(0, second.Raised);

        var third = await RunAsync(harness);
        Assert.Equal(0, third.Raised);

        Assert.Single((await ChargesAsync(harness)).Items);
    }

    /// <summary>
    /// AL-03: "Mode A vehicles never appear as a charged line", and Mode C is never fleet-billed.
    /// </summary>
    [Fact]
    public async Task Mode_a_and_mode_c_vehicles_get_no_monthly_line_at_all()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        var registered = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var bus = await harness.Seed.VehicleAsync(owner, "bus", mode: "A", createdAt: registered);
        var threeWheeler = await harness.Seed.VehicleAsync(owner, "three_wheeler", createdAt: registered);
        var van = await harness.Seed.VehicleAsync(owner, "van", mode: "B", createdAt: registered);

        var run = await RunAsync(harness);
        Assert.Equal(1, run.Raised);

        var items = (await ChargesAsync(harness)).Items;

        Assert.Contains(items, item => item.VehicleId == van.Id);
        Assert.DoesNotContain(items, item => item.VehicleId == bus.Id);
        Assert.DoesNotContain(items, item => item.VehicleId == threeWheeler.Id);
    }

    /// <summary>An unapproved vehicle is not billed.</summary>
    [Fact]
    public async Task A_vehicle_that_is_not_approved_is_not_billed()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        var vehicle = await harness.Seed.VehicleAsync(
            owner, "van", mode: "B", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                "UPDATE registry.vehicles SET status = 'PENDING' WHERE id = @Id;",
                new { Id = vehicle.Id });
        }

        var run = await RunAsync(harness);

        Assert.Equal(0, run.Raised);
    }

    /// <summary>
    /// The AL-03 hand-off: a fleet's vehicles consolidate into one total with a per-vehicle breakdown.
    /// </summary>
    [Fact]
    public async Task A_fleets_lines_consolidate_into_one_total_for_c060_to_invoice()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        var registered = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await harness.Seed.VehicleAsync(owner, "van", mode: "B", createdAt: registered);
        var second = await harness.Seed.VehicleAsync(owner, "mini_van", mode: "B", createdAt: registered);
        var busInSameFleet = await harness.Seed.VehicleAsync(owner, "bus", mode: "A", createdAt: registered);
        var independent = await harness.Seed.VehicleAsync(
            await harness.Seed.UserAsync("driver"), "van", mode: "B", createdAt: registered);

        var fleetId = await harness.Seed.FleetAsync(owner, first.Id, second.Id, busInSameFleet.Id);

        await RunAsync(harness);

        var charges = await ChargesAsync(harness);

        var fleet = Assert.Single(charges.Fleets, total => total.FleetId == fleetId);
        Assert.Equal(2, fleet.VehicleCount);
        Assert.Equal(2 * MonthlyFee, fleet.TotalMinor);

        // A vehicle in no fleet groups under a null fleetId — it belongs to no consolidated invoice.
        var unfleeted = Assert.Single(charges.Fleets, total => total.FleetId is null);
        Assert.Equal(1, unfleeted.VehicleCount);
        Assert.Contains(charges.Items, item => item.VehicleId == independent.Id && item.FleetId is null);

        Assert.Equal(3 * MonthlyFee, charges.TotalMinor);

        // Narrowing to one fleet is what C060 asks for when it invoices that fleet.
        var justTheFleet = await ChargesAsync(harness, fleetId: fleetId);

        Assert.Equal(2, justTheFleet.Items.Count);
        Assert.All(justTheFleet.Items, item => Assert.Equal(fleetId, item.FleetId));
    }

    /// <summary>
    /// The per-vehicle charge is a statement of what is owed and posts nothing: §10 gives it no
    /// <c>journal_entry_id</c>, and there is no journal <c>kind</c> a monthly fee could carry.
    /// </summary>
    [Fact]
    public async Task The_monthly_charge_writes_no_ledger_entry()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var owner = await harness.Seed.UserAsync("fleet_owner");
        await harness.Seed.VehicleAsync(
            owner, "van", mode: "B", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        await RunAsync(harness);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(0, await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            connection, "SELECT count(*)::int FROM billing.journal_entries;"));

        // fleet-billing-svc (C060) owns billing.fleet_invoices. This service must not have written one.
        Assert.Equal(0, await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            connection, "SELECT count(*)::int FROM billing.fleet_invoices;"));
    }

    /// <summary>The background runner raises the current Colombo month with no request at all.</summary>
    [Fact]
    public async Task The_background_runner_raises_the_current_month()
    {
        await using var harness = await SubscriptionHarness.StartAsync(
            postgres,
            redis,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Subscription:ModeBBillingEnabled"] = "true",
                ["Subscription:ModeBBillingInterval"] = "00:01:00",
            });

        var owner = await harness.Seed.UserAsync("fleet_owner");
        var vehicle = await harness.Seed.VehicleAsync(
            owner, "van", mode: "B", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        // The runner's first pass happens at start-up, before this vehicle existed, so the clock is
        // advanced inside the poll: a single Advance would race the hosted service's timer creation.
        var raised = await WaitForAsync(async () =>
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(1));

            await using var connection = await harness.OpenAsync();

            return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection,
                "SELECT count(*)::int FROM billing.monthly_subscriptions WHERE vehicle_id = @Id;",
                new { Id = vehicle.Id }) == 1;
        });

        Assert.True(raised, "the Mode B runner did not raise the current month's charge");
    }

    /// <summary>The run is on the internal plane — it is not something a driver can trigger.</summary>
    [Fact]
    public async Task The_run_is_not_reachable_without_the_internal_key()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();

        using var response = await harness.PostAsync("/v1/internal/fees/mode-b/run", null, bearer: driver.Bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Reading the month's charges is Finance's, not a driver's.</summary>
    [Fact]
    public async Task The_charge_view_is_closed_to_drivers()
    {
        await using var harness = await SubscriptionHarness.StartAsync(postgres, redis);

        var driver = await harness.Seed.DriverAsync();

        using var response = await harness.GetAsync("/v1/admin/fees/mode-b/charges", driver.Bearer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<ModeBRunResponse> RunAsync(SubscriptionHarness harness, string? month = null)
    {
        var path = month is null ? "/v1/internal/fees/mode-b/run" : $"/v1/internal/fees/mode-b/run?month={month}";

        using var response = await harness.PostAsync(
            path, null, internalKey: SubscriptionHarness.InternalApiKey);

        var text = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"POST {path} returned {(int)response.StatusCode}: {text}");

        return (await response.Content.ReadFromJsonAsync<ModeBRunResponse>(MageRideJson.Options))!;
    }

    private static async Task<ModeBChargesResponse> ChargesAsync(
        SubscriptionHarness harness, string? month = null, Guid? fleetId = null)
    {
        var query = new List<string>();

        if (month is not null)
        {
            query.Add($"month={month}");
        }

        if (fleetId is not null)
        {
            query.Add($"fleetId={fleetId}");
        }

        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
        var finance = harness.Tokens.FinanceOfficer(await harness.Seed.UserAsync("finance_officer"));

        return await harness.GetAsync<ModeBChargesResponse>($"/v1/admin/fees/mode-b/charges{suffix}", finance);
    }

    /// <remarks>
    /// The runner is a background loop, so its effect arrives on its own thread. Polled rather than
    /// slept on: the assertion is "it happened", and a fixed sleep would be either flaky or slow.
    /// </remarks>
    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}

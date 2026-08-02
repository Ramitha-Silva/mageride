using System.Net;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// DoD items 1 and 2: "only an APPROVED Mode C vehicle or an assigned/shared Mode A/B vehicle is
/// go-live eligible (US-9.6)" and "selecting a vehicle live releases the previous one atomically;
/// two live vehicles per driver is impossible".
/// </summary>
[Collection<RegistryCollection>]
public sealed class GoLiveEligibilityTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task An_approved_owned_Mode_C_vehicle_is_eligible()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal(vehicleId, body.GetProperty("vehicleId").GetString());
        Assert.Equal("owned", body.GetProperty("source").GetString());
        Assert.False(body.TryGetProperty("releasedVehicleId", out _));
    }

    [Fact]
    public async Task A_pending_vehicle_is_not_eligible()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicle = await harness.RegisterVehicleAsync(bearer);
        var vehicleId = vehicle.GetProperty("vehicleId").GetString();

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "vehicle-not-approved");
    }

    /// <summary>
    /// E-03: an expired document suspends dispatch, and a suspended vehicle is not one a driver
    /// may take live even though it is still APPROVED.
    /// </summary>
    [Fact]
    public async Task A_document_suspended_vehicle_is_not_eligible()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);
        await harness.SuspendDispatchAsync(Guid.Parse(vehicleId));

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "vehicle-not-approved");
    }

    /// <summary>
    /// US-13.9: an assigned driver did not register the vehicle and may still go online with it.
    /// This is the case migration 0311 relaxed the C021 composite foreign key for.
    /// </summary>
    [Fact]
    public async Task An_assigned_fleet_vehicle_is_eligible_for_a_driver_who_does_not_own_it()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);
        var fleetId = await harness.AssignToFleetAsync(vehicleId, driverId, fleetOwnerId);

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("assigned", body.GetProperty("source").GetString());

        // And it renders in US-13.9's separate group, with the assigning fleet.
        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        var assigned = mine.GetProperty("assigned").EnumerateArray().ToArray();

        Assert.Single(assigned);
        Assert.Equal(vehicleId.ToString(), assigned[0].GetProperty("vehicleId").GetString());
        Assert.Equal(fleetId.ToString(), assigned[0].GetProperty("fleetId").GetString());
        Assert.True(assigned[0].GetProperty("isSelected").GetBoolean());
    }

    /// <summary>
    /// Δ MCS-02 — SCR-DA/DI-026's caption: "Lanka Fleet (Pvt) Ltd · until 30 Jun". The list read
    /// could name neither the fleet nor the date, so the group rendered without the two facts
    /// that make it meaningful.
    /// </summary>
    [Fact]
    public async Task An_assigned_vehicle_carries_the_fleet_name_and_the_assignment_expiry()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);
        await harness.AssignToFleetAsync(vehicleId, driverId, fleetOwnerId, expiresAt, "Lanka Fleet (Pvt) Ltd");

        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        var assigned = mine.GetProperty("assigned").EnumerateArray().Single();

        Assert.Equal("Lanka Fleet (Pvt) Ltd", assigned.GetProperty("fleetName").GetString());
        Assert.Equal(
            expiresAt.ToUnixTimeSeconds(),
            assigned.GetProperty("assignedUntil").GetDateTimeOffset().ToUnixTimeSeconds());
    }

    /// <summary>
    /// US-13.9's auto-expiry, end to end: an assignment whose window has closed is gone from the
    /// list with nobody having revoked anything. The view is what enforces it (migration 0314) —
    /// this asserts the app sees the result rather than having to check a date itself.
    /// </summary>
    [Fact]
    public async Task An_expired_assignment_disappears_with_no_revocation()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);
        // `ck_fleet_assign_window` refuses an assignment that expires before it starts — an
        // assignment like that confers nothing and would sit in the roster looking live (0314).
        // A lapsed one is a window that opened in the past and has since closed.
        await harness.AssignToFleetAsync(
            vehicleId,
            driverId,
            fleetOwnerId,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            validFrom: DateTimeOffset.UtcNow.AddHours(-2));

        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));

        Assert.Empty(mine.GetProperty("assigned").EnumerateArray());

        // And it cannot be selected either — the same view answers both questions (US-9.6).
        var select = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);
        Assert.NotEqual(HttpStatusCode.OK, select.StatusCode);
    }

    /// <summary>An owned vehicle carries neither field — they are the assignment's, not the vehicle's.</summary>
    [Fact]
    public async Task An_owned_vehicle_carries_no_fleet_caption()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await harness.RegisterVehicleAsync(bearer);

        // The response is `items` (everything) plus `assigned` (the US-13.9 subset); there is no
        // separate `owned` array — the app slices on `source`.
        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        var owned = mine.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("source").GetString() == "owned");

        Assert.False(owned.TryGetProperty("fleetName", out _));
        Assert.False(owned.TryGetProperty("assignedUntil", out _));
    }

    /// <summary>US-13.8: revoking the assignment takes the entitlement away immediately.</summary>
    [Fact]
    public async Task A_revoked_assignment_is_no_longer_eligible_or_listed()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var fleetOwnerId = await harness.CreateDriverAsync();
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.SeedFleetVehicleAsync(fleetOwnerId);
        await harness.AssignToFleetAsync(vehicleId, driverId, fleetOwnerId);
        await harness.RevokeAssignmentsAsync(driverId);

        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        Assert.Empty(mine.GetProperty("items").EnumerateArray());

        // 404, not 403: the projection is scoped by driver, so a vehicle they have no entitlement
        // to and one that does not exist are the same query result.
        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer),
            HttpStatusCode.NotFound,
            "vehicle-not-found");
    }

    [Fact]
    public async Task Another_drivers_vehicle_is_not_eligible()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var mineId = await harness.CreateDriverAsync();
        var theirsId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(theirsId));

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, harness.Tokens.Driver(mineId)),
            HttpStatusCode.NotFound,
            "vehicle-not-found");
    }

    /// <summary>
    /// DoD item 2. The selection is one column on a row keyed by the driver, so there is no window
    /// in which two are set — the release and the acquire are the same UPDATE.
    /// </summary>
    [Fact]
    public async Task Selecting_a_second_vehicle_releases_the_first_atomically()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var first = await harness.RegisterApprovedVehicleAsync(bearer);
        var second = await harness.RegisterApprovedVehicleAsync(bearer);

        await harness.PostAsync($"/v1/vehicles/{first}/select-live", null, bearer);
        var response = await harness.PostAsync($"/v1/vehicles/{second}/select-live", null, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            first, (await RegistryHarness.ReadJsonAsync(response)).GetProperty("releasedVehicleId").GetString());

        var selected = (await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer)))
            .GetProperty("items")
            .EnumerateArray()
            .Where(item => item.GetProperty("isSelected").GetBoolean())
            .Select(item => item.GetProperty("vehicleId").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal([second], selected);
    }

    /// <summary>
    /// The database says the same thing under concurrency: ten selections racing across two
    /// vehicles leave exactly one, whichever wins.
    /// </summary>
    [Fact]
    public async Task Two_live_vehicles_for_one_driver_is_impossible_under_a_race()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var first = await harness.RegisterApprovedVehicleAsync(bearer);
        var second = await harness.RegisterApprovedVehicleAsync(bearer);

        var attempts = Enumerable.Range(0, 10)
            .Select(i => harness.PostAsync($"/v1/vehicles/{(i % 2 == 0 ? first : second)}/select-live", null, bearer));

        var responses = await Task.WhenAll(attempts);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        Assert.Equal(1, await harness.ActiveSelectionCountAsync(driverId));
    }

    /// <summary>
    /// D-03. The selection is published into <c>lock:driver:{driverId}</c> so the dispatch and
    /// tracking planes agree with the registry about which vehicle it is.
    /// </summary>
    [Fact]
    public async Task The_selection_is_published_for_the_downstream_planes()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = redis.ConnectionString });

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var first = await harness.RegisterApprovedVehicleAsync(bearer);
        await harness.PostAsync($"/v1/vehicles/{first}/select-live", null, bearer);

        Assert.Equal(first, await harness.PublishedLiveVehicleAsync(driverId));

        var second = await harness.RegisterApprovedVehicleAsync(bearer);
        await harness.PostAsync($"/v1/vehicles/{second}/select-live", null, bearer);

        Assert.Equal(second, await harness.PublishedLiveVehicleAsync(driverId));
    }

    /// <summary>
    /// Postgres holds the invariant, so an unreachable Redis costs a cache and not a driver's
    /// shift. The harness points at a dead address by default, which is what this exercises.
    /// </summary>
    [Fact]
    public async Task A_selection_still_succeeds_when_the_cache_is_unreachable()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await harness.ActiveSelectionCountAsync(driverId));
    }
}

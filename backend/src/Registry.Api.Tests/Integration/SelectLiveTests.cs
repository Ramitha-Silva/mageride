using System.Net;
using System.Text.Json;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// <c>POST /v1/vehicles/{vehicleId}/select-live</c> and <c>GET /v1/vehicles/mine</c> — US-9.6's
/// "only one vehicle can go live at a time" and US-9.7's dashboard read.
/// </summary>
[Collection<PostgresCollection>]
public sealed class SelectLiveTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Selecting_a_second_vehicle_replaces_the_first()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var first = await harness.RegisterApprovedVehicleAsync(bearer);
        var second = await harness.RegisterApprovedVehicleAsync(bearer);

        Assert.Equal(HttpStatusCode.OK, (await harness.PostAsync($"/v1/vehicles/{first}/select-live", null, bearer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.PostAsync($"/v1/vehicles/{second}/select-live", null, bearer)).StatusCode);

        // US-9.6 is a replacement, not an addition. The invariant is registry.driver_profiles'
        // primary key, so there is nowhere for a second selection to live.
        var selected = await SelectedVehicleIdsAsync(harness, bearer);
        Assert.Equal([second], selected);
    }

    [Fact]
    public async Task The_selection_response_names_the_vehicle_and_when_it_was_chosen()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var plate = RegistryHarness.NextPlate();
        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer, plate);

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RegistryHarness.ReadJsonAsync(response);

        // US-9.7 puts the registration number on the driver dashboard, so it comes back with the
        // selection rather than costing a second round trip.
        Assert.Equal(vehicleId, body.GetProperty("vehicleId").GetString());
        Assert.Equal(plate, body.GetProperty("registrationNumber").GetString());
        Assert.Equal("three_wheeler", body.GetProperty("vehicleType").GetString());
        Assert.Equal("C", body.GetProperty("mode").GetString());

        // The instant is the one the row carries, not the one the process happened to observe.
        await using var connection = await harness.OpenAsync();
        var stored = await connection.QuerySingleAsync<DateTimeOffset>(
            "SELECT active_vehicle_selected_at FROM registry.driver_profiles WHERE driver_id = @DriverId;",
            new { DriverId = driverId });

        Assert.Equal(stored, body.GetProperty("selectedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_pending_vehicle_cannot_be_taken_live()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString();

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "vehicle-not-approved");
    }

    /// <summary>
    /// A driver with no entitlement to a vehicle cannot take it live.
    /// </summary>
    /// <remarks>
    /// <b>404, not 403 — changed by C028.</b> C021 answered <c>not-owner</c>, because ownership
    /// was the whole rule and the vehicle was read unscoped. US-13.9 made the rule "owned <b>or</b>
    /// assigned", which registry-svc reads out of <c>registry.driver_eligible_vehicles</c> — a
    /// projection scoped by driver, so a vehicle the caller has no entitlement to and one that does
    /// not exist are literally the same query result. Answering 403 again would need a second read
    /// whose only purpose is to tell a stranger that somebody else's plate is registered.
    /// </remarks>
    [Fact]
    public async Task A_driver_cannot_take_another_drivers_vehicle_live()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerBearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = await harness.RegisterApprovedVehicleAsync(ownerBearer);

        var intruder = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, intruder);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "vehicle-not-found");
    }

    /// <summary>
    /// What the database still guarantees about the selection after C028.
    /// </summary>
    /// <remarks>
    /// C021 asserted migration 0308's composite foreign key to
    /// <c>registry.vehicles(id, owner_id)</c> — "a driver may only select a vehicle they own",
    /// enforced by Postgres. <b>Migration 0311 relaxed that</b>, because US-13.9 gives an assigned
    /// non-owner the right to select a fleet vehicle and the composite key rejected exactly that.
    /// The invariant was restated, not dropped: entitlement now spans two tables and is enforced
    /// by registry-svc against the projection every consumer reads, and the database still refuses
    /// a selection that names no real vehicle at all.
    /// </remarks>
    [Fact]
    public async Task The_database_still_refuses_a_selection_that_names_no_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        await harness.RegisterVehicleAsync(harness.Tokens.Driver(driverId));

        await using var connection = await harness.OpenAsync();
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => connection.ExecuteAsync(
            """
            UPDATE registry.driver_profiles
               SET active_vehicle_id = @VehicleId, active_vehicle_selected_at = now()
             WHERE driver_id = @DriverId;
            """,
            new { VehicleId = Guid.NewGuid(), DriverId = driverId }));

        Assert.Equal("23503", exception.SqlState);
        Assert.Equal("fk_driver_profiles_active_vehicle_id", exception.ConstraintName);
    }

    /// <summary>
    /// And the half 0311 gave up is genuinely gone, so nobody reads the relaxation as an accident:
    /// the schema now permits a non-owner's selection, and registry-svc is what refuses one.
    /// </summary>
    [Fact]
    public async Task The_database_no_longer_scopes_the_selection_to_the_owner()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerBearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = Guid.Parse(await harness.RegisterApprovedVehicleAsync(ownerBearer));

        var otherId = await harness.CreateDriverAsync();
        await harness.RegisterVehicleAsync(harness.Tokens.Driver(otherId));

        await using var connection = await harness.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE registry.driver_profiles
               SET active_vehicle_id = @VehicleId, active_vehicle_selected_at = now()
             WHERE driver_id = @DriverId;
            """,
            new { VehicleId = vehicleId, DriverId = otherId });

        Assert.Equal(1, await harness.ActiveSelectionCountAsync(otherId));

        // The route still refuses it — that is where the rule lives now.
        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, harness.Tokens.Driver(otherId)),
            HttpStatusCode.NotFound,
            "vehicle-not-found");
    }

    [Fact]
    public async Task A_vehicle_that_does_not_exist_is_a_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());

        foreach (var id in new[] { Guid.NewGuid().ToString(), "not-a-vehicle-id" })
        {
            var response = await harness.PostAsync($"/v1/vehicles/{id}/select-live", null, bearer);
            await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "vehicle-not-found");
        }
    }

    [Fact]
    public async Task My_vehicles_lists_every_vehicle_and_marks_the_selected_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var first = await harness.RegisterApprovedVehicleAsync(bearer);
        var second = (await harness.RegisterVehicleAsync(bearer, vehicleType: "sedan")).GetProperty("vehicleId").GetString();

        await harness.PostAsync($"/v1/vehicles/{first}/select-live", null, bearer);

        var items = await ListMineAsync(harness, bearer);

        // US-2.8: multi-vehicle, both rendered, Approved and Incomplete side by side (AL-30).
        Assert.Equal([first, second], items.Select(i => i.GetProperty("vehicleId").GetString()));
        Assert.Equal(["APPROVED", "PENDING"], items.Select(i => i.GetProperty("status").GetString()));
        Assert.Equal(["approved", "incomplete"], items.Select(i => i.GetProperty("onboardingStatus").GetString()));
        Assert.Equal([true, false], items.Select(i => i.GetProperty("isSelected").GetBoolean()));
    }

    [Fact]
    public async Task My_vehicles_shows_only_the_callers_own()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var mine = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var theirs = harness.Tokens.Driver(await harness.CreateDriverAsync());

        var vehicleId = await harness.RegisterApprovedVehicleAsync(mine);
        await harness.RegisterApprovedVehicleAsync(theirs);

        Assert.Equal([vehicleId], (await ListMineAsync(harness, mine)).Select(i => i.GetProperty("vehicleId").GetString()));
    }

    [Fact]
    public async Task A_driver_with_no_vehicles_gets_an_empty_list_not_an_error()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        Assert.Empty(await ListMineAsync(harness, harness.Tokens.Driver(await harness.CreateDriverAsync())));
    }

    private static async Task<JsonElement[]> ListMineAsync(RegistryHarness harness, string bearer)
    {
        var response = await harness.GetAsync("/v1/vehicles/mine", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await RegistryHarness.ReadJsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static async Task<string[]> SelectedVehicleIdsAsync(RegistryHarness harness, string bearer) =>
    [
        .. (await ListMineAsync(harness, bearer))
            .Where(i => i.GetProperty("isSelected").GetBoolean())
            .Select(i => i.GetProperty("vehicleId").GetString()!),
    ];
}

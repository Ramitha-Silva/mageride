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

    [Fact]
    public async Task A_driver_cannot_take_another_drivers_vehicle_live()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerBearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = await harness.RegisterApprovedVehicleAsync(ownerBearer);

        var intruder = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, intruder);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-owner");
    }

    [Fact]
    public async Task The_database_refuses_a_selection_of_someone_elses_vehicle_even_without_the_service()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerId = await harness.CreateDriverAsync();
        var ownerBearer = harness.Tokens.Driver(ownerId);
        var vehicleId = Guid.Parse(await harness.RegisterApprovedVehicleAsync(ownerBearer));

        var intruderId = await harness.CreateDriverAsync();
        await harness.RegisterVehicleAsync(harness.Tokens.Driver(intruderId));

        // The service check above is the useful error; this is the backstop that means a future
        // component cannot reintroduce the hole by writing the column directly (migration 0308's
        // composite FK to registry.vehicles(id, owner_id)).
        await using var connection = await harness.OpenAsync();
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => connection.ExecuteAsync(
            """
            UPDATE registry.driver_profiles
               SET active_vehicle_id = @VehicleId, active_vehicle_selected_at = now()
             WHERE driver_id = @DriverId;
            """,
            new { VehicleId = vehicleId, DriverId = intruderId }));

        Assert.Equal("23503", exception.SqlState);
        Assert.Equal("fk_driver_profiles_active_vehicle", exception.ConstraintName);
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

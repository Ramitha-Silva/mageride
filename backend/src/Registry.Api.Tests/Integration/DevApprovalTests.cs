using System.Net;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;
using Microsoft.Extensions.Hosting;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// The dev seed approval — mapped only where it was asked for, and never reachable without a
/// driver bearer that owns the vehicle.
/// </summary>
[Collection<PostgresCollection>]
public sealed class DevApprovalTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Approving_moves_the_vehicle_and_its_onboarding_status_together()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString();

        var response = await harness.PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);

        // AL-30 treats "approved" as the derived view of the same fact. Leaving them apart is
        // what makes a vehicle Approved on one screen and Incomplete on the next.
        Assert.Equal("APPROVED", body.GetProperty("status").GetString());
        Assert.Equal("approved", body.GetProperty("onboardingStatus").GetString());
    }

    [Fact]
    public async Task Approving_twice_is_a_no_op_rather_than_a_conflict()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString();

        // Different Idempotency-Keys, so this is the endpoint's own re-entrancy under test, not
        // the command log's replay.
        Assert.Equal(HttpStatusCode.OK, (await harness.PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer)).StatusCode);
    }

    [Fact]
    public async Task A_driver_cannot_approve_another_drivers_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var ownerBearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(ownerBearer)).GetProperty("vehicleId").GetString();

        var response = await harness.PostAsync(
            $"/v1/dev/vehicles/{vehicleId}/approve", null, harness.Tokens.Driver(await harness.CreateDriverAsync()));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-owner");
    }

    [Fact]
    public async Task Outside_development_the_route_is_not_mapped_at_all()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres, Environments.Production);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString();

        var response = await harness.PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer);

        // Not 403: an unmapped route makes the endpoint undiscoverable rather than merely
        // refused, so a deployment that did not ask for it gives nothing away.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Outside_development_an_operator_can_still_turn_it_on_for_the_replica()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        // The lightweight production replica runs synthetic data under the Production
        // environment name and still needs an approved vehicle to book a ride against.
        await using var harness = await RegistryHarness.StartAsync(
            postgres,
            Environments.Production,
            new Dictionary<string, string?> { ["Registry:DevApprovalEnabled"] = "true" });

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString();

        Assert.Equal(HttpStatusCode.OK, (await harness.PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer)).StatusCode);
    }

    [Fact]
    public async Task In_development_an_operator_can_turn_it_off()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await using var harness = await RegistryHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Registry:DevApprovalEnabled"] = "false" });

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await harness.PostAsync($"/v1/dev/vehicles/{vehicleId}/approve", null, bearer)).StatusCode);
    }
}

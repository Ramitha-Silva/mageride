using System.Net;
using System.Text.Json;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// The Mode B subscriber roster (US-4.7, US-NEW.1) and the internal plane's own fence.
/// </summary>
[Collection<RegistryCollection>]
public sealed class SubscriberAndMerchantTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_owner_reads_the_roster_of_their_Mode_B_vehicle()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var firstPassenger = await harness.CreateDriverAsync();
        var secondPassenger = await harness.CreateDriverAsync();

        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);
        await harness.SeedSubscriptionGrantAsync(vehicleId, firstPassenger);
        await harness.SeedSubscriptionGrantAsync(vehicleId, secondPassenger);

        var body = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/subscribers", harness.Tokens.Driver(ownerId)));

        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal("active", item.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task The_roster_pages_and_hands_back_a_cursor()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);

        for (var i = 0; i < 3; i++)
        {
            await harness.SeedSubscriptionGrantAsync(vehicleId, await harness.CreateDriverAsync());
        }

        var first = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/subscribers?limit=2", bearer));

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync(
                $"/v1/vehicles/{vehicleId}/subscribers?limit=2&cursor={Uri.EscapeDataString(cursor!)}", bearer));

        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.False(second.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task A_cursor_this_endpoint_did_not_issue_is_refused()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);

        await ProblemDocument.AssertAsync(
            await harness.GetAsync(
                $"/v1/vehicles/{vehicleId}/subscribers?cursor=notbase64", harness.Tokens.Driver(ownerId)),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    [Fact]
    public async Task Another_drivers_roster_is_not_readable()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var strangerId = await harness.CreateDriverAsync();

        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);

        await ProblemDocument.AssertAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/subscribers", harness.Tokens.Driver(strangerId)),
            HttpStatusCode.Forbidden,
            "not-owner");
    }



    // -----------------------------------------------------------------------------------------
    // Δ AL-57 — four merchant-bind tests REMOVED with D-11. OnePay supports one merchant account per
    // merchant, so the per-driver sub-account `POST /v1/internal/vehicles/{id}/merchant` wrote never
    // existed: every row it could produce was MageRide's own id repeated once per driver. Where a
    // driver's money goes is now `registry.driver_payout_profiles` (AL-58) and payout-svc's weekly
    // sweep, whose own suite proves it. `Without_a_configured_secret_the_route_does_not_exist` is
    // KEPT below — the internal plane still carries AL-30's recompute, and that fence still holds.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Unset <c>Registry:InternalApiKey</c> means the route is not mapped at all — a deployment
    /// that forgets it gets 404s, not an unauthenticated write to <c>registry.driver_payouts</c>.
    /// </summary>
    /// <remarks>
    /// Asserted with a signed-in driver's token rather than anonymously: the kernel's
    /// deny-by-default fallback policy also applies to requests that match no endpoint, so an
    /// anonymous caller sees <c>401</c> for a route that does not exist and for one that does. A
    /// token gets past the fallback and leaves routing to answer, which is the thing under test.
    /// </remarks>
    [Fact]
    public async Task Without_a_configured_secret_the_route_does_not_exist()
    {
        await using var harness = await RegistryHarness.StartAsync(
            postgres, new Dictionary<string, string?> { ["Registry:InternalApiKey"] = string.Empty });

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        var response = await harness.PostAsync(
            $"/v1/internal/vehicles/{vehicleId}/onboarding/recompute", null, bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

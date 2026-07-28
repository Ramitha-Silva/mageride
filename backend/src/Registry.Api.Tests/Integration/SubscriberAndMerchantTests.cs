using System.Net;
using System.Text.Json;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// The Mode B subscriber roster (US-4.7, US-NEW.1) and the D-11 OnePay merchant binding.
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

    /// <summary>
    /// US-NEW.1: the passenger loses visibility now, and the row stays MUTED on the owner's roster
    /// until they delete it (US-4.12) — so the grant is still listed, as `unsubscribed`.
    /// </summary>
    [Fact]
    public async Task A_passenger_unsubscribes_and_the_row_stays_muted()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();
        var passenger = harness.Tokens.Issue(passengerId, [MageRideRoles.Passenger], MageRideApps.Passenger);

        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);
        await harness.SeedSubscriptionGrantAsync(vehicleId, passengerId);

        var response = await harness.DeleteAsync($"/v1/vehicles/{vehicleId}/subscribers/{passengerId}", passenger);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The same directed removal a revoke earns (D-22), carrying the passenger fanout needs.
        var revocation = Assert.Single(await harness.OutboxAsync(vehicleId), e => e.EventType == "share.revoked");
        using var payload = JsonDocument.Parse(revocation.Payload);
        Assert.Equal(passengerId.ToString(), payload.RootElement.GetProperty("passengerId").GetString());
        Assert.Equal("unsubscribed", payload.RootElement.GetProperty("reason").GetString());

        var roster = await RegistryHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/vehicles/{vehicleId}/subscribers", harness.Tokens.Driver(ownerId)));

        var row = Assert.Single(roster.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal("unsubscribed", row.GetProperty("status").GetString());
    }

    /// <summary>
    /// The owner's removal is a different verb on a different service (subscription.yaml,
    /// US-4.12): letting them through here would silently perform the wrong one.
    /// </summary>
    [Fact]
    public async Task An_owner_cannot_unsubscribe_somebody_else_through_this_route()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();

        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);
        await harness.SeedSubscriptionGrantAsync(vehicleId, passengerId);

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync(
                $"/v1/vehicles/{vehicleId}/subscribers/{passengerId}", harness.Tokens.Driver(ownerId)),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    [Fact]
    public async Task Unsubscribing_without_a_grant_is_not_found()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var passengerId = await harness.CreateDriverAsync();

        var vehicleId = await harness.SeedFleetVehicleAsync(ownerId);

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync(
                $"/v1/vehicles/{vehicleId}/subscribers/{passengerId}",
                harness.Tokens.Issue(passengerId, [MageRideRoles.Passenger], MageRideApps.Passenger)),
            HttpStatusCode.NotFound,
            "not-found");
    }

    // ---------------------------------------------------------------------------------------
    // D-11 — the OnePay merchant binding
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Approval_binds_the_drivers_OnePay_merchant()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(bearer);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/vehicles/{vehicleId}/merchant",
            new { merchantId = "ONEPAY-MR-0001", merchantRef = "ref-1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal(vehicleId, body.GetProperty("vehicleId").GetString());
        Assert.Equal("ONEPAY-MR-0001", body.GetProperty("merchantId").GetString());

        // Keyed on the driver, not the vehicle: settlement pays a person.
        Assert.Equal("ONEPAY-MR-0001", await harness.MerchantIdAsync(driverId));
    }

    /// <summary>A driver's second vehicle reaching APPROVED must not fail on the same binding.</summary>
    [Fact]
    public async Task Binding_a_second_vehicle_for_the_same_driver_rebinds_rather_than_failing()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var first = await harness.RegisterApprovedVehicleAsync(bearer);
        var second = await harness.RegisterApprovedVehicleAsync(bearer);

        await harness.PostInternalAsync(
            $"/v1/internal/vehicles/{first}/merchant", new { merchantId = "ONEPAY-A" });

        var response = await harness.PostInternalAsync(
            $"/v1/internal/vehicles/{second}/merchant", new { merchantId = "ONEPAY-B" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ONEPAY-B", await harness.MerchantIdAsync(driverId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_binding_needs_a_merchant_id(string? merchantId)
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(
            await harness.PostInternalAsync($"/v1/internal/vehicles/{vehicleId}/merchant", new { merchantId }),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    /// <summary>
    /// The gateway refuses <c>/v1/internal/**</c> at the edge (C008), and the service refuses it
    /// again — a shared secret until C042 lands a mesh.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("the-wrong-key")]
    public async Task Without_the_internal_secret_the_bind_is_refused(string? apiKey)
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var driverId = await harness.CreateDriverAsync();

        var vehicleId = await harness.RegisterApprovedVehicleAsync(harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(
            await harness.PostInternalAsync(
                $"/v1/internal/vehicles/{vehicleId}/merchant", new { merchantId = "ONEPAY-X" }, apiKey),
            HttpStatusCode.Unauthorized,
            "unauthorized");

        Assert.Null(await harness.MerchantIdAsync(driverId));
    }

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
            $"/v1/internal/vehicles/{vehicleId}/merchant", new { merchantId = "ONEPAY-X" }, bearer);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

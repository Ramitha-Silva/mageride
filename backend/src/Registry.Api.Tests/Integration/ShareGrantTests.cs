using System.Net;
using System.Text.Json;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// DoD item 3: "<c>share.revoked</c> is emitted through the outbox and carries the passenger id
/// fanout needs" — Mode B sharing (D-22, US-4.1–4.7, US-NEW.1).
/// </summary>
[Collection<RegistryCollection>]
public sealed class ShareGrantTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_grant_is_pending_until_the_sharee_accepts_it()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);

        var created = await harness.PostAsync(
            $"/v1/vehicles/{vehicleId}/share", new { userId = granteeId.ToString() }, owner);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var grantId = (await RegistryHarness.ReadJsonAsync(created)).GetProperty("grantId").GetString();

        // US-4.3b: nothing is published until the sharee accepts, because a PENDING grant confers
        // no visibility for fanout to act on.
        Assert.Empty(await harness.OutboxAsync(Guid.Parse(vehicleId)));

        var accepted = await harness.PostAsync(
            $"/v1/vehicles/{vehicleId}/share/{grantId}/accept", null, harness.Tokens.Driver(granteeId));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("active", (await RegistryHarness.ReadJsonAsync(accepted)).GetProperty("status").GetString());

        var events = await harness.OutboxAsync(Guid.Parse(vehicleId));
        Assert.Equal("share.granted", Assert.Single(events).EventType);
    }

    /// <summary>
    /// DoD item 3, stated exactly: the event goes through <c>registry.outbox</c> — not a direct
    /// publish — and its payload names the passenger, which D6' §5.1's <c>{vehicleId}</c> does not.
    /// </summary>
    [Fact]
    public async Task Revoking_a_grant_queues_share_revoked_with_the_passenger_id()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        var grantId = await harness.GrantShareAsync(vehicleId, granteeId, owner);

        var revoked = await harness.DeleteAsync($"/v1/vehicles/{vehicleId}/share/{grantId}", owner);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        var events = await harness.OutboxAsync(Guid.Parse(vehicleId));
        var revocation = events.Single(e => e.EventType == "share.revoked");

        // The partition key is the vehicle, so a later share.granted for the same passenger cannot
        // overtake this and restore visibility that was taken away.
        Assert.Equal(Guid.Parse(vehicleId), revocation.AggregateId);

        using var payload = JsonDocument.Parse(revocation.Payload);
        Assert.Equal(granteeId.ToString(), payload.RootElement.GetProperty("passengerId").GetString());
        Assert.Equal(vehicleId, payload.RootElement.GetProperty("vehicleId").GetString());
        Assert.Equal("revoked", payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Revoking_twice_emits_one_event()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        var grantId = await harness.GrantShareAsync(vehicleId, granteeId, owner);

        await harness.DeleteAsync($"/v1/vehicles/{vehicleId}/share/{grantId}", owner);
        var second = await harness.DeleteAsync($"/v1/vehicles/{vehicleId}/share/{grantId}", owner);

        // 204: the caller asked for the grant to be gone and it is. The conditional UPDATE is what
        // stops a second share.revoked going out.
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var events = await harness.OutboxAsync(Guid.Parse(vehicleId));
        Assert.Single(events, e => e.EventType == "share.revoked");
    }

    [Fact]
    public async Task Only_the_named_grantee_may_accept()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var strangerId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        var created = await harness.PostAsync(
            $"/v1/vehicles/{vehicleId}/share", new { userId = granteeId.ToString() }, owner);
        var grantId = (await RegistryHarness.ReadJsonAsync(created)).GetProperty("grantId").GetString();

        await ProblemDocument.AssertAsync(
            await harness.PostAsync(
                $"/v1/vehicles/{vehicleId}/share/{grantId}/accept", null, harness.Tokens.Driver(strangerId)),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    [Fact]
    public async Task Only_the_owner_may_grant_or_revoke()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var strangerId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);
        var stranger = harness.Tokens.Driver(strangerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        var grantId = await harness.GrantShareAsync(vehicleId, granteeId, owner);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/share", new { userId = strangerId.ToString() }, stranger),
            HttpStatusCode.Forbidden,
            "not-owner");

        await ProblemDocument.AssertAsync(
            await harness.DeleteAsync($"/v1/vehicles/{vehicleId}/share/{grantId}", stranger),
            HttpStatusCode.Forbidden,
            "not-owner");
    }

    [Fact]
    public async Task A_second_live_grant_for_the_same_user_is_a_conflict()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        await harness.GrantShareAsync(vehicleId, granteeId, owner);

        var duplicate = await harness.PostAsync(
            $"/v1/vehicles/{vehicleId}/share", new { userId = granteeId.ToString() }, owner);

        await ProblemDocument.AssertAsync(duplicate, HttpStatusCode.Conflict, "conflict");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-id")]
    public async Task A_grant_needs_a_real_user_id(string? userId)
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/share", new { userId }, owner),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_refused()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var granteeId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync(
                $"/v1/vehicles/{vehicleId}/share",
                new { userId = granteeId.ToString(), expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                owner),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    [Fact]
    public async Task A_vehicle_cannot_be_shared_with_its_own_owner()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);

        await ProblemDocument.AssertAsync(
            await harness.PostAsync($"/v1/vehicles/{vehicleId}/share", new { userId = ownerId.ToString() }, owner),
            HttpStatusCode.BadRequest,
            "validation-failed");
    }

    /// <summary>
    /// US-2.16 cascades: a vehicle that is off the map while a grant still says otherwise is
    /// exactly the leak D-22 is about, so deactivation revokes every live grant in the same
    /// transaction and owes each grantee a directed removal.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_vehicle_revokes_every_live_grant_on_it()
    {
        await using var harness = await RegistryHarness.StartAsync(postgres);
        var ownerId = await harness.CreateDriverAsync();
        var firstGrantee = await harness.CreateDriverAsync();
        var secondGrantee = await harness.CreateDriverAsync();
        var owner = harness.Tokens.Driver(ownerId);

        var vehicleId = await harness.RegisterApprovedVehicleAsync(owner);
        await harness.GrantShareAsync(vehicleId, firstGrantee, owner);
        await harness.GrantShareAsync(vehicleId, secondGrantee, owner);

        var response = await harness.PostAsync($"/v1/vehicles/{vehicleId}/deactivate", null, owner);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var events = await harness.OutboxAsync(Guid.Parse(vehicleId));

        Assert.Single(events, e => e.EventType == "vehicle.deactivated");

        var revocations = events.Where(e => e.EventType == "share.revoked").ToArray();
        Assert.Equal(2, revocations.Length);

        var passengers = revocations
            .Select(e => JsonDocument.Parse(e.Payload).RootElement.GetProperty("passengerId").GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { firstGrantee.ToString(), secondGrantee.ToString() }.Order(StringComparer.Ordinal),
            passengers);

        Assert.All(
            revocations,
            e => Assert.Equal(
                "vehicle-deactivated",
                JsonDocument.Parse(e.Payload).RootElement.GetProperty("reason").GetString()));
    }
}

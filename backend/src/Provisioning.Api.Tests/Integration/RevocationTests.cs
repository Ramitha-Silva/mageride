using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Auth;
using MageRide.Shared.Caching;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary>
/// T-12 / US-3.8: a revoked credential stops authenticating, on both transports, well inside 60 s.
/// </summary>
[Collection<ProvisioningCollection>]
public sealed class RevocationTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>T-12's budget, and the one the DoD names.</summary>
    private static readonly TimeSpan RevocationBudget = TimeSpan.FromSeconds(60);

    /// <summary>The DoD assertion, on the TCP path the adapter authenticates through.</summary>
    [Fact]
    public async Task A_decommission_stops_a_credential_authenticating_well_inside_sixty_seconds()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var adminId = await harness.CreateUserAsync("admin");
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);
        var serial = bound.GetProperty("credentialSerial").GetString()!;

        var before = await ValidateAsync(harness, imei, serial);
        Assert.True(before.GetProperty("valid").GetBoolean());

        var elapsed = Stopwatch.StartNew();

        var decommissioned = await harness.DeleteAsync(
            $"/v1/trackers/{imei}",
            harness.Tokens.Issue(adminId, [MageRideRoles.Admin], MageRideApps.Admin));

        Assert.Equal(HttpStatusCode.NoContent, decommissioned.StatusCode);

        var after = await ValidateAsync(harness, imei, serial);
        elapsed.Stop();

        Assert.False(after.GetProperty("valid").GetBoolean());
        Assert.Equal(BindingStates.Revoked, after.GetProperty("state").GetString());
        Assert.True(
            elapsed.Elapsed < RevocationBudget,
            $"revocation took {elapsed.Elapsed} — T-12 budgets {RevocationBudget}");

        // The Redis half: the cache entry is gone, so the adapter's next connect misses and falls
        // through to a Postgres read that refuses it.
        Assert.Null(await harness.CachedVehicleAsync(imei));

        // The durable half: every credential on the binding is stamped, so the CRL and any
        // consumer of provisioning.events agree with the cache.
        var certificates = await harness.CertificatesAsync(imei);
        Assert.All(certificates, certificate => Assert.NotNull(certificate.RevokedAt));
        Assert.All(certificates, certificate =>
            Assert.Equal(RevocationReasons.CessationOfOperation, certificate.Reason));

        var events = await harness.OutboxAsync(vehicleId);
        Assert.Contains(events, e => e.EventType == TrackerEventTypes.TrackerUnbound);
        Assert.Contains(events, e => e.EventType == TrackerEventTypes.TrackerRevoked);
    }

    /// <summary>
    /// The fast half of T-12 (D6' §4.2): the adapter is told, rather than left to discover it on
    /// the cache's 24 h TTL, so it can force-close the socket inside a second.
    /// </summary>
    [Fact]
    public async Task A_revocation_is_published_on_the_pub_sub_channel_the_adapter_subscribes_to()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(bearer, imei, vehicleId);
        var serial = bound.GetProperty("credentialSerial").GetString()!;

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscriber = harness.Services.GetRequiredService<IConnectionMultiplexer>().GetSubscriber();
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(RedisKeys.TrackerCredentialChannel),
            (_, value) => received.TrySetResult(value.ToString()));

        var unbound = await harness.PostAsync("/v1/trackers/unbind", new { imei }, bearer);
        Assert.Equal(HttpStatusCode.NoContent, unbound.StatusCode);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "the revocation signal did not arrive on prov:tracker");

        using var signal = JsonDocument.Parse(await received.Task);

        Assert.Equal(TrackerEventTypes.TrackerRevoked, signal.RootElement.GetProperty("type").GetString());
        Assert.Equal(imei, signal.RootElement.GetProperty("imei").GetString());
        Assert.Equal(vehicleId.ToString(), signal.RootElement.GetProperty("vehicleId").GetString());

        // The serials are what a broker or a holder of certificates matches on; the IMEI is what
        // the adapter matches an open socket by. Both are on the message because the two consumers
        // key differently.
        Assert.Contains(
            serial,
            signal.RootElement.GetProperty("serials").EnumerateArray().Select(item => item.GetString()));
    }

    /// <summary>
    /// The MQTT half. EMQX cannot be told to drop a session, so X.509's answer applies: the serial
    /// goes on the CRL the broker fetches from the distribution point in the certificate.
    /// </summary>
    [Fact]
    public async Task A_revoked_certificate_appears_on_the_crl_the_broker_fetches()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(bearer, imei, vehicleId);
        var serial = bound.GetProperty("credentialSerial").GetString()!;

        Assert.Null(Assert.Single(await harness.CertificatesAsync(imei)).RevokedAt);

        await harness.PostAsync("/v1/trackers/unbind", new { imei }, bearer);

        // The first fetch this harness makes, so the built list is not being served out of the
        // few-seconds cache that keeps a broker fleet's refresh interval from becoming one table
        // scan per node.
        var response = await harness.GetInternalAsync("/v1/internal/trackers/crl.pem");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var pem = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("-----BEGIN X509 CRL-----", pem, StringComparison.Ordinal);
        Assert.Contains(serial, HexOf(pem), StringComparison.Ordinal);
    }

    /// <summary>
    /// An owner may release their own tracker; only an admin may decommission one. D3''s route
    /// table marks the DELETE "admin", and a decommission is not a thing an owner does.
    /// </summary>
    [Fact]
    public async Task An_owner_may_unbind_but_may_not_decommission()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(bearer, imei, vehicleId);

        var refused = await harness.DeleteAsync($"/v1/trackers/{imei}", bearer);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var unbound = await harness.PostAsync("/v1/trackers/unbind", new { imei }, bearer);
        Assert.Equal(HttpStatusCode.NoContent, unbound.StatusCode);

        var binding = Assert.Single(await harness.BindingsAsync(imei));
        Assert.Equal(BindingStates.Revoked, binding.State);
        Assert.Equal(BindingStateReasons.Unbound, binding.Reason);
    }

    /// <summary>
    /// A released IMEI can be bound again immediately — that is the whole point of the unbind, and
    /// <c>ux_tracker_imei_active</c>'s predicate is what frees the slot.
    /// </summary>
    [Fact]
    public async Task An_unbound_imei_can_be_rebound_to_another_vehicle_at_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var first = await harness.CreateVehicleAsync(driverId);
        var second = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(bearer, imei, first);
        await harness.PostAsync("/v1/trackers/unbind", new { imei }, bearer);

        var rebound = await harness.BindAsync(bearer, imei, second);

        Assert.Equal(second.ToString(), rebound.GetProperty("vehicleId").GetString());
        Assert.Equal(second.ToString(), await harness.CachedVehicleAsync(imei));
    }

    /// <summary>Unbinding somebody else's tracker is refused before anything is revoked.</summary>
    [Fact]
    public async Task A_stranger_cannot_unbind_a_tracker()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var ownerId = await harness.CreateUserAsync();
        var strangerId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(ownerId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(ownerId), imei, vehicleId);

        var response = await harness.PostAsync(
            "/v1/trackers/unbind", new { imei }, harness.Tokens.Driver(strangerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-owner");
        Assert.Equal(BindingStates.Active, Assert.Single(await harness.BindingsAsync(imei)).State);
    }

    /// <summary>
    /// The internal family is service-to-service only. An adapter without the secret gets 401, not
    /// an IMEI oracle.
    /// </summary>
    [Fact]
    public async Task Validate_refuses_a_caller_without_the_internal_secret()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var response = await harness.GetInternalAsync(
            $"/v1/internal/trackers/{ProvisioningHarness.NextImei()}/validate", apiKey: null);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    /// <summary>An IMEI nobody bound is a verdict, not an error — the adapter closes the socket either way.</summary>
    [Fact]
    public async Task An_unknown_imei_validates_false_rather_than_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var response = await harness.GetInternalAsync(
            $"/v1/internal/trackers/{ProvisioningHarness.NextImei()}/validate");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var verdict = await ProvisioningHarness.ReadJsonAsync(response);
        Assert.False(verdict.GetProperty("valid").GetBoolean());
        Assert.False(verdict.TryGetProperty("vehicleId", out _));
    }

    private static async Task<JsonElement> ValidateAsync(ProvisioningHarness harness, string imei, string serial) =>
        await ProvisioningHarness.ReadJsonAsync(await harness.GetInternalAsync(
            $"/v1/internal/trackers/{imei}/validate?credentialSerial={serial}"));

    /// <summary>The DER inside a PEM, as hex — so a serial can be looked for in it.</summary>
    private static string HexOf(string pem) => Convert.ToHexString(
        Convert.FromBase64String(string.Concat(pem
            .Split('\n')
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal))
            .Select(line => line.Trim()))));
}

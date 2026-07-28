using System.Net;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary><c>POST /v1/trackers/bind</c> — T-02, US-3.1.</summary>
[Collection<ProvisioningCollection>]
public sealed class TrackerBindingTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The DoD's mint: a binding, a credential returned once, a primed cache and the
    /// <c>tracker.bound</c> D3' names as a side effect.
    /// </summary>
    [Fact]
    public async Task A_bind_mints_a_credential_primes_the_cache_and_queues_tracker_bound()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var body = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);

        Assert.Equal(imei, body.GetProperty("imei").GetString());
        Assert.Equal(vehicleId.ToString(), body.GetProperty("vehicleId").GetString());
        Assert.Equal(BindingStates.Active, body.GetProperty("state").GetString());

        var credential = body.GetProperty("credential");
        Assert.Equal(CredentialTypes.X509, credential.GetProperty("type").GetString());
        Assert.Contains("BEGIN CERTIFICATE", credential.GetProperty("clientCertPem").GetString());

        // The serial the response reports is the one the ledger recorded — a mismatch would make
        // a revocation name a credential nothing holds.
        var serial = body.GetProperty("credentialSerial").GetString();
        var certificates = await harness.CertificatesAsync(imei);
        Assert.Equal(serial, Assert.Single(certificates).Serial);
        Assert.Null(certificates[0].RevokedAt);

        // T-03's cache, primed after COMMIT.
        Assert.Equal(vehicleId.ToString(), await harness.CachedVehicleAsync(imei));

        var outbox = await harness.OutboxAsync(vehicleId);
        Assert.Equal(TrackerEventTypes.TrackerBound, Assert.Single(outbox).EventType);
    }

    /// <summary>
    /// A freshly bound tracker is the authoritative publisher for its vehicle (T-11). The driver
    /// app is put back in charge explicitly, through switch-source.
    /// </summary>
    [Fact]
    public async Task A_bind_makes_the_hardware_the_publisher()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(bearer, imei, vehicleId);

        var switched = await harness.PostAsync(
            $"/v1/trackers/{imei}/switch-source", new { source = "mobile" }, bearer);

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal("mobile", (await ProvisioningHarness.ReadJsonAsync(switched)).GetProperty("source").GetString());

        var events = await harness.OutboxAsync(vehicleId);
        Assert.Contains(events, e => e.EventType == TrackerEventTypes.SourceSwitched);
    }

    [Fact]
    public async Task A_psk_bind_returns_a_token_and_no_certificate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var body = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId, CredentialTypes.Psk);
        var credential = body.GetProperty("credential");

        Assert.Equal(CredentialTypes.Psk, credential.GetProperty("type").GetString());
        Assert.False(credential.TryGetProperty("clientCertPem", out _));

        // The adapter verifies it offline before it ever calls this service (D6' §4.2).
        Assert.True(harness.Authority.TryReadPsk(
            credential.GetProperty("pskToken").GetString(), imei, DateTimeOffset.UtcNow, out var serial));
        Assert.Equal(body.GetProperty("credentialSerial").GetString(), serial);
    }

    /// <summary>
    /// R-14: a retry under the same key replays the original response, credential and all. Without
    /// it a client retrying a timed-out bind would reach the anti-clone rule and quarantine the
    /// binding its own first attempt had just created.
    /// </summary>
    [Fact]
    public async Task A_replayed_bind_returns_the_same_credential_and_mints_nothing_new()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();
        var key = Guid.NewGuid().ToString();

        var body = new { imei, vehicleId = vehicleId.ToString(), method = "manual", credentialType = "x509" };

        var first = await harness.PostAsync("/v1/trackers/bind", body, bearer, key);
        var second = await harness.PostAsync("/v1/trackers/bind", body, bearer, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        Assert.Single(await harness.CertificatesAsync(imei));
        Assert.Single(await harness.BindingsAsync(imei));
    }

    [Fact]
    public async Task Reading_a_tracker_reports_the_binding_without_the_credential_material()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(bearer, imei, vehicleId);

        var response = await harness.GetAsync($"/v1/trackers/{imei}", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ProvisioningHarness.ReadJsonAsync(response);
        var binding = body.GetProperty("binding");

        Assert.Equal(imei, binding.GetProperty("imei").GetString());

        // The secret half existed once and was not kept — prov.device_certs stores a hash.
        Assert.False(binding.GetProperty("credential").TryGetProperty("clientCertPem", out _));
    }

    [Fact]
    public async Task A_vehicle_that_does_not_exist_is_404_vehicle_not_found()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();

        var response = await harness.PostAsync(
            "/v1/trackers/bind",
            new
            {
                imei = ProvisioningHarness.NextImei(),
                vehicleId = Guid.NewGuid().ToString(),
                method = "manual",
                credentialType = "x509",
            },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.NotFound, "vehicle-not-found");
    }

    /// <summary>Ownership comes from the token's <c>sub</c>, never from the body.</summary>
    [Fact]
    public async Task Another_drivers_vehicle_is_403_not_owner()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var ownerId = await harness.CreateUserAsync();
        var strangerId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(ownerId);

        var response = await harness.PostAsync(
            "/v1/trackers/bind",
            new
            {
                imei = ProvisioningHarness.NextImei(),
                vehicleId = vehicleId.ToString(),
                method = "manual",
                credentialType = "x509",
            },
            harness.Tokens.Driver(strangerId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "not-owner");
    }

    /// <summary>A fleet's trackers are provisioned by the fleet, whose operator owns no vehicle (AL-03).</summary>
    [Fact]
    public async Task A_fleet_owner_may_bind_a_tracker_to_a_vehicle_on_their_roster()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var fleetOwnerId = await harness.CreateUserAsync("fleet_owner");
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var fleetId = await harness.CreateFleetAsync(fleetOwnerId);
        await harness.AddToFleetAsync(fleetId, vehicleId);

        var imei = ProvisioningHarness.NextImei();

        var body = await harness.BindAsync(
            harness.Tokens.Issue(fleetOwnerId, [MageRideRoles.FleetOwner], MageRideApps.Fleet), imei, vehicleId);

        Assert.Equal(BindingStates.Active, body.GetProperty("state").GetString());

        // T-11 scopes tracker positions by fleet, so the binding has to carry the fleet the
        // vehicle is rostered to.
        await using var connection = await harness.OpenAsync();
        var recordedFleet = await Dapper.SqlMapper.QuerySingleAsync<Guid?>(
            connection, "SELECT fleet_id FROM prov.tracker_bindings WHERE imei = @Imei;", new { Imei = imei });

        Assert.Equal(fleetId, recordedFleet);
    }

    [Theory]
    [InlineData("12345", "x509", "manual")]
    [InlineData("359586015829435", "rsa", "manual")]
    [InlineData("359586015829435", "x509", "telepathy")]
    public async Task A_malformed_body_is_400_validation_failed(string imei, string credentialType, string method)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var response = await harness.PostAsync(
            "/v1/trackers/bind",
            new { imei, vehicleId = vehicleId.ToString(), method, credentialType },
            harness.Tokens.Driver(driverId));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    /// <summary>
    /// D3': "bindCode: required when method=admin_code". A request claiming an admin-code bind
    /// without one is refused, so the stronger method cannot be spelled as the weaker one.
    /// </summary>
    [Fact]
    public async Task An_admin_code_bind_without_a_code_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var response = await harness.PostAsync(
            "/v1/trackers/bind",
            new
            {
                imei = ProvisioningHarness.NextImei(),
                vehicleId = vehicleId.ToString(),
                method = "admin_code",
                credentialType = "x509",
            },
            harness.Tokens.Driver(driverId));

        var problem = await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Contains("bindCode", problem.Root.GetProperty("errors").ToString(), StringComparison.Ordinal);
    }

    /// <summary>Opening the Driver App does not grant the driver role (C020 decision 4).</summary>
    [Fact]
    public async Task A_passenger_is_refused_the_whole_surface()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var passengerId = await harness.CreateUserAsync("passenger");

        var response = await harness.PostAsync(
            "/v1/trackers/bind",
            new
            {
                imei = ProvisioningHarness.NextImei(),
                vehicleId = Guid.NewGuid().ToString(),
                method = "manual",
                credentialType = "x509",
            },
            harness.Tokens.PassengerOnDriverApp(passengerId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_401()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var response = await harness.GetAsync($"/v1/trackers/{ProvisioningHarness.NextImei()}", bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

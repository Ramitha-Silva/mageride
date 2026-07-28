using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Provisioning.Trackers;
using MageRide.TestKit;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary>T-02 / US-3.5: the 90-day rotation, and the overlap that keeps it from bricking devices.</summary>
[Collection<ProvisioningCollection>]
public sealed class RotationTests(PostgresFixture postgres, RedisFixture redis)
{
    [Fact]
    public async Task The_sweep_rotates_a_credential_that_has_reached_its_renewal_window()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);
        var outgoing = bound.GetProperty("credentialSerial").GetString()!;

        await harness.MakeRotationDueAsync(imei);

        Assert.Equal(1, await harness.SweepRotationAsync());

        var certificates = await harness.CertificatesAsync(imei);
        Assert.Equal(2, certificates.Count);

        // The replacement is minted; the outgoing one is untouched. A sweep that revoked as it
        // rotated would take every tracker out of coverage off the air — the population least able
        // to come back and collect a new credential.
        Assert.All(certificates, certificate => Assert.Null(certificate.RevokedAt));

        var events = await harness.OutboxAsync(vehicleId);
        var rotated = Assert.Single(events, e => e.EventType == TrackerEventTypes.CredentialRotated);

        Assert.Contains(outgoing, rotated.Payload, StringComparison.Ordinal);

        // No credential material on the topic. The secret half goes to the caller of the internal
        // rotate route, once, and nowhere else.
        Assert.DoesNotContain("BEGIN", rotated.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("mrp1.", rotated.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overlap is the point: a tracker that has been parked out of GSM coverage for a
    /// fortnight still authenticates with the credential it left with.
    /// </summary>
    [Fact]
    public async Task The_outgoing_credential_keeps_working_after_a_rotation()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);
        var outgoing = bound.GetProperty("credentialSerial").GetString()!;

        await harness.MakeRotationDueAsync(imei);
        await harness.SweepRotationAsync();

        var verdict = await ProvisioningHarness.ReadJsonAsync(await harness.GetInternalAsync(
            $"/v1/internal/trackers/{imei}/validate?credentialSerial={outgoing}"));

        Assert.True(verdict.GetProperty("valid").GetBoolean());
    }

    /// <summary>A sweep with nothing due does nothing, so an hourly ticker costs one indexed query.</summary>
    [Fact]
    public async Task A_credential_outside_its_renewal_window_is_left_alone()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);

        Assert.Equal(0, await harness.SweepRotationAsync());
        Assert.Single(await harness.CertificatesAsync(imei));
    }

    /// <summary>
    /// A revoked or quarantined binding is not rotated. Handing a working credential to a device
    /// the platform has already decided against would undo the decision.
    /// </summary>
    [Fact]
    public async Task A_revoked_binding_is_not_rotated()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(bearer, imei, vehicleId);
        await harness.MakeRotationDueAsync(imei);
        await harness.PostAsync("/v1/trackers/unbind", new { imei }, bearer);

        Assert.Equal(0, await harness.SweepRotationAsync());
        Assert.Single(await harness.CertificatesAsync(imei));
    }

    /// <summary>
    /// The manual route and the cron run the same code, so an operator's rotation and a swept one
    /// cannot leave the ledger in different shapes.
    /// </summary>
    [Fact]
    public async Task The_internal_route_returns_the_new_credential_material_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId, CredentialTypes.Psk);
        var outgoing = bound.GetProperty("credentialSerial").GetString()!;

        var response = await harness.PostInternalAsync($"/v1/internal/trackers/{imei}/rotate");
        var credential = await ProvisioningHarness.ReadJsonAsync(response);

        Assert.NotEqual(outgoing, credential.GetProperty("credentialSerial").GetString());
        Assert.True(harness.Authority.TryReadPsk(
            credential.GetProperty("pskToken").GetString(), imei, DateTimeOffset.UtcNow, out _));

        // Reading the tracker back never returns material again — prov.device_certs holds a hash.
        var read = await ProvisioningHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/trackers/{imei}", harness.Tokens.Driver(driverId)));

        Assert.False(read.GetProperty("binding").GetProperty("credential").TryGetProperty("pskToken", out _));
    }

    [Fact]
    public async Task Rotating_an_imei_nobody_bound_is_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var response = await harness.PostInternalAsync(
            $"/v1/internal/trackers/{ProvisioningHarness.NextImei()}/rotate");

        await ProblemDocument.AssertAsync(response, System.Net.HttpStatusCode.NotFound, "not-found");
    }
}

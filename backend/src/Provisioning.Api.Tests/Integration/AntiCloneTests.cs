using System.Net;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Endpoints;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Provisioning.Trackers;
using MageRide.TestKit;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary>
/// T-08 / US-3.4: two devices presenting one IMEI inside 24 h put <b>both</b> bindings into
/// QUARANTINED and raise an admin alert.
/// </summary>
[Collection<ProvisioningCollection>]
public sealed class AntiCloneTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>The DoD assertion.</summary>
    [Fact]
    public async Task A_duplicate_imei_quarantines_both_bindings_and_emits_an_admin_alert()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var firstDriver = await harness.CreateUserAsync();
        var secondDriver = await harness.CreateUserAsync();
        var firstVehicle = await harness.CreateVehicleAsync(firstDriver);
        var secondVehicle = await harness.CreateVehicleAsync(secondDriver);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(firstDriver), imei, firstVehicle);

        var clone = await harness.PostAsync(
            "/v1/trackers/bind",
            new { imei, vehicleId = secondVehicle.ToString(), method = "manual", credentialType = "x509" },
            harness.Tokens.Driver(secondDriver));

        await ProblemDocument.AssertAsync(clone, HttpStatusCode.Conflict, "imei-duplicate");

        // Both records are held. The incumbent kept publishing until this moment, so leaving it
        // ACTIVE would be the clone winning by arriving second.
        var bindings = await harness.BindingsAsync(imei);
        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.Equal(BindingStates.Quarantined, binding.State));
        Assert.All(bindings, binding => Assert.Equal(BindingStateReasons.ImeiDuplicate, binding.Reason));

        // Held rather than destroyed: certificate_hold is the one RFC 5280 reason a CA may lift,
        // which is what US-3.4's admin resolution does for whichever device is genuine.
        var certificates = await harness.CertificatesAsync(imei);
        Assert.Equal(2, certificates.Count);
        Assert.All(certificates, certificate => Assert.NotNull(certificate.RevokedAt));
        Assert.All(certificates, certificate =>
            Assert.Equal(RevocationReasons.CertificateHold, certificate.Reason));

        // The cache entry is gone, so the adapter stops resolving the IMEI at once (T-12).
        Assert.Null(await harness.CachedVehicleAsync(imei));

        // The alert names both holders — the operator's question is which of the two is real, and
        // one id cannot answer it.
        var alert = Assert.Single(
            await harness.OutboxAsync(firstVehicle),
            e => e.EventType == TrackerEventTypes.TrackerQuarantined);

        Assert.Contains(firstVehicle.ToString(), alert.Payload, StringComparison.Ordinal);
        Assert.Contains(secondVehicle.ToString(), alert.Payload, StringComparison.Ordinal);
    }

    /// <summary>Neither device may publish afterwards — that is what "both held" has to mean.</summary>
    [Fact]
    public async Task Neither_quarantined_device_validates()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var firstDriver = await harness.CreateUserAsync();
        var secondDriver = await harness.CreateUserAsync();
        var firstVehicle = await harness.CreateVehicleAsync(firstDriver);
        var secondVehicle = await harness.CreateVehicleAsync(secondDriver);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(firstDriver), imei, firstVehicle);

        await harness.PostAsync(
            "/v1/trackers/bind",
            new { imei, vehicleId = secondVehicle.ToString(), method = "manual", credentialType = "x509" },
            harness.Tokens.Driver(secondDriver));

        var verdict = await ProvisioningHarness.ReadJsonAsync(
            await harness.GetInternalAsync($"/v1/internal/trackers/{imei}/validate"));

        Assert.False(verdict.GetProperty("valid").GetBoolean());
        Assert.Equal(BindingStates.Quarantined, verdict.GetProperty("state").GetString());
    }

    /// <summary>
    /// Outside the window the incumbent is stale, not cloned. An operator moving a tracker to
    /// another vehicle a week later has duplicated nothing, and holding both would make them wait
    /// for an admin to undo a legitimate re-provision.
    /// </summary>
    [Fact]
    public async Task A_rebind_after_the_window_supersedes_the_old_binding_instead()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var oldVehicle = await harness.CreateVehicleAsync(driverId);
        var newVehicle = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(bearer, imei, oldVehicle);
        await harness.AgeBindingAsync(imei, TimeSpan.FromHours(25));

        var body = await harness.BindAsync(bearer, imei, newVehicle);

        Assert.Equal(newVehicle.ToString(), body.GetProperty("vehicleId").GetString());

        var bindings = await harness.BindingsAsync(imei);
        Assert.Equal(2, bindings.Count);
        Assert.Equal(BindingStates.Active, bindings[0].State);
        Assert.Equal(newVehicle, bindings[0].VehicleId);
        Assert.Equal(BindingStates.Revoked, bindings[1].State);
        Assert.Equal(BindingStateReasons.Superseded, bindings[1].Reason);

        // The cache now points at the new vehicle, so the adapter routes the device's positions to
        // the right one on its next connect (T-03).
        Assert.Equal(newVehicle.ToString(), await harness.CachedVehicleAsync(imei));
    }

    /// <summary>
    /// The other half of T-08, reported from where a clone is actually visible.
    /// </summary>
    /// <remarks>
    /// A clone copies the certificate, so at the adapter both devices present the <i>same</i>
    /// serial — what tells them apart is two live sockets holding one identity, which is the
    /// adapter's state. It reports; this service adjudicates and holds the binding.
    /// </remarks>
    [Fact]
    public async Task An_adapters_clone_report_quarantines_the_binding_and_alerts()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);

        var reported = await harness.PostInternalAsync(
            $"/v1/internal/trackers/{imei}/quarantine",
            new { reportedBy = "adapter-gt06", detail = "two sockets, 203.0.113.7 and 198.51.100.2" });

        Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);

        Assert.Equal(BindingStates.Quarantined, Assert.Single(await harness.BindingsAsync(imei)).State);
        Assert.Null(await harness.CachedVehicleAsync(imei));

        var verdict = await ProvisioningHarness.ReadJsonAsync(
            await harness.GetInternalAsync($"/v1/internal/trackers/{imei}/validate"));
        Assert.False(verdict.GetProperty("valid").GetBoolean());

        var alert = Assert.Single(
            await harness.OutboxAsync(vehicleId), e => e.EventType == TrackerEventTypes.TrackerQuarantined);
        Assert.Contains("adapter-gt06", alert.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A second report is not a second incident. An adapter re-reports on every reconnect, and the
    /// first report already took the device off the air.
    /// </summary>
    [Fact]
    public async Task A_repeated_clone_report_is_a_no_op()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);

        for (var i = 0; i < 3; i++)
        {
            var response = await harness.PostInternalAsync(
                $"/v1/internal/trackers/{imei}/quarantine", new { reportedBy = "adapter-gt06" });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Single(
            await harness.OutboxAsync(vehicleId), e => e.EventType == TrackerEventTypes.TrackerQuarantined);
    }

    /// <summary>
    /// <b>Rotation is not a clone.</b> The overlap window leaves two presentable serials on one
    /// binding by design, and a rule that read "this IMEI showed two serials" would quarantine
    /// every device the 90-day cron renews.
    /// </summary>
    [Fact]
    public async Task Both_serials_validate_across_a_rotation_overlap()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        var bound = await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);
        var outgoing = bound.GetProperty("credentialSerial").GetString()!;

        var rotated = (await ProvisioningHarness.ReadJsonAsync(
            await harness.PostInternalAsync($"/v1/internal/trackers/{imei}/rotate")))
            .GetProperty("credentialSerial").GetString()!;

        Assert.NotEqual(outgoing, rotated);

        foreach (var serial in new[] { outgoing, rotated })
        {
            var verdict = await ProvisioningHarness.ReadJsonAsync(await harness.GetInternalAsync(
                $"/v1/internal/trackers/{imei}/validate?{InternalTrackerEndpoints.CredentialSerialQuery}={serial}"));

            Assert.True(verdict.GetProperty("valid").GetBoolean(), $"serial {serial} should still authenticate");
        }

        Assert.Equal(BindingStates.Active, Assert.Single(await harness.BindingsAsync(imei)).State);
    }

    /// <summary>A serial this binding never held does not authenticate, clone rule or not.</summary>
    [Fact]
    public async Task A_serial_the_binding_never_held_does_not_validate()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);

        var verdict = await ProvisioningHarness.ReadJsonAsync(await harness.GetInternalAsync(
            $"/v1/internal/trackers/{imei}/validate?{InternalTrackerEndpoints.CredentialSerialQuery}=DEADBEEF01"));

        Assert.False(verdict.GetProperty("valid").GetBoolean());
    }

    /// <summary>An adapter that reports no serial resolves normally and quarantines nothing.</summary>
    [Fact]
    public async Task Repeated_validation_without_a_serial_never_quarantines()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);
        var imei = ProvisioningHarness.NextImei();

        await harness.BindAsync(harness.Tokens.Driver(driverId), imei, vehicleId);

        for (var i = 0; i < 5; i++)
        {
            var verdict = await ProvisioningHarness.ReadJsonAsync(
                await harness.GetInternalAsync($"/v1/internal/trackers/{imei}/validate"));

            Assert.True(verdict.GetProperty("valid").GetBoolean());
        }

        Assert.Equal(BindingStates.Active, Assert.Single(await harness.BindingsAsync(imei)).State);
    }
}

using System.Net;
using System.Text.Json;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// E-03's document-expiry tracker: the T−30 d / T−7 d / T−1 d reminders, the expiry that suspends
/// dispatch, and the renewal that lifts it. DoD item 4 lives here.
/// </summary>
[Collection<PostgresCollection>]
public sealed class DocumentExpiryTests(PostgresFixture postgres)
{
    /// <summary>DoD item 4: an expired insurance suspends dispatch for that driver.</summary>
    [Fact]
    public async Task An_expired_insurance_suspends_dispatch_for_the_driver()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var (driverId, bearer, vehicleId) = await ApprovedVehicleAsync(harness);

        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(1, await harness.SweepDocumentExpiryAsync());

        Assert.Equal("DISPATCH_SUSPENDED", await harness.DispatchStateAsync(vehicleId));

        // The registration is still APPROVED — the vehicle passed its checks, its cover lapsed —
        // and the go-live gate is what refuses it (D5' §3.2, the eligibility projection).
        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        var summary = mine.GetProperty("items")[0];

        Assert.Equal("APPROVED", summary.GetProperty("status").GetString());
        Assert.False(summary.GetProperty("isGoLiveEligible").GetBoolean());

        var refused = await harness.PostAsync($"/v1/vehicles/{vehicleId}/select-live", null, bearer);
        await ProblemDocument.AssertAsync(refused, HttpStatusCode.Forbidden, "vehicle-not-approved");

        var expired = Assert.Single(await harness.OutboxAsync(vehicleId), e => e.EventType == "document.expired");

        using var payload = JsonDocument.Parse(expired.Payload);
        Assert.Equal("insurance", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal(driverId.ToString(), payload.RootElement.GetProperty("driverId").GetString());
        Assert.Equal("DISPATCH_SUSPENDED", payload.RootElement.GetProperty("dispatchState").GetString());

        Assert.Contains(
            await harness.DocumentsAsync(vehicleId),
            document => document.Kind == "insurance" && document.Status == "EXPIRED");
    }

    [Fact]
    public async Task An_expired_revenue_licence_suspends_dispatch_too()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var (_, _, vehicleId) = await ApprovedVehicleAsync(harness);

        // AL-10 makes the revenue licence as mandatory as the insurance, so its expiry is as
        // disqualifying (ADD §1 AL-10, "expiry → auto-suspend (E-03)").
        await harness.SetDocumentExpiryAsync(vehicleId, "revenue_license", DateTimeOffset.UtcNow.AddDays(-2));

        Assert.Equal(1, await harness.SweepDocumentExpiryAsync());
        Assert.Equal("DISPATCH_SUSPENDED", await harness.DispatchStateAsync(vehicleId));
    }

    /// <summary>DoD item 4's second half: "until re-approved".</summary>
    [Fact]
    public async Task Renewing_the_certificate_brings_the_vehicle_back()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var (driverId, bearer, vehicleId) = await ApprovedVehicleAsync(harness);

        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(-1));
        await harness.SweepDocumentExpiryAsync();

        Assert.Equal("DISPATCH_SUSPENDED", await harness.DispatchStateAsync(vehicleId));

        var renewed = await harness.SaveStepAsync(driverId, bearer, vehicleId.ToString(), "insurance");
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        Assert.Equal("ACTIVE", await harness.DispatchStateAsync(vehicleId));

        // The lapsed row stays EXPIRED in the audit trail; the renewal supersedes it, which is
        // what the "current document per kind" read is for (migration 0312).
        var documents = await harness.DocumentsAsync(vehicleId);
        Assert.Equal(2, documents.Count(document => document.Kind == "insurance"));
        Assert.Contains(documents, document => document.Kind == "insurance" && document.Status == "EXPIRED");

        Assert.Single(await harness.OutboxAsync(vehicleId), e => e.EventType == "vehicle.dispatch_resumed");

        var mine = await RegistryHarness.ReadJsonAsync(await harness.GetAsync("/v1/vehicles/mine", bearer));
        Assert.True(mine.GetProperty("items")[0].GetProperty("isGoLiveEligible").GetBoolean());
    }

    /// <summary>
    /// A renewal filed early must not be undone by its predecessor lapsing on schedule.
    /// </summary>
    [Fact]
    public async Task A_superseded_certificate_expiring_does_not_suspend_anything()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var (driverId, bearer, vehicleId) = await ApprovedVehicleAsync(harness);

        // The driver renews a fortnight before the old certificate runs out — then it runs out.
        Assert.Equal(
            HttpStatusCode.OK,
            (await harness.SaveStepAsync(driverId, bearer, vehicleId.ToString(), "insurance")).StatusCode);

        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                """
                UPDATE registry.documents SET expires_at = now() - interval '1 day'
                 WHERE id = (SELECT id FROM registry.documents
                              WHERE vehicle_id = @Id AND kind = 'insurance'
                              ORDER BY created_at ASC, id ASC LIMIT 1);
                """,
                new { Id = vehicleId });
        }

        Assert.Equal(0, await harness.SweepDocumentExpiryAsync());
        Assert.Equal("ACTIVE", await harness.DispatchStateAsync(vehicleId));
    }

    /// <summary>
    /// E-03's three reminders, each emitted once. The notice ledger is what makes a nightly job
    /// nightly rather than a nightly nuisance (migration 0312).
    /// </summary>
    [Fact]
    public async Task Each_reminder_threshold_fires_exactly_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var (_, _, vehicleId) = await ApprovedVehicleAsync(harness);

        // Revenue is left far out so exactly one document is inside a window at a time.
        await harness.SetDocumentExpiryAsync(vehicleId, "revenue_license", DateTimeOffset.UtcNow.AddYears(2));

        var thresholds = new (int InDays, int Expected)[] { (20, 30), (5, 7), (1, 1) };

        foreach (var (inDays, expected) in thresholds)
        {
            await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(inDays));

            Assert.Equal(1, await harness.SweepDocumentExpiryAsync());

            // Tonight's job runs again tomorrow morning; nothing has changed, so nothing goes out.
            Assert.Equal(0, await harness.SweepDocumentExpiryAsync());

            var reminders = (await harness.OutboxAsync(vehicleId))
                .Where(e => e.EventType == "document.expiring")
                .Select(e => JsonDocument.Parse(e.Payload).RootElement.GetProperty("daysRemaining").GetInt32())
                .ToArray();

            Assert.Contains(expected, reminders);

            // Still ACTIVE: a warning is not a suspension.
            Assert.Equal("ACTIVE", await harness.DispatchStateAsync(vehicleId));
        }

        var emitted = (await harness.OutboxAsync(vehicleId))
            .Where(e => e.EventType == "document.expiring")
            .Select(e => JsonDocument.Parse(e.Payload).RootElement.GetProperty("daysRemaining").GetInt32())
            .Order()
            .ToArray();

        Assert.Equal(new[] { 1, 7, 30 }, emitted);
    }

    /// <summary>
    /// A job that was down for a fortnight must not send three pushes about one certificate.
    /// </summary>
    [Fact]
    public async Task A_sweep_that_crosses_several_thresholds_at_once_sends_only_the_tightest()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var (_, _, vehicleId) = await ApprovedVehicleAsync(harness);

        await harness.SetDocumentExpiryAsync(vehicleId, "revenue_license", DateTimeOffset.UtcNow.AddYears(2));
        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddHours(12));

        Assert.Equal(1, await harness.SweepDocumentExpiryAsync());

        var reminders = (await harness.OutboxAsync(vehicleId))
            .Where(e => e.EventType == "document.expiring")
            .ToArray();

        // 30, 7 and 1 were all crossed; only "one day left" is worth a driver's attention, and the
        // other two are recorded as moot so they never arrive late.
        var reminder = Assert.Single(reminders);
        using var payload = JsonDocument.Parse(reminder.Payload);
        Assert.Equal(1, payload.RootElement.GetProperty("daysRemaining").GetInt32());
    }

    /// <summary>
    /// A driving licence has no vehicle, and E-03 says expiry "flips driver to
    /// DISPATCH_SUSPENDED" — so it suspends every vehicle that driver owns.
    /// </summary>
    [Fact]
    public async Task An_expired_driving_licence_suspends_every_vehicle_the_driver_owns()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        await harness.CompleteProfileSetupAsync(driverId, bearer);

        var first = await OnboardedVehicleAsync(harness, driverId, bearer);
        var second = await OnboardedVehicleAsync(harness, driverId, bearer);

        await using (var connection = await harness.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                connection,
                """
                UPDATE registry.documents SET expires_at = now() - interval '1 day'
                 WHERE driver_id = @DriverId AND vehicle_id IS NULL AND kind = 'driving_license';
                """,
                new { DriverId = driverId });
        }

        // Two documents (front and back) both lapse, and both suspend both vehicles.
        Assert.Equal(2, await harness.SweepDocumentExpiryAsync());

        Assert.Equal("DISPATCH_SUSPENDED", await harness.DispatchStateAsync(first));
        Assert.Equal("DISPATCH_SUSPENDED", await harness.DispatchStateAsync(second));
    }

    /// <summary>An approved vehicle whose four steps verified, with real documents on file.</summary>
    private static async Task<(Guid DriverId, string Bearer, Guid VehicleId)> ApprovedVehicleAsync(
        RegistryHarness harness)
    {
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await OnboardedVehicleAsync(harness, driverId, bearer);

        return (driverId, bearer, vehicleId);
    }

    private static async Task<Guid> OnboardedVehicleAsync(RegistryHarness harness, Guid driverId, string bearer)
    {
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        var last = await harness.CompleteOnboardingAsync(driverId, bearer, vehicleId);
        Assert.Equal("APPROVED", last.GetProperty("status").GetString());

        return Guid.Parse(vehicleId);
    }
}

using MageRide.Registry.Observability;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// ADD §13.3.1 row 8 as a gauge: a lapsed document on a vehicle E-03 has not suspended (Δ C119).
/// </summary>
/// <remarks>
/// <para>
/// The alert pages, and its subject is a passenger in a vehicle whose insurance, permit or licence
/// the platform should already have taken off the road. So what has to be true is that the gauge
/// counts exactly the vehicles E-03 has missed — not every lapsed certificate, and not every
/// suspended vehicle.
/// </para>
/// <para>
/// The pivotal case is <see cref="A_lapsed_document_the_worker_has_acted_on_stops_being_counted"/>:
/// it is the difference between "a document expired" (routine, happens daily) and "the doc-expiry
/// job is not running" (the row's stated cause), and every other case here is a variation on it.
/// </para>
/// <para>
/// <b>Every assertion is a delta, and every case cleans up after itself.</b> The gauge is a
/// platform-wide count and this collection shares one Postgres across every registry suite without
/// resetting it — so an absolute number would depend on which files the runner had already got
/// through, and a lapsed document left behind would change the sweep count
/// <see cref="DocumentExpiryTests"/> asserts. Each case that lapses a document ends by sweeping,
/// which is the state the worker would have left the platform in anyway.
/// </para>
/// </remarks>
[Collection<PostgresCollection>]
public sealed class ExpiredDocumentsGaugeTests(PostgresFixture postgres)
{
    private static Task<int> CountAsync(RegistryHarness harness) =>
        ExpiredDocumentsGauge.CountAsync(harness.Services, CancellationToken.None);

    [Fact]
    public async Task A_lapsed_document_on_a_dispatchable_vehicle_is_counted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var before = await CountAsync(harness);
        var vehicleId = await ApprovedVehicleAsync(harness);
        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(-1));

        // Deliberately no sweep: this is the state the platform is in when the worker is dead.
        Assert.Equal("ACTIVE", await harness.DispatchStateAsync(vehicleId));
        Assert.Equal(before + 1, await CountAsync(harness));

        await harness.SweepDocumentExpiryAsync();
    }

    /// <summary>
    /// The worker running is what clears the alert, and it is the only thing that should. Nothing
    /// about the document changed — it is still lapsed — but the vehicle is off the road.
    /// </summary>
    [Fact]
    public async Task A_lapsed_document_the_worker_has_acted_on_stops_being_counted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var before = await CountAsync(harness);
        var vehicleId = await ApprovedVehicleAsync(harness);
        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(before + 1, await CountAsync(harness));

        await harness.SweepDocumentExpiryAsync();

        Assert.Equal("DISPATCH_SUSPENDED", await harness.DispatchStateAsync(vehicleId));
        Assert.Equal(before, await CountAsync(harness));
    }

    /// <summary>A certificate with time left on it is E-03 having nothing to do.</summary>
    [Fact]
    public async Task A_document_that_has_not_yet_lapsed_is_not_counted()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var before = await CountAsync(harness);
        var vehicleId = await ApprovedVehicleAsync(harness);
        // Far enough out that it is not inside E-03's T-30 d notice window either, so the sweep
        // count DocumentExpiryTests asserts is untouched.
        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(400));

        Assert.Equal(before, await CountAsync(harness));
    }

    /// <summary>
    /// Three lapsed certificates on one car is one car to take off the road, not three alerts. The
    /// number an operator is paged with has to be the number of things they must act on.
    /// </summary>
    [Fact]
    public async Task Several_lapsed_documents_on_one_vehicle_count_once()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var before = await CountAsync(harness);
        var vehicleId = await ApprovedVehicleAsync(harness);

        await harness.SetDocumentExpiryAsync(vehicleId, "insurance", DateTimeOffset.UtcNow.AddDays(-3));
        await harness.SetDocumentExpiryAsync(vehicleId, "registration", DateTimeOffset.UtcNow.AddDays(-2));
        await harness.SetDocumentExpiryAsync(vehicleId, "revenue_license", DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(before + 1, await CountAsync(harness));

        await harness.SweepDocumentExpiryAsync();
    }

    /// <summary>A vehicle with everything in order moves the gauge not at all.</summary>
    [Fact]
    public async Task A_vehicle_with_current_documents_does_not_move_the_gauge()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var before = await CountAsync(harness);
        await ApprovedVehicleAsync(harness);

        Assert.Equal(before, await CountAsync(harness));
    }

    private static async Task<Guid> ApprovedVehicleAsync(RegistryHarness harness)
    {
        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);

        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;
        var last = await harness.CompleteOnboardingAsync(driverId, bearer, vehicleId);

        Assert.Equal("APPROVED", last.GetProperty("status").GetString());

        return Guid.Parse(vehicleId);
    }
}

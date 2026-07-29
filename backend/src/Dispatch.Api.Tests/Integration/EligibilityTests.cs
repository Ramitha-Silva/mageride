using System.Text.Json;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// D5' §3.2's hard eligibility gates, run BEFORE scoring — the block state (D-04), the vehicle
/// category, the passenger's own block list (US-12.10), the E-03 document suspension and P-11's
/// package-size compatibility.
/// </summary>
/// <remarks>
/// The wallet/daily-fee gate has a suite of its own (<see cref="WalletGateTests"/>) because it is
/// this component's Definition of Done rather than one gate among several.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class EligibilityTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);
    private static readonly GeoPoint AlsoNear = new(6.9360, 79.8430);

    /// <summary>
    /// D-04: a DELISTED driver is excluded — and the round trip is a real gRPC call to a real
    /// reputation-svc, because that is the part that fails open when it is misconfigured.
    /// </summary>
    [Theory]
    [InlineData("DELISTED")]
    [InlineData("BOOKING_DISABLED")]
    public async Task A_driver_reputation_svc_has_excluded_is_never_offered_a_ride(string state)
    {
        await using var harness = await StartAsync();

        var blocked = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.SetBlockStateAsync(blocked.DriverId, state);

        var outcome = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);

        // They came through the H3 pre-filter and the exact post-filter — the gate is what stopped
        // them, and it is the gate this assertion is about.
        Assert.Equal(1, outcome.PreFilterCount);
        Assert.Equal(1, outcome.CandidateCount);
        Assert.Equal(0, outcome.EligibleCount);
    }

    /// <summary>
    /// R-11's audit is asked "why was this driver not offered the ride" more often than the
    /// converse, so an excluded candidate gets a row with the gate's name on it.
    /// </summary>
    [Fact]
    public async Task An_excluded_candidate_is_still_written_to_the_audit_with_the_gate_that_stopped_them()
    {
        await using var harness = await StartAsync();

        var blocked = await harness.CreateOnlineDriverAsync(Nearest);
        var clear = await harness.CreateOnlineDriverAsync(AlsoNear);

        await harness.SetBlockStateAsync(blocked.DriverId, "DELISTED");

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(clear.DriverId, outcome.DriverId);

        var scores = await harness.ReadScoresAsync(rideId);
        Assert.Equal(2, scores.Count);

        var excluded = scores.Single(row => row.DriverId == blocked.DriverId);
        using var breakdown = JsonDocument.Parse(excluded.Breakdown);

        Assert.Equal(EligibilityGates.BlockState, breakdown.RootElement.GetProperty("rejectedBy").GetString());

        // The spelling reputation.block_states.state uses, not protobuf's generated identifier —
        // the audit is joined against those tables by hand.
        Assert.Equal("DELISTED", breakdown.RootElement.GetProperty("blockState").GetString());

        // -1, not "last": they were never in the running, which is a different fact from ranking
        // behind the winner.
        Assert.Equal(-1, breakdown.RootElement.GetProperty("rank").GetInt32());

        var offered = scores.Single(row => row.DriverId == clear.DriverId);
        using var winner = JsonDocument.Parse(offered.Breakdown);

        // Absent rather than null — MageRideJson.StorageOptions omits nulls, as it does for every
        // other stored envelope on the platform.
        Assert.False(winner.RootElement.TryGetProperty("rejectedBy", out _));
        Assert.Equal(0, winner.RootElement.GetProperty("rank").GetInt32());
    }

    /// <summary>
    /// US-12.10: a passenger who blocked a driver never sees them again. Applied in SQL beside the
    /// <c>ST_DWithin</c>, so the driver is not a candidate at all rather than a scored exclusion.
    /// </summary>
    [Fact]
    public async Task A_driver_this_passenger_has_blocked_is_not_a_candidate()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var passenger = await harness.CreatePassengerAsync();

        await harness.BlockDriverAsync(passenger, driver.DriverId);

        var rideId = await harness.RequestRideAsync(passenger);
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(1, outcome.PreFilterCount);
        Assert.Equal(0, outcome.CandidateCount);

        // The block is one-directional and per pair: another passenger still gets this driver.
        var otherRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var other = await OfferLoopTests.DispatchAsync(harness, otherRideId);

        Assert.Equal(DispatchResult.Offered, other.Result);
        Assert.Equal(driver.DriverId, other.DriverId);
    }

    /// <summary>
    /// E-03: a driver already on standby when their documents lapse stops receiving offers, without
    /// being asked to go offline first. registry-svc flips <c>dispatch_state</c>; dispatch reads it
    /// on every candidate build, which is what makes the suspension take effect immediately.
    /// </summary>
    [Fact]
    public async Task A_vehicle_registry_has_suspended_for_lapsed_documents_stops_being_a_candidate()
    {
        await using var harness = await StartAsync();

        // Two identical drivers a few metres apart, so the only difference between them is the
        // suspension — otherwise the assertion could pass for a driver who was never a candidate.
        var suspended = await harness.CreateOnlineDriverAsync(Nearest);
        var healthy = await harness.CreateOnlineDriverAsync(AlsoNear);

        await harness.SuspendVehicleAsync(suspended.VehicleId);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        // The suspended driver is the nearer of the two and would otherwise have won.
        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(healthy.DriverId, outcome.DriverId);

        // Both are in the Redis index; only one survived the SQL gate, so only one was ever scored.
        Assert.Equal(2, outcome.PreFilterCount);
        Assert.Equal(1, outcome.CandidateCount);

        var row = Assert.Single(await harness.ReadScoresAsync(rideId));
        Assert.Equal(healthy.DriverId, row.DriverId);
    }

    /// <summary>
    /// P-11: an L parcel does not go on a motorbike. The gate narrows the round and the audit
    /// records the verdict in <c>candidate_scores.package_size_compatible</c>.
    /// </summary>
    [Fact]
    public async Task A_vehicle_that_cannot_carry_the_package_is_excluded_and_the_verdict_is_persisted()
    {
        await using var harness = await StartAsync();

        var rider = await harness.CreateOnlineDriverAsync(Nearest, vehicleType: "motorbike");

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync(), vehicleType: "motorbike");
        var request = await OfferLoopTests.BuildRequestAsync(harness, rideId);

        var outcome = await DispatchAsync(
            harness, request with { Kind = RideKinds.Package, PackageSize = PackageSizes.Large });

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal(1, outcome.CandidateCount);
        Assert.Equal(0, outcome.EligibleCount);

        var row = Assert.Single(await harness.ReadScoresAsync(rideId));
        Assert.Equal(rider.DriverId, row.DriverId);
        Assert.False(row.PackageSizeCompatible);

        using var breakdown = JsonDocument.Parse(row.Breakdown);
        Assert.Equal(EligibilityGates.PackageSize, breakdown.RootElement.GetProperty("rejectedBy").GetString());
    }

    /// <summary>The same motorbike takes an S parcel, and the audit says the table was consulted.</summary>
    [Fact]
    public async Task A_vehicle_that_can_carry_the_package_is_offered_it()
    {
        await using var harness = await StartAsync();

        var rider = await harness.CreateOnlineDriverAsync(Nearest, vehicleType: "motorbike");

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync(), vehicleType: "motorbike");
        var request = await OfferLoopTests.BuildRequestAsync(harness, rideId);

        var outcome = await DispatchAsync(
            harness, request with { Kind = RideKinds.Package, PackageSize = PackageSizes.Small });

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(rider.DriverId, outcome.DriverId);

        var row = Assert.Single(await harness.ReadScoresAsync(rideId));
        Assert.True(row.PackageSizeCompatible);

        // P-11: the driver still sees the size and may reject it, so it travels on the offer.
        await using var connection = await harness.OpenAsync();
        var payload = await Dapper.SqlMapper.ExecuteScalarAsync<string>(
            connection,
            "SELECT payload::text FROM dispatch.outbox WHERE aggregate_id = @RideId;",
            new { RideId = rideId });

        using var envelope = JsonDocument.Parse(payload!);
        Assert.True(envelope.RootElement.GetProperty("isPackage").GetBoolean());
        Assert.Equal(PackageSizes.Small, envelope.RootElement.GetProperty("packageSize").GetString());
    }

    /// <summary>
    /// D5' §3.2's GPS-freshness rule is <c>2×expectedInterval</c>, and both halves are
    /// configuration — so the bound the service applies is the product, not a constant.
    /// </summary>
    [Fact]
    public async Task The_freshness_bound_is_two_times_the_expected_position_interval()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            ["Dispatch:ExpectedPositionInterval"] = "00:00:05",
            ["Dispatch:PositionFreshnessFactor"] = "2",
        });

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        // 8 s old: inside 2 × 5 s.
        await harness.AgePresenceAsync(driver.DriverId, TimeSpan.FromSeconds(8));

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var fresh = await OfferLoopTests.DispatchAsync(harness, firstRideId);

        Assert.Equal(DispatchResult.Offered, fresh.Result);

        // Put the driver back in the pool properly — settling the offer as well as the presence
        // row, or ux_offers_driver_live would refuse the next round and the assertion below would
        // pass without the freshness gate doing anything.
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatchService>()
                .ReleaseLiveOfferAsync(
                    firstRideId, OfferStatuses.Declined, TestContext.Current.CancellationToken);
        }

        // 12 s old: outside it.
        await harness.AgePresenceAsync(driver.DriverId, TimeSpan.FromSeconds(12));

        var stale = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.NoCandidate, stale.Result);
        Assert.Equal(1, stale.PreFilterCount);
        Assert.Equal(0, stale.CandidateCount);
    }

    // -----------------------------------------------------------------------------------------

    private static async Task<DispatchOutcome> DispatchAsync(DispatchHarness harness, RideDispatchRequest request)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IDispatchService>()
            .BeginAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}

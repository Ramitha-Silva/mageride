using System.Text.Json;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 1 — "a scoring decision is reproducible from its persisted candidate_scores breakdown".</b>
/// D5' §3.3's versioned weighted score (R-11).
/// </summary>
[Collection<DispatchCollection>]
public sealed class ScoringTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>~70 m from the pickup.</summary>
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>~400 m north of the pickup — 0.0036° of latitude.</summary>
    private static readonly GeoPoint FourHundredM = new(6.9380, 79.8428);

    /// <summary>~4.4 km from the pickup, still inside the 5 km radius.</summary>
    private static readonly GeoPoint FourKm = new(6.9700, 79.8600);

    /// <summary>
    /// The whole of DoD 1: every number the formula used is on the row, so the score can be
    /// recomputed from the audit alone — without this service, its configuration, or the weights
    /// an admin happens to have live today.
    /// </summary>
    [Fact]
    public async Task Each_persisted_score_can_be_recomputed_from_its_own_breakdown()
    {
        await using var harness = await StartAsync();

        await harness.CreateOnlineDriverAsync(Nearest);
        await harness.CreateOnlineDriverAsync(FourKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);

        var scores = await harness.ReadScoresAsync(rideId);
        Assert.Equal(2, scores.Count);

        foreach (var row in scores)
        {
            using var document = JsonDocument.Parse(row.Breakdown);
            var breakdown = document.RootElement;

            var terms = breakdown.GetProperty("terms");
            var weights = breakdown.GetProperty("weights");

            // The three D5' §3.3 terms, multiplied by the three weights that were live when the
            // decision was taken. Nothing is read from configuration here — that is the point.
            var recomputed =
                (terms.GetProperty("distance").GetDouble() * weights.GetProperty("distance").GetDouble())
                + (terms.GetProperty("level").GetDouble() * weights.GetProperty("level").GetDouble())
                + (terms.GetProperty("category").GetDouble() * weights.GetProperty("category").GetDouble());

            Assert.Equal((double)row.Score, recomputed, 6);

            // And each term is reproducible from the raw inputs beside it, so the audit does not
            // simply restate its own arithmetic.
            var distanceM = breakdown.GetProperty("distanceM").GetDouble();
            var halfLife = breakdown.GetProperty("distanceHalfLifeM").GetInt32();

            Assert.Equal(
                1d / (1d + (distanceM / halfLife)), terms.GetProperty("distance").GetDouble(), 6);

            Assert.Equal(
                breakdown.GetProperty("driverLevel").GetInt32() / 3d, terms.GetProperty("level").GetDouble(), 6);

            // The gate verdicts that decided whether this candidate was in the running at all are
            // on the row too, so "why did this driver not get the ride" is answerable from it.
            Assert.Equal("OK", breakdown.GetProperty("blockState").GetString());
            Assert.True(breakdown.GetProperty("walletOk").GetBoolean());

            // Absent, not null: MageRideJson.StorageOptions omits nulls, so a survivor's row simply
            // carries no `rejectedBy` — the same convention every other stored envelope uses.
            Assert.False(breakdown.TryGetProperty("rejectedBy", out _));
        }

        // The version on the row is what says which formula those terms belong to.
        Assert.All(scores, row => Assert.Equal(1, row.Version));
        Assert.Equal(
            1, harness.Services.GetRequiredService<IOptions<DispatchOptions>>().Value.AlgorithmVersion);
    }

    /// <summary>
    /// US-6A.2: the Driver Level is a real term, not decoration. A Level-3 driver 400 m away beats
    /// a Level-1 driver 70 m away under the default weights, and the audit shows exactly why.
    /// </summary>
    [Fact]
    public async Task A_higher_level_driver_wins_over_a_nearer_one_when_the_level_term_covers_the_gap()
    {
        await using var harness = await StartAsync();

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        var far = await harness.CreateOnlineDriverAsync(FourHundredM);

        await harness.SetDriverLevelAsync(near.DriverId, 1);
        await harness.SetDriverLevelAsync(far.DriverId, 3);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal(far.DriverId, outcome.DriverId);

        var scores = await harness.ReadScoresAsync(rideId);
        Assert.Equal(far.DriverId, scores[0].DriverId);

        using var winner = JsonDocument.Parse(scores[0].Breakdown);
        using var loser = JsonDocument.Parse(scores[1].Breakdown);

        Assert.Equal(3, winner.RootElement.GetProperty("driverLevel").GetInt32());
        Assert.Equal(1, loser.RootElement.GetProperty("driverLevel").GetInt32());

        // …and the loser really was the nearer of the two, which is what makes this a statement
        // about the level term rather than about the distances.
        Assert.True(
            loser.RootElement.GetProperty("distanceM").GetDouble()
            < winner.RootElement.GetProperty("distanceM").GetDouble());
    }

    /// <summary>The same two drivers at the same level: proximity decides, as it should.</summary>
    [Fact]
    public async Task At_equal_level_the_nearer_driver_wins()
    {
        await using var harness = await StartAsync();

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        var far = await harness.CreateOnlineDriverAsync(FourHundredM);

        await harness.SetDriverLevelAsync(near.DriverId, 3);
        await harness.SetDriverLevelAsync(far.DriverId, 3);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(near.DriverId, outcome.DriverId);
    }

    /// <summary>
    /// Retuning the weights retunes the outcome, which is what "admin-config, versioned per
    /// <c>dispatch_algorithm_version</c>" has to mean if R-11's audit is to be worth anything.
    /// </summary>
    [Fact]
    public async Task The_weights_are_configuration_and_the_breakdown_records_the_ones_that_ran()
    {
        // Distance alone: the Level-1 driver 70 m away now wins the pairing that the level term
        // decided in the other direction.
        await using var harness = await StartAsync(new Dictionary<string, string?>
        {
            ["Dispatch:Weights:Distance"] = "1",
            ["Dispatch:Weights:Level"] = "0",
            ["Dispatch:Weights:Category"] = "0",
            ["Dispatch:AlgorithmVersion"] = "7",
        });

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        var far = await harness.CreateOnlineDriverAsync(FourHundredM);

        await harness.SetDriverLevelAsync(near.DriverId, 1);
        await harness.SetDriverLevelAsync(far.DriverId, 3);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(near.DriverId, outcome.DriverId);

        var scores = await harness.ReadScoresAsync(rideId);
        Assert.All(scores, row => Assert.Equal(7, row.Version));

        using var breakdown = JsonDocument.Parse(scores[0].Breakdown);
        var weights = breakdown.RootElement.GetProperty("weights");

        Assert.Equal(1d, weights.GetProperty("distance").GetDouble());
        Assert.Equal(0d, weights.GetProperty("level").GetDouble());

        // The score is the distance term alone, and it is still reproducible from the row.
        Assert.Equal(
            breakdown.RootElement.GetProperty("terms").GetProperty("distance").GetDouble(),
            (double)scores[0].Score,
            6);
    }

    /// <summary>
    /// R-12: Phase 1 is sequential. The flag that would turn on batch matching exists so the
    /// decision is visible in configuration, and it is off.
    /// </summary>
    [Fact]
    public async Task Batch_matching_is_a_disabled_feature_flag()
    {
        await using var harness = await StartAsync();

        Assert.False(
            harness.Services.GetRequiredService<IOptions<DispatchOptions>>().Value.BatchMatchingEnabled);

        await harness.CreateOnlineDriverAsync(Nearest);
        await harness.CreateOnlineDriverAsync(FourHundredM);
        await harness.CreateOnlineDriverAsync(FourKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, rideId);

        // Three candidates scored, exactly one offered.
        Assert.Equal(3, (await harness.ReadScoresAsync(rideId)).Count);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            1,
            await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection,
                "SELECT count(*)::int FROM dispatch.offers WHERE ride_id = @RideId;",
                new { RideId = rideId }));
    }

    /// <summary>Every rank in the audit is a cascade position, and they are dense and ordered.</summary>
    [Fact]
    public async Task Rank_follows_the_score_order_the_cascade_will_use()
    {
        await using var harness = await StartAsync();

        await harness.CreateOnlineDriverAsync(Nearest);
        await harness.CreateOnlineDriverAsync(FourHundredM);
        await harness.CreateOnlineDriverAsync(FourKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, rideId);

        var ranks = (await harness.ReadScoresAsync(rideId))
            .Select(row => JsonDocument.Parse(row.Breakdown).RootElement.GetProperty("rank").GetInt32())
            .ToArray();

        Assert.Equal([0, 1, 2], ranks);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}

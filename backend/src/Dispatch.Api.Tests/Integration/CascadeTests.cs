using Dapper;
using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Dispatch.Timers;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 3 — "the offer cascade tries candidates in score order and ends in ExpiredNoDriver at
/// 120 s".</b> D5' §3.5's sequential cascade and US-6A.11's global timeout.
/// </summary>
[Collection<DispatchCollection>]
public sealed class CascadeTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);
    private static readonly GeoPoint FourHundredM = new(6.9380, 79.8428);
    private static readonly GeoPoint OneKm = new(6.9434, 79.8428);

    /// <summary>
    /// Three drivers, three declines, and the offers go out best score first. At equal level the
    /// score is monotonic in distance, so the expected order is also the obvious one — which is
    /// what makes this readable as a statement about the cascade rather than about the formula.
    /// </summary>
    [Fact]
    public async Task The_cascade_walks_the_candidates_in_score_order()
    {
        await using var harness = await StartAsync();

        var first = await harness.CreateOnlineDriverAsync(Nearest);
        var second = await harness.CreateOnlineDriverAsync(FourHundredM);
        var third = await harness.CreateOnlineDriverAsync(OneKm);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var request = await OfferLoopTests.BuildRequestAsync(harness, rideId);

        await using var scope = harness.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        var offered = new List<Guid>();

        var round = await dispatch.BeginAsync(request, TestContext.Current.CancellationToken);

        foreach (var driver in new[] { first, second, third })
        {
            Assert.Equal(DispatchResult.Offered, round.Result);
            offered.Add(round.DriverId!.Value);

            // Through ride-svc's real decline route: it is ride-svc that performs Offered → Matching
            // and emits offer.declined, and a test that released only dispatch's own row would leave
            // the ride Offered and prove nothing about the cascade.
            await harness.DeclineOfferAsync(driver, rideId, round.OfferId!.Value);

            await dispatch.ReleaseLiveOfferAsync(
                rideId, OfferStatuses.Declined, TestContext.Current.CancellationToken);

            round = await dispatch.DispatchAsync(request, TestContext.Current.CancellationToken);
        }

        Assert.Equal([first.DriverId, second.DriverId, third.DriverId], offered);

        // Everyone has declined, so this round finds nobody — and the ride stays in Matching
        // rather than ending, because a driver who comes online inside the remaining seconds of the
        // 120 s window is still a candidate. It is the deadline that ends it, not an empty round.
        Assert.Equal(DispatchResult.NoCandidate, round.Result);
        Assert.Equal("Matching", (await harness.ReadRideAsync(rideId)).State);

        await MakeDeadlineDueAsync(harness, rideId);
        Assert.Equal(1, await SweepAsync(harness));

        Assert.Equal("ExpiredNoDriver", (await harness.ReadRideAsync(rideId)).State);
    }

    /// <summary>
    /// The deadline armed on <c>ride.requested</c> is D5' §3.5's two minutes, and it is durable —
    /// a <c>dispatch.timers</c> row, not a Redis key and not an in-process timer.
    /// </summary>
    [Fact]
    public async Task Beginning_a_ride_arms_a_durable_120_second_deadline()
    {
        await using var harness = await StartAsync();

        var before = DateTimeOffset.UtcNow;
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        await OfferLoopTests.DispatchAsync(harness, rideId);

        await using var connection = await harness.OpenAsync();
        var timer = await connection.QuerySingleAsync<DispatchTimerRowText>(
            """
            SELECT id AS Id, kind AS Kind, ride_id AS RideId, driver_id AS DriverId,
                   fire_at AS FireAt, fired_at AS FiredAt
              FROM dispatch.timers WHERE ride_id = @RideId;
            """,
            new { RideId = rideId });

        Assert.Equal(DispatchTimerKinds.RideTimeout, timer.Kind);
        Assert.Null(timer.FiredAt);

        // The subject is the ride, and there is deliberately no driver — the timeout has to fire in
        // exactly the case where none was ever found (migration 0711).
        Assert.Null(timer.DriverId);
        Assert.InRange(timer.FireAt - before, TimeSpan.FromSeconds(118), TimeSpan.FromSeconds(122));

        Assert.Equal(
            TimeSpan.FromSeconds(120),
            harness.Services.GetRequiredService<IOptions<DispatchOptions>>().Value.GlobalTimeout);
    }

    /// <summary>
    /// The deadline arriving with nobody found ends the ride in <c>ExpiredNoDriver</c> (US-6A.11).
    /// The clock is wound forward rather than waited out; what is under test is the sweep and the
    /// <c>system-cancel</c> it sends, not <see cref="PeriodicTimer"/>.
    /// </summary>
    [Fact]
    public async Task The_global_deadline_ends_the_ride_in_ExpiredNoDriver()
    {
        await using var harness = await StartAsync();

        // Nobody online at all: the case that has no offer, no backstop and nothing else watching.
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);
        Assert.Equal("Matching", (await harness.ReadRideAsync(rideId)).State);

        await MakeDeadlineDueAsync(harness, rideId);
        Assert.Equal(1, await SweepAsync(harness));

        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal("ExpiredNoDriver", ride.State);
        Assert.Null(ride.CurrentOfferId);

        // The timer that fired is retired, so the sweep does not try again every half-second.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.timers WHERE ride_id = @RideId AND fired_at IS NULL;",
                new { RideId = rideId }));
    }

    /// <summary>
    /// A deadline that arrives while a driver is still looking at an offer waits for the 15 s
    /// window instead of killing the ride under them: §11.12's <c>ExpiredNoDriver</c> row resolves
    /// from <c>Matching</c> alone, and the one candidate the cascade did find should get their turn.
    /// </summary>
    [Fact]
    public async Task A_deadline_that_arrives_mid_offer_waits_for_the_offer_to_settle()
    {
        await using var harness = await StartAsync();

        await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);

        await MakeDeadlineDueAsync(harness, rideId);
        Assert.Equal(1, await SweepAsync(harness));

        // Still Offered, and the deadline has been pushed out to just past the offer's own.
        Assert.Equal("Offered", (await harness.ReadRideAsync(rideId)).State);

        await using var connection = await harness.OpenAsync();
        var fireAt = await connection.ExecuteScalarAsync<DateTimeOffset>(
            """
            SELECT fire_at FROM dispatch.timers
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = DispatchTimerKinds.RideTimeout });

        Assert.InRange(fireAt - outcome.ExpiresAt!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    /// <summary>A ride a driver accepted must not be cancelled by the deadline behind them.</summary>
    [Fact]
    public async Task Accepting_retires_the_deadline()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, rideId);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatchService>()
                .MarkAcceptedAsync(rideId, driver.DriverId, TestContext.Current.CancellationToken);
        }

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.timers WHERE ride_id = @RideId AND fired_at IS NULL;",
                new { RideId = rideId }));

        // Nothing is due, so the sweep has nothing to do — and the ride stays where the driver put it.
        Assert.Equal(0, await SweepAsync(harness));
    }

    /// <summary>
    /// At-least-once delivery (D6' §2.3) means <c>ride.requested</c> arrives more than once. Two
    /// deadlines for one ride would mean two attempts to cancel it, so the partial unique index is
    /// the arming's idempotency (migration 0711).
    /// </summary>
    [Fact]
    public async Task A_redelivered_ride_requested_does_not_arm_a_second_deadline()
    {
        await using var harness = await StartAsync();

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var request = await OfferLoopTests.BuildRequestAsync(harness, rideId);

        await using var scope = harness.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        await dispatch.BeginAsync(request, TestContext.Current.CancellationToken);
        await dispatch.BeginAsync(request, TestContext.Current.CancellationToken);
        await dispatch.BeginAsync(request, TestContext.Current.CancellationToken);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.timers WHERE ride_id = @RideId;",
                new { RideId = rideId }));
    }

    /// <summary>
    /// The bug migration 0712 fixes: an ACCEPTED offer stayed live for ever, so the second ride a
    /// driver was ever offered was refused by <c>ux_offers_driver_live</c> — and every one after it.
    /// </summary>
    [Fact]
    public async Task A_driver_who_finished_a_ride_can_be_offered_another_one()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        // Funded, so the D-08 gate is not what decides the second trip — this test is about R-10's
        // index, and the wallet has a suite of its own.
        await harness.SetWalletBalanceAsync(driver.DriverId, 1_000_000);

        var firstRideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var first = await OfferLoopTests.DispatchAsync(harness, firstRideId);
        Assert.Equal(DispatchResult.Offered, first.Result);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

            await dispatch.MarkAcceptedAsync(firstRideId, driver.DriverId, TestContext.Current.CancellationToken);
            await dispatch.ReturnToPoolAsync(driver.DriverId, TestContext.Current.CancellationToken);
        }

        var second = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.Offered, second.Result);
        Assert.Equal(driver.DriverId, second.DriverId);

        // The first offer keeps its status — it is what the driver did — and is merely released.
        await using var connection = await harness.OpenAsync();
        var settled = await connection.QuerySingleAsync<SettledOffer>(
            "SELECT status AS Status, released_at AS ReleasedAt FROM dispatch.offers WHERE id = @OfferId;",
            new { first.OfferId });

        Assert.Equal(OfferStatuses.Accepted, settled.Status);
        Assert.NotNull(settled.ReleasedAt);
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>Winds a ride's cascade deadline back to now, so the next sweep claims it.</summary>
    private static async Task MakeDeadlineDueAsync(DispatchHarness harness, Guid rideId)
    {
        await using var connection = await harness.OpenAsync();

        var affected = await connection.ExecuteAsync(
            """
            UPDATE dispatch.timers SET fire_at = now() - interval '1 second'
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = DispatchTimerKinds.RideTimeout });

        Assert.Equal(1, affected);
    }

    private static async Task<int> SweepAsync(DispatchHarness harness)
    {
        var worker = ActivatorUtilities.CreateInstance<DispatchTimerWorker>(harness.Services);

        return await worker.SweepOnceAsync(TestContext.Current.CancellationToken);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }

    private sealed record DispatchTimerRowText(
        Guid Id, string Kind, Guid? RideId, Guid? DriverId, DateTimeOffset FireAt, DateTimeOffset? FiredAt);

    private sealed record SettledOffer(string Status, DateTimeOffset? ReleasedAt);
}

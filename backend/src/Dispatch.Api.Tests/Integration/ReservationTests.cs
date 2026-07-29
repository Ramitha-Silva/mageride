using Dapper;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Redis;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// R-10 — "one live offer per driver", guaranteed by <b>both</b> the Redis Lua reservation and the
/// Postgres <c>ux_offers_driver_live</c> partial unique index.
/// </summary>
/// <remarks>
/// ADD §11.11: "Redis Lua is the fast path so a driver app doesn't see a phantom offer that has
/// already been claimed. Postgres is the authoritative writer; if Redis is partitioned / flushed,
/// the <c>UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')</c> constraint … are the only
/// guarantees that survive. <b>Both are required; neither alone is sufficient.</b>" So both are
/// tested, and one test deliberately removes the fast path to show the slow one still holds.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class ReservationTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    [Fact]
    public async Task Two_rides_racing_for_the_only_driver_produce_exactly_one_offer()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var firstRide = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var secondRide = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        var results = await Task.WhenAll(
            OfferLoopTests.DispatchAsync(harness, firstRide),
            OfferLoopTests.DispatchAsync(harness, secondRide));

        Assert.Single(results, r => r.Result == DispatchResult.Offered);
        Assert.Single(results, r => r.Result == DispatchResult.NoCandidate);

        await using var connection = await harness.OpenAsync();
        var live = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM dispatch.offers WHERE driver_id = @DriverId AND status = 'OFFERED';",
            new { driver.DriverId });

        Assert.Equal(1, live);
    }

    [Fact]
    public async Task Ten_rides_racing_for_one_driver_still_produce_exactly_one_offer()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var rides = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            rides.Add(await harness.RequestRideAsync(await harness.CreatePassengerAsync()));
        }

        var results = await Task.WhenAll(rides.Select(r => OfferLoopTests.DispatchAsync(harness, r)));

        Assert.Single(results, r => r.Result == DispatchResult.Offered);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.offers WHERE driver_id = @DriverId;", new { driver.DriverId }));
    }

    /// <summary>
    /// The half ADD §11.11 says is the one that survives: with the Redis lock gone, a second offer
    /// to the same driver must still be impossible.
    /// </summary>
    [Fact]
    public async Task With_the_Redis_lock_deleted_the_partial_unique_index_still_refuses_a_second_offer()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var firstRide = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        Assert.Equal(DispatchResult.Offered, (await OfferLoopTests.DispatchAsync(harness, firstRide)).Result);

        // Simulate exactly what ADD §11.11 warns about: Redis partitioned or flushed. The lock is
        // gone and the driver is back in the index as if nothing had happened.
        var db = harness.Redis.GetDatabase();
        await db.KeyDeleteAsync(RedisKeys.DriverOfferLock(driver.DriverId));
        await harness.Services.GetRequiredService<IDriverIndex>().IndexAvailableAsync(
            driver.DriverId, driver.VehicleId, "three_wheeler", Nearest, TestContext.Current.CancellationToken);

        // And put the durable presence row back to AVAILABLE, so nothing but the unique index
        // stands between this driver and a second live offer.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE dispatch.driver_presence SET state = 'AVAILABLE' WHERE driver_id = @DriverId;",
                new { driver.DriverId });
        }

        var secondRide = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, secondRide);

        Assert.Equal(DispatchResult.NoCandidate, outcome.Result);

        // The pre-filter and the post-filter both returned this driver — the index is what said no.
        Assert.Equal(1, outcome.PreFilterCount);
        Assert.Equal(1, outcome.CandidateCount);

        await using var check = await harness.OpenAsync();
        Assert.Equal(
            1,
            await check.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.offers WHERE driver_id = @DriverId AND status IN ('OFFERED','ACCEPTED');",
                new { driver.DriverId }));
    }

    [Fact]
    public async Task The_Lua_reservation_is_atomic_with_the_offer_hint()
    {
        // ADD §9.4's lock:driver-offer row: "Lua script combines SET NX with insert into
        // offer:{rideId}". A lock held with no hint beside it is the phantom the fast path exists
        // to prevent, so the two are one round trip.
        await using var harness = await StartAsync();

        var index = harness.Services.GetRequiredService<IDriverIndex>();
        var driverId = Guid.NewGuid();
        var rideId = Guid.NewGuid();
        var offerId = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;

        Assert.True(await index.TryReserveAsync(driverId, rideId, offerId, TimeSpan.FromSeconds(15), token));

        var hint = await index.ReadOfferAsync(rideId, token);
        Assert.NotNull(hint);
        Assert.Equal(offerId, hint.OfferId);
        Assert.Equal(driverId, hint.DriverId);
        Assert.Equal("OFFERED", hint.Status);

        var db = harness.Redis.GetDatabase();
        Assert.Equal(offerId.ToString(), await db.StringGetAsync(RedisKeys.DriverOfferLock(driverId)));

        // Both keys carry the 15 s window (D-07 PEXPIRE).
        Assert.InRange(
            (await db.KeyTimeToLiveAsync(RedisKeys.DriverOfferLock(driverId)))!.Value,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        Assert.InRange(
            (await db.KeyTimeToLiveAsync(RedisKeys.Offer(rideId)))!.Value,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

        // A second reservation for the same driver, on a different ride, is refused — and does not
        // overwrite the first ride's hint.
        var otherRide = Guid.NewGuid();
        Assert.False(await index.TryReserveAsync(driverId, otherRide, Guid.NewGuid(), TimeSpan.FromSeconds(15), token));
        Assert.Null(await index.ReadOfferAsync(otherRide, token));
    }

    [Fact]
    public async Task Releasing_a_reservation_only_clears_the_offer_that_holds_it()
    {
        // A blind DEL would let a slow expiry sweep unlock a driver who has since been offered a
        // different ride — the driver would then hold two live offers with Redis agreeing.
        await using var harness = await StartAsync();

        var index = harness.Services.GetRequiredService<IDriverIndex>();
        var driverId = Guid.NewGuid();
        var rideId = Guid.NewGuid();
        var currentOffer = Guid.NewGuid();
        var staleOffer = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;

        Assert.True(await index.TryReserveAsync(driverId, rideId, currentOffer, TimeSpan.FromSeconds(15), token));

        await index.ReleaseReservationAsync(driverId, rideId, staleOffer, token);

        Assert.True(await harness.Redis.GetDatabase().KeyExistsAsync(RedisKeys.DriverOfferLock(driverId)));
        Assert.NotNull(await index.ReadOfferAsync(rideId, token));

        await index.ReleaseReservationAsync(driverId, rideId, currentOffer, token);

        Assert.False(await harness.Redis.GetDatabase().KeyExistsAsync(RedisKeys.DriverOfferLock(driverId)));
        Assert.Null(await index.ReadOfferAsync(rideId, token));
    }

    [Fact]
    public async Task An_accepted_ride_takes_the_driver_out_of_the_pool()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatchService>()
                .MarkAcceptedAsync(rideId, driver.DriverId, TestContext.Current.CancellationToken);
        }

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            OfferStatuses.Accepted,
            await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId }));

        Assert.Equal(
            PresenceStates.OnRide,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId }));

        // The backstop is retired: an accepted ride must not be pulled back to Matching 15 s later.
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM rides.timers WHERE ride_id = @RideId AND fired_at IS NULL;",
                new { RideId = rideId }));

        Assert.Null(await harness.Redis.GetDatabase().SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", new H3Grid(5, 2).CellAt(Nearest)),
            driver.DriverId.ToString()));
    }

    [Fact]
    public async Task A_declined_offer_puts_the_driver_straight_back_in_the_pool()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatchService>()
                .ReleaseLiveOfferAsync(rideId, OfferStatuses.Declined, TestContext.Current.CancellationToken);
        }

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            OfferStatuses.Declined,
            await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId }));

        Assert.Equal(
            PresenceStates.Available,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId }));

        // ADD §9.4: re-added to geo:drivers:available, lock released. Skipping either leaves a
        // "ghost-busy" driver.
        Assert.NotNull(await harness.Redis.GetDatabase().SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", new H3Grid(5, 2).CellAt(Nearest)),
            driver.DriverId.ToString()));
        Assert.False(await harness.Redis.GetDatabase().KeyExistsAsync(RedisKeys.DriverOfferLock(driver.DriverId)));
    }

    /// <summary>
    /// D5' §3.5's cascade, and the memory that makes it terminate: a driver who has already been
    /// offered this ride is not offered it again.
    /// </summary>
    /// <remarks>
    /// The decline goes through <b>ride-svc's</b> route, not through dispatch's own release. It is
    /// ride-svc that performs <c>Offered → Matching</c>; releasing dispatch's row alone would leave
    /// the ride in <c>Offered</c> and the next round would be refused for the right reason at the
    /// wrong time.
    /// </remarks>
    [Fact]
    public async Task The_cascade_moves_to_the_next_driver_and_never_back_to_the_first()
    {
        await using var harness = await StartAsync();

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        var next = await harness.CreateOnlineDriverAsync(new GeoPoint(6.9700, 79.8600));

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        var first = await OfferLoopTests.DispatchAsync(harness, rideId);
        Assert.Equal(near.DriverId, first.DriverId);

        var request = await OfferLoopTests.BuildRequestAsync(harness, rideId);

        await harness.DeclineOfferAsync(near, rideId, first.OfferId!.Value);
        Assert.Equal("Matching", (await harness.ReadRideAsync(rideId)).State);

        await using var scope = harness.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        // What the offer.declined consumer does.
        await dispatch.ReleaseLiveOfferAsync(rideId, OfferStatuses.Declined, TestContext.Current.CancellationToken);

        var second = await dispatch.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchResult.Offered, second.Result);
        Assert.Equal(next.DriverId, second.DriverId);

        // Round three: both have now seen it, so there is nobody left — not a third offer to the
        // driver who declined first.
        await harness.DeclineOfferAsync(next, rideId, second.OfferId!.Value);
        await dispatch.ReleaseLiveOfferAsync(rideId, OfferStatuses.Declined, TestContext.Current.CancellationToken);

        var third = await dispatch.DispatchAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchResult.NoCandidate, third.Result);
        Assert.Equal(2, third.PreFilterCount);      // both are back in the index…
        Assert.Equal(0, third.CandidateCount);      // …and both are excluded from this ride
    }

    [Fact]
    public async Task The_cascade_stops_at_the_configured_round_limit()
    {
        await using var harness = await StartAsync(
            new Dictionary<string, string?> { ["Dispatch:MaxOfferRounds"] = "1" });

        var near = await harness.CreateOnlineDriverAsync(Nearest);
        await harness.CreateOnlineDriverAsync(new GeoPoint(6.9700, 79.8600));

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var request = await OfferLoopTests.BuildRequestAsync(harness, rideId);

        await using var scope = harness.Services.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        var first = await dispatch.BeginAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(DispatchResult.Offered, first.Result);

        await harness.DeclineOfferAsync(near, rideId, first.OfferId!.Value);
        await dispatch.ReleaseLiveOfferAsync(rideId, OfferStatuses.Declined, TestContext.Current.CancellationToken);

        // A driver is available and near, and the ride is back in Matching — the round bound is
        // the only thing stopping a second offer. C034 made that ending terminal: §11.12's
        // "No candidates after N rounds OR timeout (…)" row produces ExpiredNoDriver either way,
        // so the passenger is told rather than left watching a spinner (US-6A.11).
        Assert.Equal(
            DispatchResult.ExpiredNoDriver,
            (await dispatch.DispatchAsync(request, TestContext.Current.CancellationToken)).Result);

        Assert.Equal("ExpiredNoDriver", (await harness.ReadRideAsync(rideId)).State);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}

using Dapper;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Dispatch.Timers;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 3 — "an unanswered offer expires at 15 s and the ride returns to Matching".</b>
/// </summary>
/// <remarks>
/// The 15 s itself is asserted in <see cref="OfferLoopTests"/> against the deadline ride-svc
/// stamps, so nothing here has to sleep for it: these run on a two-second window and prove the
/// mechanism. The one that matters most flushes Redis mid-offer — R-04 says the durable backstop
/// fires "independent of any Redis TTL", and a test that leaves Redis intact cannot tell the two
/// apart.
/// </remarks>
[Collection<DispatchCollection>]
public sealed class OfferExpiryTests(PostgresFixture postgres, RedisFixture redis)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);

    /// <summary>Short enough to keep the suite fast, long enough that the offer is really placed.</summary>
    private static readonly Dictionary<string, string?> ShortWindow = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dispatch:OfferTtl"] = "00:00:02",
    };

    [Fact]
    public async Task An_unanswered_offer_expires_and_the_ride_returns_to_Matching()
    {
        await using var harness = await StartAsync(ShortWindow);

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);
        Assert.Equal("Offered", (await harness.ReadRideAsync(rideId)).State);

        await SweepUntilFiredAsync(harness, rideId);

        // The ride is back in the pool — and holds no stale offer. Leaving current_offer_id set
        // would make ADD §11.11's second accept origin reachable and the accept's audit row
        // (from_state='Offered') start lying; the C022 handoff left that decision to whoever
        // landed R-04.
        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal("Matching", ride.State);
        Assert.Null(ride.CurrentOfferId);
        Assert.Null(ride.OfferedDriverId);
        Assert.Null(ride.OfferExpiresAt);

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            OfferStatuses.Expired,
            await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId }));

        // The driver is back in the pool, not stranded (ADD §9.4).
        Assert.Equal(
            PresenceStates.Available,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId }));

        Assert.NotNull(await harness.Redis.GetDatabase().SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", new H3Grid(5, 2).CellAt(Nearest)),
            driver.DriverId.ToString()));

        Assert.False(await harness.Redis.GetDatabase().KeyExistsAsync(RedisKeys.DriverOfferLock(driver.DriverId)));
    }

    /// <summary>
    /// <b>R-04, stated exactly.</b> Everything Redis knew about this offer is destroyed while it is
    /// in flight; the <c>rides.timers</c> row is the only thing left, and the ride still comes back.
    /// </summary>
    [Fact]
    public async Task The_expiry_survives_a_complete_Redis_flush()
    {
        await using var harness = await StartAsync(ShortWindow);

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, outcome.Result);

        await harness.FlushRedisAsync();

        Assert.False(await harness.Redis.GetDatabase().KeyExistsAsync(RedisKeys.Offer(rideId)));
        Assert.False(await harness.Redis.GetDatabase().KeyExistsAsync(RedisKeys.DriverOfferLock(driver.DriverId)));

        await SweepUntilFiredAsync(harness, rideId);

        Assert.Equal("Matching", (await harness.ReadRideAsync(rideId)).State);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            OfferStatuses.Expired,
            await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId }));
    }

    /// <summary>
    /// The backstop must not fire early. ride-svc evaluates <c>offer_expires_at &lt;= now()</c>, so
    /// a sweeping node whose clock ran ahead is answered 409 and the timer is pushed out — the
    /// driver keeps the window they were promised.
    /// </summary>
    [Fact]
    public async Task A_sweep_before_the_deadline_does_not_take_the_offer_away()
    {
        await using var harness = await StartAsync();     // the default 15 s window

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await harness.CreateOnlineDriverAsync(Nearest);

        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);
        Assert.Equal(DispatchResult.Offered, outcome.Result);

        // Force the timer due now, well inside the offer window.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE rides.timers SET fire_at = now() - interval '1 second' WHERE ride_id = @RideId;",
                new { RideId = rideId });
        }

        Assert.Equal(1, await SweepAsync(harness));

        var ride = await harness.ReadRideAsync(rideId);
        Assert.Equal("Offered", ride.State);
        Assert.Equal(outcome.OfferId, ride.CurrentOfferId);

        await using var check = await harness.OpenAsync();

        Assert.Equal(
            OfferStatuses.Offered,
            await check.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId }));

        // Unfired and rescheduled, not abandoned.
        Assert.Null(await check.ExecuteScalarAsync<DateTimeOffset?>(
            "SELECT fired_at FROM rides.timers WHERE ride_id = @RideId;", new { RideId = rideId }));

        Assert.True(
            await check.ExecuteScalarAsync<DateTimeOffset>(
                "SELECT fire_at FROM rides.timers WHERE ride_id = @RideId;", new { RideId = rideId })
            > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_backstop_whose_offer_was_already_answered_does_nothing()
    {
        await using var harness = await StartAsync(ShortWindow);

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatchService>()
                .MarkAcceptedAsync(rideId, driver.DriverId, TestContext.Current.CancellationToken);
        }

        // Force it due and sweep. An accepted ride must not be dragged back to Matching.
        await using (var connection = await harness.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE rides.timers SET fire_at = now() - interval '1 second', fired_at = NULL WHERE ride_id = @RideId;",
                new { RideId = rideId });
        }

        await SweepAsync(harness);

        await using var check = await harness.OpenAsync();
        Assert.Equal(
            OfferStatuses.Accepted,
            await check.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { outcome.OfferId }));

        Assert.Equal(
            PresenceStates.OnRide,
            await check.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId }));
    }

    [Fact]
    public async Task A_claimed_timer_is_leased_not_deleted_so_a_dead_worker_loses_nothing()
    {
        // If a claim marked the row fired outright, a worker killed between claiming and calling
        // ride-svc would take the ride's only backstop with it and leave it in Offered forever.
        await using var harness = await StartAsync(ShortWindow);

        await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var outcome = await OfferLoopTests.DispatchAsync(harness, rideId);

        await using var connection = await harness.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE rides.timers SET fire_at = now() - interval '1 second' WHERE ride_id = @RideId;",
            new { RideId = rideId });

        var timers = harness.Services.GetRequiredService<
            MageRide.Dispatch.Persistence.IOfferTimerRepository>();

        var claimed = await timers.ClaimDueAsync(
            connection, null, batchSize: 10, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var only = Assert.Single(claimed);
        Assert.Equal(outcome.OfferId, only.OfferId);
        Assert.Equal(outcome.DriverId, only.DriverId);

        // A second claim finds nothing: the lease is what keeps two replicas from double-firing.
        Assert.Empty(await timers.ClaimDueAsync(
            connection, null, batchSize: 10, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // But the row is still unfired and still due later, so the work is not lost.
        Assert.Null(await connection.ExecuteScalarAsync<DateTimeOffset?>(
            "SELECT fired_at FROM rides.timers WHERE id = @Id;", new { only.Id }));

        Assert.True(
            await connection.ExecuteScalarAsync<DateTimeOffset>(
                "SELECT fire_at FROM rides.timers WHERE id = @Id;", new { only.Id })
            > DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// D-07's accelerator: <c>offer:{rideId}</c> lapsing in Redis reassigns the offer without
    /// waiting for the next durable sweep. The expiry worker is off here, so the only thing that
    /// can move this ride is the keyspace notification.
    /// </summary>
    [Fact]
    public async Task The_Redis_key_expiring_reassigns_the_offer_before_the_sweep_would()
    {
        await using var harness = await StartAsync(new Dictionary<string, string?>(ShortWindow, StringComparer.OrdinalIgnoreCase)
        {
            ["Dispatch:KeyspaceNotificationsEnabled"] = "true",
            ["Dispatch:ExpiryWorkerEnabled"] = "false",
        });

        await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        Assert.Equal(DispatchResult.Offered, (await OfferLoopTests.DispatchAsync(harness, rideId)).Result);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string state;

        do
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            state = (await harness.ReadRideAsync(rideId)).State;
        }
        while (state == "Offered" && DateTimeOffset.UtcNow < deadline);

        Assert.Equal("Matching", state);
    }

    [Fact]
    public async Task The_background_worker_fires_the_backstop_without_being_asked()
    {
        // Everything above drives SweepOnceAsync directly. This is the one that proves the hosted
        // service is actually wired up and ticking.
        await using var harness = await StartAsync(new Dictionary<string, string?>(ShortWindow, StringComparer.OrdinalIgnoreCase)
        {
            ["Dispatch:ExpiryWorkerEnabled"] = "true",
            ["Dispatch:TimerPollInterval"] = "00:00:00.200",
        });

        await harness.CreateOnlineDriverAsync(Nearest);
        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());

        Assert.Equal(DispatchResult.Offered, (await OfferLoopTests.DispatchAsync(harness, rideId)).Result);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string state;

        do
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            state = (await harness.ReadRideAsync(rideId)).State;
        }
        while (state == "Offered" && DateTimeOffset.UtcNow < deadline);

        Assert.Equal("Matching", state);
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>Runs the backstop until the ride's timer has fired, or gives up loudly.</summary>
    private static async Task SweepUntilFiredAsync(DispatchHarness harness, Guid rideId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await SweepAsync(harness);

            await using var connection = await harness.OpenAsync();
            var fired = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM rides.timers WHERE ride_id = @RideId AND fired_at IS NOT NULL;",
                new { RideId = rideId });

            if (fired > 0)
            {
                return;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The offer-expiry backstop never fired for ride {rideId}.");
    }

    /// <summary>
    /// One sweep, on demand. Built from the container rather than fished out of the hosted
    /// services so it works whether or not the background worker is running in this harness —
    /// <see cref="OfferExpiryWorker.SweepOnceAsync"/> holds no state between ticks.
    /// </summary>
    private static Task<int> SweepAsync(DispatchHarness harness) =>
        ActivatorUtilities.CreateInstance<OfferExpiryWorker>(harness.Services)
            .SweepOnceAsync(TestContext.Current.CancellationToken);

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}

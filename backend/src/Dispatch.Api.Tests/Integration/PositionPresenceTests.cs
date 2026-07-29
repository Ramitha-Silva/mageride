using Confluent.Kafka;
using Dapper;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Presence;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Messaging;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// R-08: <c>dispatch.driver_presence</c> and the Redis availability index are kept live by position
/// events, not by the driver re-posting <c>/v1/standby/online</c>.
/// </summary>
[Collection<DispatchCollection>]
public sealed class PositionPresenceTests(
    PostgresFixture postgres, RedisFixture redis, RedpandaFixture redpanda)
{
    private static readonly GeoPoint WentOnlineAt = new(6.9350, 79.8430);

    /// <summary>~1.1 km north — well past the 25 m coalescing threshold.</summary>
    private static readonly GeoPoint MovedTo = new(6.9450, 79.8430);

    /// <summary>~11 m east — inside it.</summary>
    private static readonly GeoPoint BarelyMoved = new(6.9350, 79.8431);

    [Fact]
    public async Task A_position_sample_moves_the_presence_row_and_the_geo_index()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);

        var update = await RecordAsync(harness, driver.VehicleId, MovedTo);

        Assert.NotNull(update);
        Assert.True(update.Moved);
        Assert.Equal(driver.DriverId, update.DriverId);
        Assert.Equal(PresenceStates.Available, update.State);

        // The durable row is what the exact ST_DWithin post-filter reads, so it is what has to move.
        await using var connection = await harness.OpenAsync();
        var stored = await connection.QuerySingleAsync<StoredPosition>(
            """
            SELECT ST_Y(geo::geometry) AS Lat, ST_X(geo::geometry) AS Lng, last_seen_at AS LastSeenAt
              FROM dispatch.driver_presence WHERE driver_id = @DriverId;
            """,
            new { driver.DriverId });

        Assert.Equal(MovedTo.Latitude, stored.Lat, 5);
        Assert.Equal(MovedTo.Longitude, stored.Lng, 5);

        // …and the driver is discoverable from the cell they are now in, not the one they left.
        var grid = new H3Grid(5, 2);
        var db = harness.Redis.GetDatabase();

        Assert.NotNull(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", grid.CellAt(MovedTo)), driver.DriverId.ToString()));

        if (grid.CellAt(MovedTo) != grid.CellAt(WentOnlineAt))
        {
            Assert.Null(await db.SortedSetScoreAsync(
                RedisKeys.AvailableDrivers("three_wheeler", grid.CellAt(WentOnlineAt)), driver.DriverId.ToString()));
        }
    }

    /// <summary>
    /// A driver waiting at a rank is the candidate this service most wants to keep, so their
    /// liveness advances even though their position does not (D5' §5.2's Δpos coalescing).
    /// </summary>
    [Fact]
    public async Task A_stationary_driver_stays_fresh_without_a_write_to_their_coordinate()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);
        await harness.AgePresenceAsync(driver.DriverId, TimeSpan.FromMinutes(30));

        var update = await RecordAsync(harness, driver.VehicleId, BarelyMoved);

        Assert.NotNull(update);
        Assert.False(update.Moved);

        await using var connection = await harness.OpenAsync();
        var stored = await connection.QuerySingleAsync<StoredPosition>(
            """
            SELECT ST_Y(geo::geometry) AS Lat, ST_X(geo::geometry) AS Lng, last_seen_at AS LastSeenAt
              FROM dispatch.driver_presence WHERE driver_id = @DriverId;
            """,
            new { driver.DriverId });

        // The coordinate is the one they went online at — the sample was inside the threshold.
        Assert.Equal(WentOnlineAt.Longitude, stored.Lng, 5);

        // Liveness advanced anyway, which is the whole point: the freshness gate is about whether
        // the driver is still there, not about whether they are moving.
        Assert.InRange(DateTimeOffset.UtcNow - stored.LastSeenAt, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    /// <summary>The R-08 heartbeat: the 60 s availability-hash TTL is refreshed by every sample.</summary>
    [Fact]
    public async Task A_sample_refreshes_the_sixty_second_availability_hash()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);

        var key = RedisKeys.DriverAvailability(driver.DriverId);
        await harness.Redis.GetDatabase().KeyExpireAsync(key, TimeSpan.FromSeconds(5));

        await RecordAsync(harness, driver.VehicleId, BarelyMoved);

        var ttl = await harness.Redis.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// The hash's TTL lapsing takes a driver out of the hot index while the durable row survives.
    /// The next sample is what puts them back — otherwise a driver would silently stop receiving
    /// offers a minute after going online and never recover.
    /// </summary>
    [Fact]
    public async Task A_sample_re_indexes_a_driver_whose_availability_hash_expired()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);
        var grid = new H3Grid(5, 2);

        // What a lapsed 60 s TTL leaves behind: the durable row, and nothing in Redis.
        await harness.Redis.GetDatabase().KeyDeleteAsync(RedisKeys.DriverAvailability(driver.DriverId));
        await harness.Redis.GetDatabase().SortedSetRemoveAsync(
            RedisKeys.AvailableDrivers("three_wheeler", grid.CellAt(WentOnlineAt)), driver.DriverId.ToString());

        var gone = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(0, gone.PreFilterCount);

        await RecordAsync(harness, driver.VehicleId, WentOnlineAt);

        var back = await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        Assert.Equal(DispatchResult.Offered, back.Result);
        Assert.Equal(driver.DriverId, back.DriverId);
    }

    /// <summary>An OFFLINE driver's position must not put them back in the pool.</summary>
    [Fact]
    public async Task A_sample_for_an_offline_driver_changes_nothing()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);
        await harness.GoOfflineAsync(driver);

        Assert.Null(await RecordAsync(harness, driver.VehicleId, MovedTo));

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            PresenceStates.Offline,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;", new { driver.DriverId }));
    }

    /// <summary>
    /// An OFFERED driver's liveness is refreshed — they are coming back to the pool and their
    /// freshness decides whether they are a candidate when they do — but they stay out of the GEO
    /// set, or they would be offered a second ride while holding the first.
    /// </summary>
    [Fact]
    public async Task An_offered_driver_is_kept_fresh_but_stays_out_of_the_pool()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);
        await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        var update = await RecordAsync(harness, driver.VehicleId, MovedTo);

        Assert.NotNull(update);
        Assert.Equal(PresenceStates.Offered, update.State);

        var grid = new H3Grid(5, 2);
        var db = harness.Redis.GetDatabase();

        Assert.Null(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", grid.CellAt(MovedTo)), driver.DriverId.ToString()));

        Assert.Null(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers("three_wheeler", grid.CellAt(WentOnlineAt)), driver.DriverId.ToString()));
    }

    /// <summary>
    /// R-17's replay backlog arrives with a fresh receive time and a stale capture time. Ordering
    /// per vehicle holds because the topic is keyed by vehicleId, except across a rebalance — which
    /// is the window this guard exists for.
    /// </summary>
    [Fact]
    public async Task A_sample_older_than_the_row_is_ignored()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);

        Assert.NotNull(await RecordAsync(harness, driver.VehicleId, MovedTo));

        // Captured a minute ago, delivered now.
        var stale = await RecordAsync(
            harness, driver.VehicleId, WentOnlineAt, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Null(stale);

        await using var connection = await harness.OpenAsync();
        var lat = await connection.ExecuteScalarAsync<double>(
            "SELECT ST_Y(geo::geometry) FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
            new { driver.DriverId });

        Assert.Equal(MovedTo.Latitude, lat, 5);
    }

    /// <summary>
    /// The same thing through a real broker. Every other test here calls the service directly so a
    /// background consumer cannot race an assertion; this one exists because that is exactly what
    /// they cannot prove — that the topic name, the CBOR codec and the consumer group line up with
    /// what position-processor-svc (C024) actually produces.
    /// </summary>
    [Fact]
    public async Task A_normalised_position_on_the_broker_reaches_the_presence_row()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await redpanda.CreateTopicAsync(EventTopics.TelemetryNormalized);

        await using var harness = await DispatchHarness.StartAsync(
            postgres,
            redis,
            dispatchSettings: new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = redpanda.BootstrapServers,
                ["Dispatch:PositionConsumerEnabled"] = "true",
                ["Dispatch:PositionConsumerGroup"] = $"dispatch-svc-presence-test-{Guid.NewGuid():N}",
            });

        var driver = await harness.CreateOnlineDriverAsync(WentOnlineAt);

        // The consumer reads from the LATEST offset — a presence index is current state, so a
        // restart must not replay an hour of stale positions as if they were now. That means the
        // sample has to be published after the subscription is live, which is what the retry loop
        // below is for rather than a sleep.
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = redpanda.BootstrapServers }).Build();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var moved = false;

        while (!moved && DateTimeOffset.UtcNow < deadline)
        {
            var sample = new PositionSample(
                driver.VehicleId, DateTimeOffset.UtcNow, Seq: 1, MovedTo.Latitude, MovedTo.Longitude,
                PositionSource.Mobile);

            await producer.ProduceAsync(
                EventTopics.TelemetryNormalized,
                new Message<string, byte[]>
                {
                    Key = driver.VehicleId.ToString(),
                    Value = PositionSampleCodec.Encode(sample),
                });

            await Task.Delay(TimeSpan.FromSeconds(1));

            await using var connection = await harness.OpenAsync();
            var lat = await connection.ExecuteScalarAsync<double?>(
                "SELECT ST_Y(geo::geometry) FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
                new { driver.DriverId });

            moved = lat is { } value && Math.Abs(value - MovedTo.Latitude) < 0.00001;
        }

        Assert.True(moved, "telemetry.normalized never reached dispatch.driver_presence");
    }

    // -----------------------------------------------------------------------------------------

    private static async Task<PositionUpdate?> RecordAsync(
        DispatchHarness harness, Guid vehicleId, GeoPoint position, DateTimeOffset? sampledAt = null)
    {
        await using var scope = harness.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IPresenceService>()
            .RecordPositionAsync(
                vehicleId, position, sampledAt ?? DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }

    private sealed record StoredPosition(double Lat, double Lng, DateTimeOffset LastSeenAt);
}

using System.Globalization;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Processing;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using StackExchange.Redis;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// R-08's candidate index, kept at the driver's live position (ADD §9.4).
/// </summary>
/// <remarks>
/// <para>
/// The DoD's second line: "the availability index adds and removes a driver on phase transitions
/// and offline within one interval". <i>One interval</i> is one position sample — the phase is
/// dispatch-svc's fact and this service reconciles the GEO index to it on the next sample, which is
/// the only clock a position plane has.
/// </para>
/// <para>
/// dispatch-svc's half is set up here by writing the keys it writes rather than by running it: this
/// suite has no Postgres and dispatch-svc cannot start without one. The two are held together by
/// <c>RedisKeys</c> and by <see cref="A_driver_who_is_AVAILABLE_to_dispatch_is_AVAILABLE_here"/>,
/// which asserts the one string value the two services have to agree on and that neither can see
/// the other declare.
/// </para>
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "PositionProcessor")]
public sealed class DriverAvailabilityTests(RedisFixture redis)
{
    private const string Live = TelemetryHeaders.Live;
    private const string VehicleType = "three_wheeler";

    /// <summary>~450 m east of Colombo Fort — the same res-5 cell, a plausible step.</summary>
    private static readonly GeoPoint NextStreet = new(6.9344, 79.8469);

    [Fact]
    public async Task A_live_sample_puts_an_AVAILABLE_driver_in_their_res_5_cell_and_refreshes_the_TTL()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (driverId, vehicleId) = await GoOnlineAsync(db);

        // The availability hash starts with a TTL nearly lapsed, as it would after 55 s of silence.
        await db.KeyExpireAsync(RedisKeys.DriverAvailability(driverId), TimeSpan.FromSeconds(3));

        var result = await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1);

        Assert.Equal(PositionOutcome.Indexed, result.Outcome);
        Assert.Equal(PoolChange.Indexed, result.Pool);

        var cell = GeoCells.DispatchCell(Samples.ColomboFort);

        Assert.NotNull(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers(VehicleType, cell), driverId.ToString()));

        // R-08's 60 s, refreshed by the sample. Without this a driver silently falls out of the pool
        // a minute after going online and never recovers — the gap C025's handoff records as (b).
        var ttl = await db.KeyTimeToLiveAsync(RedisKeys.DriverAvailability(driverId));

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

        // `cell` on the hash is what dispatch-svc's own removal reads back to find the GEO key, so
        // moving a driver without updating it would leave them in the pool for ever.
        Assert.Equal(cell, await db.HashGetAsync(RedisKeys.DriverAvailability(driverId), "cell"));

        // …and `lastSeen` is the sample's GNSS instant, not the receive time: D5' §3.2's freshness
        // rule is about how old the *fix* is.
        Assert.False((await db.HashGetAsync(RedisKeys.DriverAvailability(driverId), "lastSeen")).IsNullOrEmpty);
    }

    [Fact]
    public async Task A_driver_who_crosses_a_res_5_boundary_is_moved_rather_than_duplicated()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (driverId, vehicleId) = await GoOnlineAsync(db);

        var captured = DateTimeOffset.UtcNow.AddMinutes(-20);

        await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1, at: captured);

        // Moratuwa is ~18.5 km south and the first well-known point in a different res-5 cell —
        // Dehiwala, nine kilometres out, is still in Colombo Fort's. Twenty minutes later, so the
        // step is 55 km/h and a drive rather than a teleport.
        var moved = await ProcessAsync(
            connection, vehicleId, Samples.Moratuwa, seq: 2, at: captured.AddMinutes(20));

        Assert.Equal(PoolChange.Indexed, moved.Pool);

        var from = GeoCells.DispatchCell(Samples.ColomboFort);
        var to = GeoCells.DispatchCell(Samples.Moratuwa);

        Assert.NotEqual(from, to);

        // A GEOADD to the new key without a GEOREM from the old one leaves the driver discoverable
        // from two places at once, one of which is now a lie about where they are.
        Assert.Null(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers(VehicleType, from), driverId.ToString()));

        Assert.NotNull(await db.SortedSetScoreAsync(
            RedisKeys.AvailableDrivers(VehicleType, to), driverId.ToString()));
    }

    [Fact]
    public async Task A_driver_who_stays_in_their_cell_is_refreshed_rather_than_re_indexed()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var (_, vehicleId) = await GoOnlineAsync(connection.GetDatabase());

        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);

        await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1, at: captured);

        // A driver waiting at a rank is the candidate dispatch most wants to keep. Their liveness
        // advances; their membership is not rewritten.
        var again = await ProcessAsync(
            connection, vehicleId, NextStreet, seq: 2, at: captured.AddSeconds(45));

        Assert.Equal(PoolChange.Refreshed, again.Pool);
    }

    /// <summary>The DoD's second line: a phase transition takes effect within one interval.</summary>
    [Fact]
    public async Task A_driver_who_is_no_longer_AVAILABLE_leaves_the_pool_on_the_next_sample()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (driverId, vehicleId) = await GoOnlineAsync(db);

        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);
        await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1, at: captured);

        var cell = RedisKeys.AvailableDrivers(VehicleType, GeoCells.DispatchCell(Samples.ColomboFort));
        Assert.NotNull(await db.SortedSetScoreAsync(cell, driverId.ToString()));

        // dispatch-svc's phase transition: the driver has been offered a ride. Only the hash is
        // written here, because that is exactly what RemoveFromPoolAsync does before this service
        // sees another sample — and the point is that this service agrees with it either way.
        await db.HashSetAsync(RedisKeys.DriverAvailability(driverId), "state", "OFFERED");

        var offered = await ProcessAsync(
            connection, vehicleId, NextStreet, seq: 2, at: captured.AddSeconds(30));

        Assert.Equal(PoolChange.Removed, offered.Pool);

        // Out of the pool, so they are not offered a second ride while holding the first…
        Assert.Null(await db.SortedSetScoreAsync(cell, driverId.ToString()));

        // …but still kept fresh: they are coming back to the pool, and their `last_seen_at` is what
        // decides whether they are a candidate when they do.
        Assert.InRange(
            (await db.KeyTimeToLiveAsync(RedisKeys.DriverAvailability(driverId)))!.Value,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));
    }

    /// <summary>The other half of the DoD's second line: offline, within one interval.</summary>
    [Fact]
    public async Task A_driver_whose_availability_lapsed_is_taken_out_of_the_pool()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (driverId, vehicleId) = await GoOnlineAsync(db);

        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);
        await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1, at: captured);

        var cell = RedisKeys.AvailableDrivers(VehicleType, GeoCells.DispatchCell(Samples.ColomboFort));
        Assert.NotNull(await db.SortedSetScoreAsync(cell, driverId.ToString()));

        // What a lapsed 60 s TTL leaves behind — and a GEO set has no TTL of its own, so without
        // this reconciliation the membership outlives the driver's shift and every one after it.
        await db.KeyDeleteAsync(RedisKeys.DriverAvailability(driverId));

        var after = await ProcessAsync(
            connection, vehicleId, NextStreet, seq: 2, at: captured.AddSeconds(30));

        Assert.Equal(PoolChange.Removed, after.Pool);
        Assert.Null(await db.SortedSetScoreAsync(cell, driverId.ToString()));

        // And nothing was resurrected: an HSET on an expired key would recreate the hash with one
        // field and no TTL, which reads to dispatch as "online, position unknown" for ever.
        Assert.False(await db.KeyExistsAsync(RedisKeys.DriverAvailability(driverId)));
    }

    [Fact]
    public async Task A_driver_who_went_offline_is_taken_out_even_though_the_vehicle_keeps_publishing()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (driverId, vehicleId) = await GoOnlineAsync(db);

        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);
        await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1, at: captured);

        var cell = RedisKeys.AvailableDrivers(VehicleType, GeoCells.DispatchCell(Samples.ColomboFort));

        // What dispatch-svc's ForgetAsync leaves: no binding, no hash. The vehicle keeps publishing
        // regardless — a Mode A tracker never stops, and a driver-app handset publishes until the
        // app is closed — so the membership has to be cleaned up from this side.
        await db.KeyDeleteAsync(RedisKeys.VehicleDriver(vehicleId));
        await db.KeyDeleteAsync(RedisKeys.DriverAvailability(driverId));

        var after = await ProcessAsync(
            connection, vehicleId, NextStreet, seq: 2, at: captured.AddSeconds(30));

        Assert.Equal(PoolChange.Removed, after.Pool);
        Assert.Null(await db.SortedSetScoreAsync(cell, driverId.ToString()));
    }

    [Fact]
    public async Task A_vehicle_with_no_driver_on_standby_touches_nothing()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var vehicleId = Guid.NewGuid();

        // The ordinary case by a wide margin: telemetry.raw carries every Mode A bus and every
        // Mode B shared vehicle on the platform, and dispatch has a presence row for a fraction.
        var result = await ProcessAsync(connection, vehicleId, Samples.ColomboFort, seq: 1);

        Assert.Equal(PositionOutcome.Indexed, result.Outcome);
        Assert.Equal(PoolChange.NoDriver, result.Pool);
    }

    [Fact]
    public async Task A_replayed_sample_never_refreshes_presence()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var db = connection.GetDatabase();
        var (driverId, vehicleId) = await GoOnlineAsync(db);

        await db.KeyExpireAsync(RedisKeys.DriverAvailability(driverId), TimeSpan.FromSeconds(5));

        // R-17's backlog arrives with a fresh receive time and a stale capture time. Refreshing
        // presence from it would advertise a driver as available where they were an hour ago —
        // exactly what D5' §3.2's freshness gate exists to refuse.
        var replayed = await ProcessAsync(
            connection, vehicleId, Samples.ColomboFort, seq: 1,
            at: DateTimeOffset.UtcNow.AddHours(-1), stream: TelemetryHeaders.Replay);

        Assert.Equal(PositionOutcome.Indexed, replayed.Outcome);
        Assert.Equal(PoolChange.NoDriver, replayed.Pool);

        var ttl = await db.KeyTimeToLiveAsync(RedisKeys.DriverAvailability(driverId));

        Assert.NotNull(ttl);
        Assert.True(ttl.Value <= TimeSpan.FromSeconds(5), $"the backlog refreshed the TTL to {ttl}");
    }

    /// <summary>
    /// The one string two services have to agree on and neither can see the other declare.
    /// </summary>
    /// <remarks>
    /// position-processor-svc must not reference Dispatch.Api — it is a hot-path service with no
    /// database, and the dependency would be the wrong way round. So the constant is spelled twice,
    /// and this is where a divergence fails: without it, the two would disagree in production as an
    /// empty candidate set, which looks exactly like "nobody is online".
    /// </remarks>
    [Fact]
    public void A_driver_who_is_AVAILABLE_to_dispatch_is_AVAILABLE_here() =>
        Assert.Equal("AVAILABLE", DriverAvailabilityIndex.AvailableState);

    /// <summary>
    /// The other half of that agreement: the res-5 resolution the pool is keyed at.
    /// </summary>
    /// <remarks>
    /// dispatch-svc builds its grid from <c>Dispatch:H3Resolution</c> (default 5) and this service
    /// from <c>GeoCells.DispatchResolution</c>. A driver indexed at one resolution and looked for at
    /// another is a key nobody reads.
    /// </remarks>
    [Fact]
    public void The_pool_is_keyed_at_the_res_5_cell_dispatch_pre_filters_on() =>
        Assert.Equal(
            new H3Grid(5, 2).CellAt(Samples.ColomboFort), GeoCells.DispatchCell(Samples.ColomboFort));

    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes what dispatch-svc's <c>IndexAvailableAsync</c> writes when a driver goes on standby.
    /// </summary>
    /// <remarks>
    /// Not a stub of dispatch-svc — it is the actual keyspace state, spelled through the same
    /// <see cref="RedisKeys"/> both services use. The GEO membership is deliberately <b>not</b>
    /// written: this service is what puts a driver in a cell from a position, and seeding it would
    /// make the "adds" half of the DoD assert nothing.
    /// </remarks>
    private static async Task<(Guid DriverId, Guid VehicleId)> GoOnlineAsync(IDatabase db)
    {
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();

        await db.HashSetAsync(RedisKeys.DriverAvailability(driverId),
        [
            new HashEntry("state", DriverAvailabilityIndex.AvailableState),
            new HashEntry("lastSeen", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            new HashEntry("vehicleType", VehicleType),
            new HashEntry("vehicleId", vehicleId.ToString()),
            new HashEntry("level", 3),
            new HashEntry("walletOk", true),
        ]);

        await db.KeyExpireAsync(RedisKeys.DriverAvailability(driverId), TimeSpan.FromSeconds(60));
        await db.StringSetAsync(RedisKeys.VehicleDriver(vehicleId), driverId.ToString());

        return (driverId, vehicleId);
    }

    private static Task<PositionResult> ProcessAsync(
        IConnectionMultiplexer connection,
        Guid vehicleId,
        GeoPoint point,
        long seq,
        DateTimeOffset? at = null,
        string stream = Live)
    {
        var processor = ProcessorParts.Build(
            connection, options: new PositionProcessorOptions { PublishNormalized = false });

        var sample = Samples.At(
            vehicleId, point, seq, vehicleType: VehicleType, sampleTs: at ?? DateTimeOffset.UtcNow);

        return processor.ProcessAsync(
            PositionSampleCodec.Encode(sample), vehicleId, stream, TestContext.Current.CancellationToken);
    }

    private Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
}

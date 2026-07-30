using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.Shared.Caching;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Query.Tests.Infrastructure;

/// <summary>
/// Puts a vehicle on the live index by running the <b>real</b> position-processor-svc writer.
/// </summary>
/// <remarks>
/// <para>
/// <c>geo:live</c> and <c>veh:meta:{vehicleId}</c> are C039's, and their field names are the contract
/// between that service and this one — neither may reference the other in production, so a rename there
/// would leave a hand-written copy of the names in this suite green while every passenger's map went
/// quietly empty. Referencing <see cref="LivePositionIndex"/> from the <em>test</em> project closes that
/// hole: the same code writes here as writes in the pipeline, and a rename breaks this build.
/// </para>
/// <para>
/// The R-08 candidate index is deliberately not exercised (<c>pool: null</c>): it is dispatch's, and
/// nothing query-svc reads touches it.
/// </para>
/// </remarks>
internal sealed class PositionWriter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly LivePositionIndex _index;

    internal PositionWriter(IConnectionMultiplexer redis)
    {
        _redis = redis;

        _index = new LivePositionIndex(
            redis,
            Options.Create(new PositionProcessorOptions()),
            NullLogger<LivePositionIndex>.Instance);
    }

    /// <summary>Records an accepted fix, exactly as the processor would.</summary>
    /// <param name="vehicleId">The publishing vehicle.</param>
    /// <param name="point">Where it is.</param>
    /// <param name="mode"><c>A</c>, <c>B</c> or <c>C</c> — the visibility rule's input.</param>
    /// <param name="vehicleType">Canonical type (AL-09).</param>
    /// <param name="sampleTs">
    /// The GNSS instant. Backdating it past the freshness window is how a test produces US-7.17's
    /// stale case without waiting a minute.
    /// </param>
    /// <param name="seq">The R-17 sequence. Must increase per vehicle or the write is a no-op.</param>
    internal async Task PublishAsync(
        Guid vehicleId,
        GeoPoint point,
        string mode = "C",
        string vehicleType = "three_wheeler",
        DateTimeOffset? sampleTs = null,
        long seq = 1,
        double? speedMps = 8.5,
        int? headingDeg = 90)
    {
        var sample = new PositionSample(
            vehicleId,
            sampleTs ?? DateTimeOffset.UtcNow,
            seq,
            point.Latitude,
            point.Longitude,
            PositionSource.Mobile,
            SpeedMps: speedMps,
            HeadingDeg: headingDeg,
            Mode: mode,
            VehicleType: vehicleType);

        var cell = await _index.RecordAsync(sample, pool: null, CancellationToken.None);

        Assert.NotNull(cell);
    }

    /// <summary>
    /// Puts a vehicle in <c>geo:live</c> at one place and leaves <c>veh:meta</c> saying another —
    /// or nothing at all.
    /// </summary>
    /// <remarks>
    /// The state the platform actually reaches, because <c>geo:live</c> has no per-member expiry and
    /// nothing ever removes a member: the GEO index keeps every vehicle that has ever published, at the
    /// place it stopped, while its <c>veh:meta</c> hash ages out after ten minutes. This is how a test
    /// produces that divergence deliberately.
    /// </remarks>
    internal async Task StrandInGeoIndexAsync(Guid vehicleId, GeoPoint stalePosition)
    {
        var db = _redis.GetDatabase();

        await db.GeoAddAsync(
            RedisKeys.GeoLive,
            new GeoEntry(stalePosition.Longitude, stalePosition.Latitude, vehicleId.ToString()));

        await db.KeyDeleteAsync(RedisKeys.VehicleMeta(vehicleId));
    }

    /// <summary>Marks a vehicle engaged on a ride, as fanout-svc does from <c>ride.events</c> (US-7.16).</summary>
    internal Task EngageAsync(Guid vehicleId, Guid rideId) =>
        _redis.GetDatabase().StringSetAsync(
            RedisKeys.VehicleEngagement(vehicleId), rideId.ToString(), TimeSpan.FromHours(1));

    /// <summary>Marks a vehicle offline, as fanout-svc does from the EMQX last will (US-7.17).</summary>
    internal Task MarkOfflineAsync(Guid vehicleId, DateTimeOffset at) =>
        _redis.GetDatabase().StringSetAsync(
            RedisKeys.VehicleOfflineAt(vehicleId),
            at.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.FromHours(1));

    /// <summary>Grants a passenger Mode B visibility of a vehicle, as fanout-svc does (D-23).</summary>
    internal Task ShareAsync(Guid userId, Guid vehicleId) =>
        _redis.GetDatabase().SetAddAsync(RedisKeys.Share(userId), vehicleId.ToString());
}

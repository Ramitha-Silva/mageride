using System.Globalization;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using StackExchange.Redis;

namespace MageRide.Fanout.Tests.Infrastructure;

/// <summary>
/// Writes the two Redis structures position-processor-svc writes — the <c>cell:{h3index}</c> stream
/// and <c>veh:meta:{vehicleId}</c> — so a test can put a vehicle somewhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>The field names here are a copy, and that is a real risk this suite does not carry alone.</b>
/// position-processor-svc's <c>CellStreamFields</c> and <c>MetaFields</c> are the contract between
/// the two services and neither may reference the other, so a rename on that side would leave this
/// suite green and the platform silent. What catches it is <c>HotPath.Tests</c>, where a <em>real</em>
/// processor writes the stream a <em>real</em> fanout-svc reads — that is the assertion those names
/// live under. Standing the whole ingest pipeline up again here would prove the same thing twice and
/// cost two more containers, while making every visibility test depend on a broker.
/// </para>
/// <para>
/// What this does <b>not</b> do is decide any of it. The mode, the timestamp and the position are
/// the test's; whether the resulting frame reaches anybody is what is under test.
/// </para>
/// </remarks>
internal sealed class PositionWriter(IConnectionMultiplexer redis)
{
    /// <summary>Puts a vehicle at <paramref name="point"/>, as an accepted sample would.</summary>
    /// <param name="mode"><c>A</c>, <c>B</c> or <c>C</c> — the visibility rule's input.</param>
    /// <param name="sampleTs">
    /// The GNSS instant. Backdating it past the freshness window is how a test produces the
    /// "replayed backlog" case without a broker.
    /// </param>
    public async Task<string> PublishAsync(
        Guid vehicleId,
        GeoPoint point,
        string mode = "C",
        string vehicleType = "three_wheeler",
        DateTimeOffset? sampleTs = null,
        long seq = 1)
    {
        var db = redis.GetDatabase();
        var cell = GeoCells.ViewCell(point);
        var stamped = (sampleTs ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);

        await db.StreamAddAsync(
            RedisKeys.Cell(cell),
            [
                new NameValueEntry("vehicleId", vehicleId.ToString()),
                new NameValueEntry("lat", point.Latitude),
                new NameValueEntry("lng", point.Longitude),
                new NameValueEntry("seq", seq),
                new NameValueEntry("sampleTs", stamped),
                new NameValueEntry("heading", 90),
                new NameValueEntry("speed", 8.5),
                new NameValueEntry("type", vehicleType),
                new NameValueEntry("mode", mode),
            ],
            messageId: null,
            maxLength: 1_000,
            useApproximateMaxLength: true);

        await db.HashSetAsync(
            RedisKeys.VehicleMeta(vehicleId),
            [
                new HashEntry("cell", cell),
                new HashEntry("lat", point.Latitude),
                new HashEntry("lng", point.Longitude),
                new HashEntry("seq", seq),
                new HashEntry("sampleTs", stamped),
                new HashEntry("heading", 90),
                new HashEntry("speed", 8.5),
                new HashEntry("type", vehicleType),
                new HashEntry("mode", mode),
            ]);

        await db.KeyExpireAsync(RedisKeys.VehicleMeta(vehicleId), TimeSpan.FromMinutes(10));

        return cell;
    }

    /// <summary>registry-svc's published go-live selection, which AL-31's own-vehicle map reads.</summary>
    public Task SelectLiveVehicleAsync(Guid driverId, Guid vehicleId) =>
        redis.GetDatabase().StringSetAsync(RedisKeys.DriverLiveVehicle(driverId), vehicleId.ToString());
}

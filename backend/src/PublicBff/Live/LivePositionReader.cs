using System.Globalization;
using MageRide.Shared.Caching;
using StackExchange.Redis;

namespace MageRide.PublicBff.Live;

/// <summary>Where a vehicle is right now, and nothing about where it has been.</summary>
public sealed record LivePosition(double Lat, double Lng, DateTimeOffset SampledAt, double? SpeedMps);

/// <summary>
/// The one position an SCR-WT page may draw.
/// </summary>
/// <remarks>
/// <para>
/// <b>The no-replay fence is the store, not the query.</b> The source is position-processor-svc's
/// <c>veh:meta:{vehicleId}</c> hash, which holds exactly one fix per vehicle and overwrites it —
/// there is no history in it to leak. The alternative, <c>telemetry.positions</c>, is the whole
/// track: a query against it with the wrong <c>LIMIT</c>, or a later "draw the route so far" change,
/// would turn a tracking link into the historical replay D-34 forbids and no reviewer would
/// necessarily notice. safety-svc's public view is built on the same reasoning and the same key.
/// </para>
/// <para>
/// The field names are position-processor-svc's; the two services cannot reference each other, so
/// the names <em>are</em> the contract — the arrangement query-svc's <c>LiveVehicleIndex</c>,
/// fanout-svc's <c>VehicleSnapshotReader</c> and safety-svc's reader are all under.
/// </para>
/// </remarks>
public interface ILivePositionReader
{
    Task<LivePosition?> ReadAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILivePositionReader"/>
internal sealed class LivePositionReader(IConnectionMultiplexer redis) : ILivePositionReader
{
    private static readonly RedisValue[] Fields = ["lat", "lng", "speed", "sampleTs"];

    public async Task<LivePosition?> ReadAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await redis.GetDatabase().HashGetAsync(RedisKeys.VehicleMeta(vehicleId), Fields);

        // No readable position: the hash aged out, or the vehicle has never published. Omitted
        // rather than approximated — a marker at a coordinate of unknown age is what US-7.17 takes
        // off the public map, and a tracking link is where it misleads most.
        if (!TryDouble(values[0], out var lat) || !TryDouble(values[1], out var lng))
        {
            return null;
        }

        return new LivePosition(
            lat,
            lng,
            ReadInstant(values[3]),
            TryDouble(values[2], out var speed) ? speed : null);
    }

    private static bool TryDouble(RedisValue value, out double parsed)
    {
        parsed = 0;

        return !value.IsNullOrEmpty
               && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static DateTimeOffset ReadInstant(RedisValue value) =>
        !value.IsNullOrEmpty
        && DateTimeOffset.TryParse(
            value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : DateTimeOffset.MinValue;
}

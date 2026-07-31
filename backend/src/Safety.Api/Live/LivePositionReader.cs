using System.Globalization;
using MageRide.Shared.Caching;
using StackExchange.Redis;

namespace MageRide.Safety.Live;

/// <summary>Where a vehicle is right now, and nothing about where it has been.</summary>
public sealed record LivePosition(double Lat, double Lng, int? Heading, double? SpeedMps, DateTimeOffset SampledAt);

/// <summary>
/// The one position a shared link may show (D-34).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is how "no historical replay" is held structurally rather than by care.</b> The source is
/// position-processor-svc's <c>veh:meta:{vehicleId}</c> Redis hash, which holds exactly one fix per
/// vehicle and overwrites it — there is no history in it to leak. The alternative,
/// <c>telemetry.positions</c>, is the full track: a query against it with a wrong <c>LIMIT</c> or a
/// later "add a trail to the map" change would turn a share link into the replay D-34 forbids, and
/// no reviewer would necessarily notice. Reading a store that cannot answer the question is a
/// stronger fence than remembering not to ask it.
/// </para>
/// <para>
/// The field names are position-processor-svc's; the two services cannot reference each other, so
/// the names <em>are</em> the contract — the same arrangement query-svc's <c>LiveVehicleIndex</c> and
/// fanout-svc's <c>VehicleSnapshotReader</c> are under, and asserted the same way against a hash a
/// real processor wrote.
/// </para>
/// </remarks>
public interface ILivePositionReader
{
    Task<LivePosition?> ReadAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILivePositionReader"/>
internal sealed class LivePositionReader(IConnectionMultiplexer redis) : ILivePositionReader
{
    private static readonly RedisValue[] Fields = ["lat", "lng", "heading", "speed", "sampleTs"];

    public async Task<LivePosition?> ReadAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await redis.GetDatabase().HashGetAsync(RedisKeys.VehicleMeta(vehicleId), Fields);

        // No readable position: the hash aged out, or the vehicle has never published. Omitted
        // rather than approximated — a marker at a coordinate of unknown age is exactly what
        // US-7.17 removes from the public map, and a shared link is where it misleads most.
        if (!TryDouble(values[0], out var lat) || !TryDouble(values[1], out var lng))
        {
            return null;
        }

        return new LivePosition(
            lat,
            lng,
            TryDouble(values[2], out var heading) ? (int)Math.Round(heading) : null,
            TryDouble(values[3], out var speed) ? speed : null,
            ReadInstant(values[4]));
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

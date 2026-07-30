using System.Globalization;
using MageRide.Shared.Caching;
using MageRide.Shared.Realtime;
using StackExchange.Redis;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Where a vehicle is now, read from the hash position-processor-svc keeps for it.
/// </summary>
/// <param name="Frame">The wire payload.</param>
/// <param name="SampleTs">The sample's GNSS instant, for the freshness rule.</param>
/// <param name="Cell">The res-7 cell the last accepted sample fell in.</param>
public sealed record VehicleSnapshot(VehicleFrame Frame, DateTimeOffset? SampleTs, string? Cell);

/// <summary>
/// Reads <c>veh:meta:{vehicleId}</c> — the per-vehicle current position, for the audiences that
/// follow a <i>vehicle</i> rather than a place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the cell streams.</b> A cell stream only reaches a replica that has a subscriber in
/// that cell, and the three audiences served from here are not in a cell at all: the passenger on a
/// ride is watching a car, not a map square; the Mode B watcher is following one shared vehicle
/// wherever it goes; the driver's home map shows their own vehicle. Driving those from cell
/// membership would mean a passenger stops receiving their own driver's position the moment the car
/// leaves the nineteen cells the app happened to subscribe to — which is exactly what happens on a
/// long ride.
/// </para>
/// <para>
/// <b>The field names are position-processor-svc's <c>MetaFields</c>.</b> The two services cannot
/// reference each other, so the names are the contract, and a test asserts them against a hash a
/// real processor wrote.
/// </para>
/// </remarks>
public interface IVehicleSnapshotReader
{
    /// <summary>Reads several vehicles in one round trip. Absent vehicles are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, VehicleSnapshot>> ReadAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleSnapshotReader"/>
public sealed class VehicleSnapshotReader(IConnectionMultiplexer redis) : IVehicleSnapshotReader
{
    private static readonly RedisValue[] Fields =
    [
        "lat", "lng", "heading", "speed", "type", "mode", "sampleTs", "cell",
    ];

    public async Task<IReadOnlyDictionary<Guid, VehicleSnapshot>> ReadAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = new Dictionary<Guid, VehicleSnapshot>(vehicleIds.Count);

        if (vehicleIds.Count == 0)
        {
            return snapshots;
        }

        var batch = redis.GetDatabase().CreateBatch();
        var pending = new List<(Guid VehicleId, Task<RedisValue[]> Values)>(vehicleIds.Count);

        foreach (var vehicleId in vehicleIds)
        {
            pending.Add((vehicleId, batch.HashGetAsync(RedisKeys.VehicleMeta(vehicleId), Fields)));
        }

        batch.Execute();

        foreach (var (vehicleId, task) in pending)
        {
            var values = await task;

            // No position at all: the vehicle has never published, or its meta hash aged out.
            // Silence is the honest answer — the pump's freshness sweep is what turns a vehicle that
            // was on the map into a VehicleRemoved.
            if (!TryReadDouble(values[0], out var lat) || !TryReadDouble(values[1], out var lng))
            {
                continue;
            }

            snapshots[vehicleId] = new VehicleSnapshot(
                new VehicleFrame(
                    vehicleId,
                    lat,
                    lng,
                    TryReadDouble(values[2], out var heading) ? (int)heading : null,
                    TryReadDouble(values[3], out var speed) ? speed : null,
                    values[4].IsNullOrEmpty ? null : values[4].ToString(),
                    values[5].IsNullOrEmpty ? null : values[5].ToString()),
                ReadInstant(values[6]),
                values[7].IsNullOrEmpty ? null : values[7].ToString());
        }

        return snapshots;
    }

    private static bool TryReadDouble(RedisValue value, out double parsed) =>
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

    private static DateTimeOffset? ReadInstant(RedisValue value) =>
        !value.IsNullOrEmpty
        && DateTimeOffset.TryParse(
            value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

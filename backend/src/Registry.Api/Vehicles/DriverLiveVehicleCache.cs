using MageRide.Shared.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MageRide.Registry.Vehicles;

/// <summary>
/// Publishes the driver's currently selected vehicle into <c>lock:driver:{driverId}</c> so the
/// two downstream planes agree with the registry about which one it is (D-03, US-9.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not what makes the invariant true.</b> "Only one vehicle can go live at a time" is
/// <c>registry.driver_profiles.active_vehicle_id</c> — a single column on a row whose primary key
/// is the driver — so selecting a second vehicle overwrites the first in one statement and there
/// is no window in which two are selected. D-03's two enforcement points
/// (<c>ux_sessions_active_driver</c> on the tracking plane, <c>dispatch.driver_presence</c> on
/// the dispatch plane) are both downstream of that choice and need to know what it was.
/// </para>
/// <para>
/// <b>Best effort, deliberately.</b> Postgres is the record, so a Redis outage costs a cache and
/// not a driver's shift — the same call the session mirror makes in iam-svc. A selection that
/// committed and then failed to publish is still the selection; a consumer that misses the key
/// falls back to reading the registry.
/// </para>
/// </remarks>
public interface IDriverLiveVehicleCache
{
    /// <summary>Records the selection. Overwrites whatever vehicle was there.</summary>
    Task PublishAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Clears the selection — deactivating the selected vehicle, or going off duty.</summary>
    Task ClearAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>The published selection, or <see langword="null"/> if there is none or Redis is unreachable.</summary>
    Task<Guid?> ReadAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverLiveVehicleCache"/>
public sealed class DriverLiveVehicleCache(
    IConnectionMultiplexer redis, ILogger<DriverLiveVehicleCache> logger) : IDriverLiveVehicleCache
{
    /// <summary>
    /// How long the published selection survives without being refreshed.
    /// </summary>
    /// <remarks>
    /// No spec fixes it. A day is long enough to cover any shift and short enough that a driver
    /// who has not opened the app since last week leaves nothing behind for a consumer to trust.
    /// The key is rewritten on every selection, so an active driver's never expires.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Task PublishAsync(Guid driverId, Guid vehicleId, CancellationToken cancellationToken) =>
        TryAsync(
            database => database.StringSetAsync(RedisKeys.DriverLiveVehicle(driverId), vehicleId.ToString(), Lifetime),
            driverId,
            "publish the live-vehicle selection for",
            cancellationToken);

    public Task ClearAsync(Guid driverId, CancellationToken cancellationToken) =>
        TryAsync(
            database => database.KeyDeleteAsync(RedisKeys.DriverLiveVehicle(driverId)),
            driverId,
            "clear the live-vehicle selection for",
            cancellationToken);

    public async Task<Guid?> ReadAsync(Guid driverId, CancellationToken cancellationToken)
    {
        try
        {
            var value = await redis.GetDatabase()
                .StringGetAsync(RedisKeys.DriverLiveVehicle(driverId))
                .WaitAsync(cancellationToken);

            return Guid.TryParse(value.ToString(), out var vehicleId) ? vehicleId : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Could not read the live-vehicle selection for {DriverId}; registry remains authoritative", driverId);
            return null;
        }
    }

    private async Task TryAsync(
        Func<IDatabase, Task> operation, Guid driverId, string what, CancellationToken cancellationToken)
    {
        try
        {
            await operation(redis.GetDatabase()).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Could not {What} {DriverId}; registry.driver_profiles remains authoritative", what, driverId);
        }
    }
}

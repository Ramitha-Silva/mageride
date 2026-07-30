using MageRide.Shared.Caching;
using StackExchange.Redis;

namespace MageRide.Fanout.Visibility;

/// <summary>
/// Which vehicle a driver has gone live on — <c>lock:driver:{driverId}</c>, registry-svc's published
/// go-live selection (D-03, US-9.6).
/// </summary>
/// <remarks>
/// <para>
/// Read here for AL-31 alone: the driver home map renders <b>the driver's own active vehicle only</b>,
/// and the server is what decides which vehicle that is. Taking it from the client would make the
/// fence a request parameter — a driver app could ask for any vehicle id and be served it, which is
/// precisely "other drivers' active vehicles rendered on the driver home map".
/// </para>
/// <para>
/// A miss means the driver has selected nothing and their home map is empty, which is what the
/// screen shows before go-live anyway. registry-svc writes the key best-effort after COMMIT, so a
/// miss can also mean a Redis blip during selection; the driver's next selection repairs it.
/// </para>
/// </remarks>
public interface IDriverVehicles
{
    /// <summary>The vehicle this driver is live on, or <see langword="null"/>.</summary>
    Task<Guid?> ActiveVehicleOfAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverVehicles"/>
public sealed class DriverVehicles(IConnectionMultiplexer redis) : IDriverVehicles
{
    public async Task<Guid?> ActiveVehicleOfAsync(Guid driverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var value = await redis.GetDatabase().StringGetAsync(RedisKeys.DriverLiveVehicle(driverId));

        return !value.IsNullOrEmpty && Guid.TryParse(value.ToString(), out var vehicleId) ? vehicleId : null;
    }
}

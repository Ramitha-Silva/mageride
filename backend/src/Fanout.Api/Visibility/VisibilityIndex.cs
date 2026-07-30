using System.Globalization;
using MageRide.Fanout.Configuration;
using MageRide.Shared.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Fanout.Visibility;

/// <summary>
/// The two per-vehicle facts the filter needs and a position sample does not carry: the active hire
/// (<c>veh:engaged:{vehicleId}</c>, US-7.16) and the last will (<c>veh:offline:{vehicleId}</c>,
/// US-7.17).
/// </summary>
/// <remarks>
/// Both live in Redis rather than in this process because a ride is accepted on whichever replica
/// happens to consume <c>ride.events</c> and a last will lands on every replica holding the
/// subscription, while the frame it has to be applied to arrives on whichever replica is reading
/// that cell. The control channel makes the change <em>fast</em> everywhere; these keys are what
/// make it <em>true</em> for a replica that started afterwards.
/// </remarks>
public interface IVisibilityIndex
{
    /// <summary>Reads the state of several vehicles in one round trip.</summary>
    Task<IReadOnlyDictionary<Guid, VehicleState>> ReadAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);

    /// <summary>Reads one vehicle's state.</summary>
    Task<VehicleState> ReadOneAsync(Guid vehicleId, CancellationToken cancellationToken);

    /// <summary>Marks a Mode C vehicle as on hire — it leaves the public groups (US-7.16).</summary>
    Task EngageAsync(Guid vehicleId, Guid rideId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the hire on a terminal, but only if <paramref name="rideId"/> is the hire recorded.
    /// </summary>
    /// <remarks>
    /// <b>Conditional, because <c>ride.events</c> is partitioned by <c>rideId</c> and two rides are
    /// two partitions.</b> A vehicle accepted on ride A can be told about ride B's expired offer
    /// afterwards even though it happened first — nothing orders events about different rides — and
    /// an unconditional delete there would put a vehicle carrying a passenger back on the public map
    /// for the rest of the trip, which is exactly what US-7.16 forbids.
    /// </remarks>
    Task ReleaseAsync(Guid vehicleId, Guid rideId, CancellationToken cancellationToken);

    /// <summary>Records an EMQX <c>offline</c> last will (R-15, T-04).</summary>
    Task MarkOfflineAsync(Guid vehicleId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Clears the last will on an explicit <c>online</c>.</summary>
    Task MarkOnlineAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVisibilityIndex"/>
public sealed class VisibilityIndex(IConnectionMultiplexer redis, IOptions<FanoutOptions> options) : IVisibilityIndex
{
    /// <summary>
    /// How long an <c>offline</c> mark survives with nothing else happening to the vehicle.
    /// </summary>
    /// <remarks>
    /// Not a knob: the mark is superseded by any fresher sample and cleared by an <c>online</c>, so
    /// the expiry only decides how long a permanently dead vehicle's key occupies the keyspace. A
    /// week is well past any window in which a stale mark could matter and short enough that a fleet
    /// that churns devices does not accumulate them for ever.
    /// </remarks>
    private static readonly TimeSpan OfflineMarkTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// Compare-and-delete: release the vehicle only if it is <em>this</em> ride that holds it.
    /// </summary>
    /// <remarks>
    /// A read-then-delete would have the same race it is written to close — two replicas consuming
    /// two rides' terminals can interleave between the two calls.
    ///
    /// KEYS[1] = veh:engaged:{vehicleId}   ARGV[1] = rideId
    /// </remarks>
    private const string ReleaseScript =
        """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyDictionary<Guid, VehicleState>> ReadAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);
        cancellationToken.ThrowIfCancellationRequested();

        if (vehicleIds.Count == 0)
        {
            return new Dictionary<Guid, VehicleState>();
        }

        var db = redis.GetDatabase();

        // One pipelined batch, not one round trip per vehicle: a busy cell carries dozens of
        // vehicles per tick and the filter runs before anything is sent, so its latency is on the
        // SLO's critical path.
        var batch = db.CreateBatch();
        var pending = new List<(Guid VehicleId, Task<RedisValue> Engaged, Task<RedisValue> Offline)>(vehicleIds.Count);

        foreach (var vehicleId in vehicleIds)
        {
            pending.Add((
                vehicleId,
                batch.StringGetAsync(RedisKeys.VehicleEngagement(vehicleId)),
                batch.StringGetAsync(RedisKeys.VehicleOfflineAt(vehicleId))));
        }

        batch.Execute();

        var states = new Dictionary<Guid, VehicleState>(pending.Count);

        foreach (var (vehicleId, engaged, offline) in pending)
        {
            states[vehicleId] = new VehicleState(ParseRide(await engaged), ParseInstant(await offline));
        }

        return states;
    }

    public async Task<VehicleState> ReadOneAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var states = await ReadAsync([vehicleId], cancellationToken);

        return states.TryGetValue(vehicleId, out var state) ? state : VehicleState.Unknown;
    }

    public async Task EngageAsync(Guid vehicleId, Guid rideId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await redis.GetDatabase().StringSetAsync(
            RedisKeys.VehicleEngagement(vehicleId), rideId.ToString(), _options.EngagementTtl);
    }

    public async Task ReleaseAsync(Guid vehicleId, Guid rideId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await redis.GetDatabase().ScriptEvaluateAsync(
            ReleaseScript, [RedisKeys.VehicleEngagement(vehicleId)], [rideId.ToString()]);
    }

    public async Task MarkOfflineAsync(Guid vehicleId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await redis.GetDatabase().StringSetAsync(
            RedisKeys.VehicleOfflineAt(vehicleId), at.ToString("O", CultureInfo.InvariantCulture), OfflineMarkTtl);
    }

    public async Task MarkOnlineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await redis.GetDatabase().KeyDeleteAsync(RedisKeys.VehicleOfflineAt(vehicleId));
    }

    private static Guid? ParseRide(RedisValue value) =>
        !value.IsNullOrEmpty && Guid.TryParse(value.ToString(), out var rideId) ? rideId : null;

    private static DateTimeOffset? ParseInstant(RedisValue value) =>
        !value.IsNullOrEmpty
        && DateTimeOffset.TryParse(
            value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

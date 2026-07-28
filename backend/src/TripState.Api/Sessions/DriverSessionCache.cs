using MageRide.Shared.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MageRide.TripState.Sessions;

/// <summary>
/// Publishes which session a driver holds, on <c>lock:session:{driverId}</c> (D-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>A published fact, not the lock — despite the key's prefix.</b> ADD §6 describes the
/// active-session mutex as "Redis <c>lock:driver:{driverId}</c> SETNX <b>+</b> Postgres UNIQUE
/// partial index", and only one of those two can be the invariant. It is the index:
/// <c>ux_sessions_active_driver</c> settles ten concurrent starts with no cooperation from anyone,
/// survives a Redis flush, and cannot be bypassed by a caller that reaches Postgres another way.
/// Treating Redis as the authority would mean an evicted key silently permits a second live
/// session — the exact failure D-03 exists to prevent.
/// </para>
/// <para>
/// So every operation here is <b>best effort and after COMMIT</b>. The value is what the dispatch
/// and tracking planes read to learn which session a vehicle's positions belong to without
/// querying this service on the hot path; losing it costs them a lookup, not correctness. This is
/// the same reasoning C028 records for <c>lock:driver:{driverId}</c>, and the two keys are
/// deliberately different — see <see cref="RedisKeys.DriverSession"/>.
/// </para>
/// </remarks>
public interface IDriverSessionCache
{
    /// <summary>Publishes the session a driver has just started.</summary>
    Task PublishAsync(Guid driverId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Clears the key when a session ends.</summary>
    Task ClearAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>The published session, or <see langword="null"/> on a miss or an outage.</summary>
    Task<Guid?> ReadAsync(Guid driverId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverSessionCache"/>
public sealed class DriverSessionCache(
    IConnectionMultiplexer redis, ILogger<DriverSessionCache> logger) : IDriverSessionCache
{
    /// <summary>
    /// How long the published fact outlives the write.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a mechanism: an end clears the key immediately, and this is what
    /// bounds the damage from an end whose Redis write failed. Twelve hours is longer than any
    /// plausible journey and short enough that a stale key cannot outlive a shift — the 30-minute
    /// idle sweep will have closed the session in Postgres long before.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public async Task PublishAsync(Guid driverId, Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetDatabase().StringSetAsync(
                RedisKeys.DriverSession(driverId), sessionId.ToString(), Lifetime);
        }
        catch (RedisException exception)
        {
            // Unconditional SET, not SETNX. The index has already decided that this driver holds
            // this session; a conditional write here could only disagree with a fact that is
            // already committed, and would leave the key describing a session that has ended.
            logger.LogWarning(
                exception, "Could not publish session {SessionId} for driver {DriverId}", sessionId, driverId);
        }
    }

    public async Task ClearAsync(Guid driverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RedisKeys.DriverSession(driverId));
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Could not clear the session key for driver {DriverId}", driverId);
        }
    }

    public async Task<Guid?> ReadAsync(Guid driverId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.DriverSession(driverId));

            return value.IsNullOrEmpty || !Guid.TryParse(value.ToString(), out var sessionId) ? null : sessionId;
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Could not read the session key for driver {DriverId}", driverId);
            return null;
        }
    }
}

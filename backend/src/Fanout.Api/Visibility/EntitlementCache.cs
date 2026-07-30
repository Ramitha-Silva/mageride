using MageRide.Shared.Caching;
using StackExchange.Redis;

namespace MageRide.Fanout.Visibility;

/// <summary>
/// D-23's <c>share:{userId}</c> SET — which Mode B vehicles one passenger may watch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked on group join, never per frame</b> (D6' §5.2). The check's result is a group
/// membership: an entitled passenger joins <c>vehicle:{vehicleId}</c> and everyone else does not, so
/// the fan-out itself carries no per-passenger test at all. That is what keeps ADD §7.4's cost model
/// intact while adding a per-passenger rule to it.
/// </para>
/// <para>
/// <b>Invalidated by events, not by a TTL.</b> The pair <c>share.granted</c>/<c>share.revoked</c> on
/// <c>registry.events</c> is the invalidation D-23 calls "pub/sub"; a TTL would make an entitled
/// passenger's map go dark on a schedule nothing published, and re-warming it would need a database
/// this service does not have.
/// </para>
/// <para>
/// <b>A miss is "not entitled", and that is the safe direction.</b> registry-svc owns
/// <c>registry.shares</c>; this is a projection of it and a cold or flushed Redis leaves an entitled
/// passenger seeing no Mode B vehicle until their next grant event. That is a degradation the
/// passenger can see and complain about. The other default — treating an unknown passenger as
/// entitled to everything — is a disclosure nobody can see. Recorded as a gap in the C041 handoff:
/// the durable fix is a rebuild path from registry-svc, which is C048's surface.
/// </para>
/// </remarks>
public interface IEntitlementCache
{
    /// <summary>The vehicles <paramref name="userId"/> may watch. Empty when nothing is known.</summary>
    Task<IReadOnlyList<Guid>> EntitlementsOfAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether one specific grant is live.</summary>
    Task<bool> IsEntitledAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary><c>share.granted</c> — the passenger accepted, so visibility begins (US-4.3b).</summary>
    Task GrantAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken);

    /// <summary><c>share.revoked</c> — ADD §11.10's <c>SREM share:{user_id} {vehicle_id}</c>.</summary>
    Task RevokeAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IEntitlementCache"/>
public sealed class EntitlementCache(IConnectionMultiplexer redis) : IEntitlementCache
{
    public async Task<IReadOnlyList<Guid>> EntitlementsOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var members = await redis.GetDatabase().SetMembersAsync(RedisKeys.Share(userId));
        var vehicles = new List<Guid>(members.Length);

        foreach (var member in members)
        {
            if (Guid.TryParse(member.ToString(), out var vehicleId))
            {
                vehicles.Add(vehicleId);
            }
        }

        return vehicles;
    }

    public async Task<bool> IsEntitledAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await redis.GetDatabase().SetContainsAsync(RedisKeys.Share(userId), vehicleId.ToString());
    }

    public async Task GrantAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await redis.GetDatabase().SetAddAsync(RedisKeys.Share(userId), vehicleId.ToString());
    }

    public async Task RevokeAsync(Guid userId, Guid vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // SREM of an absent member is a no-op, which is what makes the revocation safely
        // at-least-once: registry-svc's delivery is, and a redelivered share.revoked must not turn
        // into an error that stalls the partition.
        await redis.GetDatabase().SetRemoveAsync(RedisKeys.Share(userId), vehicleId.ToString());
    }
}

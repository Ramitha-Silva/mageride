using System.Text.Json;
using MageRide.Reputation.Configuration;
using MageRide.Reputation.Domain;
using MageRide.Shared.Caching;
using MageRide.Shared.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Reputation.Counters;

/// <summary>
/// The warm cache the C033 DoD's "under 20 ms p95" is measured against.
/// </summary>
/// <remarks>
/// <para>
/// dispatch-svc calls <c>GetBlockStatus</c> once per candidate per offer round (D5' §3.2), which is
/// the busiest read in the platform that is not a position sample. A Postgres round trip per
/// candidate would be correct and would still blow the budget on a ten-candidate pool.
/// </para>
/// <para>
/// <b>The TTL is a backstop, not the invalidation.</b> Every write deletes the key in the same
/// operation that changes the state, so a block takes effect on the next call and not
/// <see cref="ReputationOptions.BlockStatusCacheTtl"/> later. The TTL exists for the case the
/// delete could not be delivered — a Redis blip — and 5 s matches D-08's wallet gate on the same
/// hot path.
/// </para>
/// <para>
/// <b>A cache failure is never an error.</b> Redis being down must not make every driver
/// undispatchable; a miss falls through to Postgres, which is the system of record. That is why
/// every method here swallows <see cref="RedisConnectionException"/> and logs rather than throws.
/// </para>
/// </remarks>
public interface IBlockStatusCache
{
    Task<ReputationStatus?> TryGetAsync(Guid userId, CancellationToken cancellationToken);

    Task SetAsync(ReputationStatus status, CancellationToken cancellationToken);

    Task InvalidateAsync(Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBlockStatusCache"/>
public sealed class BlockStatusCache(
    IConnectionMultiplexer redis,
    IOptions<ReputationOptions> options,
    ILogger<BlockStatusCache> logger) : IBlockStatusCache
{
    private readonly ReputationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<ReputationStatus?> TryGetAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.BlockStatus(userId));

            // (string?) rather than the implicit RedisValue conversion: it is also convertible to
            // ReadOnlySpan<byte>, and the two Deserialize overloads are then ambiguous.
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<ReputationStatus>((string)value!, MageRideJson.StorageOptions);
        }
        catch (Exception ex) when (ex is RedisException or JsonException)
        {
            // A malformed value is treated exactly like a miss: the shape can change between
            // deployments, and a cached record from the previous build must not fail a call.
            logger.LogWarning(ex, "Block-status cache read failed for {UserId}; falling through", userId);
            return null;
        }
    }

    public async Task SetAsync(ReputationStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetDatabase().StringSetAsync(
                RedisKeys.BlockStatus(status.UserId),
                JsonSerializer.Serialize(status, MageRideJson.StorageOptions),
                _options.BlockStatusCacheTtl);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Block-status cache write failed for {UserId}", status.UserId);
        }
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RedisKeys.BlockStatus(userId));
        }
        catch (RedisException ex)
        {
            // Logged at warning, not error: the TTL bounds how long the stale value can be served,
            // so this degrades the block's latency rather than losing it.
            logger.LogWarning(ex, "Block-status cache invalidation failed for {UserId}", userId);
        }
    }
}

/// <summary>The cache reputation-svc keeps when Redis is switched off. Always a miss.</summary>
/// <remarks>
/// Registered when <c>UseRedis</c> is false so the service still runs — every read then goes to
/// Postgres, which is correct and slower. A test that measures the DoD's p95 must use the real one.
/// </remarks>
public sealed class NullBlockStatusCache : IBlockStatusCache
{
    public Task<ReputationStatus?> TryGetAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<ReputationStatus?>(null);

    public Task SetAsync(ReputationStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task InvalidateAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;
}

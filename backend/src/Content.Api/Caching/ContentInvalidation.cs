using MageRide.Content.Configuration;
using MageRide.Shared.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Content.Caching;

/// <summary>
/// Drops the named datasets here and tells every other replica to do the same.
/// </summary>
/// <remarks>
/// The C045 deliverable is "aggressive caching with an invalidation path on publish", and the
/// definition of done measures the other half: "a template change is visible to notification-svc
/// within the documented cache TTL". Both are true at once — the TTL is the *ceiling*, this is what
/// makes the usual case immediate.
/// </remarks>
internal interface IContentInvalidator
{
    /// <summary>
    /// Purges locally, then publishes the purge. Never throws: a publish that already committed
    /// must not be reported as a failure because a cache hint could not be sent.
    /// </summary>
    Task<IReadOnlyList<string>> InvalidateAsync(
        IReadOnlyCollection<string>? datasets, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IContentInvalidator"/> over <see cref="RedisKeys.ContentInvalidationChannel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The local purge happens first and unconditionally.</b> The replica that served the write is
/// the one whose next read is most likely to be a portal refreshing the page it just submitted, so
/// its own cache is dropped before the message goes anywhere — and it is dropped even if Redis is
/// down.
/// </para>
/// <para>
/// <b>Fire-and-forget beside a durable fact, not instead of one.</b> The row is committed; this is a
/// hint that saves other replicas the wait. A subscriber that was down misses it and falls back to
/// the TTL, which is exactly the behaviour of a deployment with <c>Content:InvalidationEnabled</c>
/// off — the same trade <c>RedisKeys.TrackerCredentialChannel</c> makes for tracker credentials.
/// </para>
/// </remarks>
internal sealed class RedisContentInvalidator(
    ContentCache cache,
    IConnectionMultiplexer redis,
    IOptions<ContentOptions> options,
    ILogger<RedisContentInvalidator> logger) : IContentInvalidator
{
    private readonly ContentOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<IReadOnlyList<string>> InvalidateAsync(
        IReadOnlyCollection<string>? datasets, CancellationToken cancellationToken)
    {
        var purged = cache.Purge(datasets);

        if (!_options.InvalidationEnabled)
        {
            return purged;
        }

        try
        {
            await redis.GetSubscriber()
                .PublishAsync(
                    RedisChannel.Literal(RedisKeys.ContentInvalidationChannel),
                    string.Join(',', purged))
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            // Deliberately not rethrown. The publish this follows has committed; every other
            // replica will pick the change up at its own TTL, which is the documented ceiling.
            logger.LogWarning(
                exception,
                "Could not publish a content cache purge for {Datasets}. Other replicas will serve "
                + "stale content for up to Content:CacheTtl ({Ttl}).",
                string.Join(", ", purged),
                _options.CacheTtl);
        }

        return purged;
    }
}

/// <summary>
/// Applies purges published by another replica.
/// </summary>
/// <remarks>
/// Registered only when <c>Content:InvalidationEnabled</c> is on. The subscription also receives
/// this replica's own messages, which purges an already-purged cache — harmless, and cheaper than
/// filtering by a publisher id that would have to be minted and trusted.
/// </remarks>
internal sealed class ContentInvalidationSubscriber(
    ContentCache cache,
    IConnectionMultiplexer redis,
    ILogger<ContentInvalidationSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first, so a slow Redis connect does not hold up host start-up (RevocationWatcher, C043).
        await Task.Yield();

        var channel = RedisChannel.Literal(RedisKeys.ContentInvalidationChannel);

        try
        {
            await redis.GetSubscriber().SubscribeAsync(channel, OnMessage).ConfigureAwait(false);

            logger.LogInformation(
                "Listening for content cache purges on {Channel}.", RedisKeys.ContentInvalidationChannel);

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
        catch (RedisException exception)
        {
            // A service that cannot subscribe still serves correct content, just up to one TTL late.
            // Loud rather than fatal: refusing to start would take the whole read surface down over a
            // cache hint.
            logger.LogError(
                exception,
                "Could not subscribe to {Channel}: a publish on another replica will not be seen here, "
                + "so this instance serves content for up to Content:CacheTtl after a change.",
                RedisKeys.ContentInvalidationChannel);
        }
        finally
        {
            try
            {
                await redis.GetSubscriber().UnsubscribeAsync(channel).ConfigureAwait(false);
            }
            catch (RedisException)
            {
                // Shutting down; nothing left to tell.
            }
        }
    }

    private void OnMessage(RedisChannel channel, RedisValue message)
    {
        var datasets = ((string?)message ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        cache.Purge(datasets);
    }
}

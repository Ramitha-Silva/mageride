using MageRide.Shared.Caching;
using StackExchange.Redis;

namespace MageRide.Notification.Templates;

/// <summary>
/// Drops the rendered-template cache when content-svc publishes a purge.
/// </summary>
/// <remarks>
/// <para>
/// The other end of content-svc's third definition of done — "a template change is visible to
/// notification-svc within the documented cache TTL". <b>This is what makes it immediate instead.</b>
/// Without it the promise still holds, one <c>Notification:TemplateCacheTtl</c> late, which is the
/// documented ceiling; with it, an admin who fixes a typo in the Sinhala offer SMS sees the fix on
/// the next send.
/// </para>
/// <para>
/// The payload is a comma-separated dataset list (content-svc's <c>RedisContentInvalidator</c>), and
/// only <c>templates</c> concerns this service. An empty message means "everything", which is what a
/// purge with no dataset list produces, so it is treated as a hit.
/// </para>
/// <para>
/// Failing to subscribe is loud and not fatal, for the same reason it is on content-svc's side: a
/// service that cannot hear a purge still sends correct notifications, up to one TTL late. Refusing
/// to start would stop every push on the platform over a cache hint.
/// </para>
/// </remarks>
internal sealed class TemplateInvalidationSubscriber(
    ITemplateSource templates,
    IConnectionMultiplexer redis,
    ILogger<TemplateInvalidationSubscriber> logger) : BackgroundService
{
    /// <summary>content-svc's dataset name for <c>content.notification_templates</c>.</summary>
    private const string TemplatesDataset = "templates";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first: a slow Redis connect must not hold up host start-up.
        await Task.Yield();

        var channel = RedisChannel.Literal(RedisKeys.ContentInvalidationChannel);

        try
        {
            await redis.GetSubscriber().SubscribeAsync(channel, OnMessage).ConfigureAwait(false);

            logger.LogInformation(
                "Listening for content template purges on {Channel}.", RedisKeys.ContentInvalidationChannel);

            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
        catch (RedisException exception)
        {
            logger.LogError(
                exception,
                "Could not subscribe to {Channel}: an edited template will not be seen here until "
                + "Notification:TemplateCacheTtl expires.",
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
        var payload = (string?)message ?? string.Empty;

        var datasets = payload.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (datasets.Length != 0 && !datasets.Contains(TemplatesDataset, StringComparer.Ordinal))
        {
            return;
        }

        templates.Invalidate();
        logger.LogInformation("Dropped the rendered-template cache after a content-svc purge.");
    }
}

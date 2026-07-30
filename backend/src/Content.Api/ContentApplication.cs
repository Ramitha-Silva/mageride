using MageRide.Content.Caching;
using MageRide.Content.Configuration;
using MageRide.Content.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using Microsoft.Extensions.Options;

namespace MageRide.Content;

/// <summary>
/// Composition root for content-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class ContentApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Redis client id.</summary>
    public const string ServiceName = "content-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            UsePostgres = true,

            // Redis carries the cross-replica cache purge and nothing else — the cache itself is in
            // process, because these datasets are small and the template render is on the hottest
            // path E-01 has. See ContentCache.
            UseRedis = true,

            // **No Kafka and no outbox, and that is structural.** Nothing here is a state change
            // another service has to learn about: notification-svc reads a template when it renders
            // one, and the apps read a banner when they open. An event announcing "the ride_offer
            // template changed" would have no consumer that could act on it — the next render already
            // sees the new version — and the outbox exists for facts that must not be lost, whereas a
            // cache purge that is lost costs one TTL.
            UseKafka = false,
            UseOutbox = false,

            // **A command log, for one route.** R-14's replay matters where a repeated request would
            // double an effect, and only `POST /v1/admin/content/broadcasts` can: an approve is
            // self-limiting (a second one is a 409 by the version's own status) and a purge is
            // idempotent by nature, but a retried publish — a proxy retry, a portal double-submit, a
            // 502 on the way back — puts a **second identical banner** in front of every user on the
            // platform, and there is no natural key that would collide. `content.yaml` declares
            // `Idempotency-Key` required on both POSTs and D3' §0 requires it on every POST mutation,
            // so the alternative was a header the service accepted and ignored.
            //
            // The purge route opts out with `AllowMissingIdempotencyKey`, matching its
            // `x-idempotency-exempt`.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins, but this service's own
        // table is the default. The kernel's defaults describe `rides.command_log`, which has a
        // `ride_id`; a broadcast targets no aggregate that exists yet, so there is no such column
        // here (migration 1307, shaped like 0307).
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "content";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddContentServices(builder.Configuration);

        var settings = builder.Configuration.GetSection(ContentOptions.SectionName).Get<ContentOptions>()
                       ?? new ContentOptions();

        if (settings.InvalidationEnabled)
        {
            builder.Services.AddHostedService(
                services => services.GetRequiredService<ContentInvalidationSubscriber>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapConfigEndpoints();
        app.MapContentEndpoints(settings.InternalApiKey);
        app.MapAdminContentEndpoints();

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalContentEndpoints(settings.InternalApiKey);
        }

        WarnAboutWhatIsNotBeingEnforced(app);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which of this service's guarantees are switched off.
    /// </summary>
    /// <remarks>
    /// The same rule query-svc, fanout-svc and fleet-health-svc are written under. Every setting below
    /// fails the same way: content is served, nothing errors, and the difference only shows up as a
    /// notification that went out with last month's wording, an edit nobody approved, or a template
    /// surface anyone in the cluster can read.
    /// </remarks>
    private static void WarnAboutWhatIsNotBeingEnforced(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ContentOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            logger.LogWarning(
                "Content:InternalApiKey is not configured. GET /v1/content/templates/{{key}} accepts any "
                + "caller that reaches this service — and unlike every other internal family it is NOT "
                + "under the /v1/internal prefix the gateway refuses (D3' prints it under /v1/content), "
                + "so that includes the public internet. POST /v1/internal/content/cache/purge is "
                + "unmapped, so an admin-bff city edit is picked up at Content:CacheTtl rather than "
                + "immediately.");
        }

        if (!options.CacheEnabled)
        {
            logger.LogWarning(
                "Content:CacheEnabled is off: every notification render is a database round trip. E-01 "
                + "renders one template per candidate driver per ride offer, which is the load the cache "
                + "exists to absorb.");
        }
        else if (!options.InvalidationEnabled)
        {
            logger.LogWarning(
                "Content:InvalidationEnabled is off: a template published on one replica reaches the "
                + "others only when their own entry expires, so the promise narrows from 'immediately' to "
                + "'within Content:CacheTtl' ({Ttl}).",
                options.CacheTtl);
        }

        if (options.PublishOnEdit)
        {
            logger.LogWarning(
                "Content:PublishOnEdit is on: PUT /v1/admin/content/{{key}} goes live immediately and "
                + "content.notification_templates.approved_by records the author as their own approver. "
                + "D3' calls this route a versioned edit with an approval workflow (D-35 four eyes).");
        }

        if (!string.IsNullOrWhiteSpace(options.AssetBaseUrl))
        {
            logger.LogInformation(
                "Content:AssetBaseUrl is set to {BaseUrl}: onboarding illustration references are served "
                + "as absolute URLs rather than app-bundled asset keys (AL-28).",
                options.AssetBaseUrl);
        }
    }
}

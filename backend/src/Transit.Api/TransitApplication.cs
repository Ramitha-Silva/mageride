using MageRide.Shared;
using MageRide.Transit.Configuration;
using MageRide.Transit.Endpoints;
using MageRide.Transit.Feed;
using Microsoft.Extensions.Options;

namespace MageRide.Transit;

/// <summary>
/// Composition root for transit-svc's routing half. Lives here rather than in <c>Program.cs</c> so
/// the test suite drives the same pipeline the process runs.
/// </summary>
public static class TransitApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "transit-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var settings = builder.Configuration.GetSection(TransitOptions.SectionName).Get<TransitOptions>()
                       ?? new TransitOptions();

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // `transit.gtfs_*`, read once per activation. Also the LISTEN connection.
            UsePostgres = true,

            // **No Redis.** The feed cache is per process and must be: it is a hundred megabytes of
            // derived structure rebuilt from one transaction's worth of rows, and a shared copy
            // would need an invalidation protocol on top of the NOTIFY that already exists. Each
            // replica reloads independently and converges within the same bound.
            UseRedis = false,

            // **No Kafka and no outbox.** This service publishes nothing and consumes nothing:
            // activation reaches it through Postgres' own LISTEN/NOTIFY, which is what D6' I-32.1
            // specifies by name. D6' §2.1 gives transit-svc no topic.
            UseKafka = false,
            UseOutbox = false,

            // **No command log.** Every route here is a GET. There is no mutation to replay.
            UseCommandLog = false,

            UseAuthentication = true,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddTransitServices(builder.Configuration);

        if (settings.FeedCacheEnabled)
        {
            builder.Services.AddHostedService<GtfsFeedListener>();
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapTransitEndpoints();

        Announce(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, whether this service can answer a corridor at all.
    /// </summary>
    /// <remarks>
    /// The same rule content-svc, support-svc, ocr-svc and voip-svc are written under, and it
    /// matters here for AL-55's reason: <b>a service with no feed answers every corridor the same
    /// way a service with a feed answers a corridor no bus serves.</b> The wire shape distinguishes
    /// them (`coverage`), and this is the operator's half of the same distinction.
    /// </remarks>
    private static void Announce(WebApplication app, TransitOptions settings)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (!settings.FeedCacheEnabled)
        {
            logger.LogError(
                "Transit:FeedCacheEnabled is off, so no GTFS feed is ever loaded: EVERY corridor answers "
                + "coverage=no_feed and Mode A route matching is hidden on every booking screen (AL-55).");
        }

        logger.LogInformation(
            "transit-svc routing is up: halt radius {Radius} m, up to {Halts} halts per end, transfers {Transfers}, "
            + "feed cache {Cache} on LISTEN {Channel} with a {Poll} safety net. No Google API is called on any "
            + "path (AL-20, D6' I-23.1).",
            settings.HaltRadiusM,
            settings.MaxHaltsPerEnd,
            settings.TransferOptionsEnabled ? "on" : "OFF",
            settings.FeedCacheEnabled ? "on" : "OFF",
            settings.FeedChannel,
            settings.FeedPollInterval);
    }
}

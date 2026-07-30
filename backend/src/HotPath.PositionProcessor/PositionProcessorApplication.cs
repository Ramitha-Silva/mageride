using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Plausibility;
using MageRide.HotPath.PositionProcessor.Processing;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.HotPath.PositionProcessor.Throttling;
using MageRide.Shared;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PositionProcessor;

/// <summary>
/// Composition root for position-processor-svc. Lives here rather than in <c>Program.cs</c> so the
/// test suite drives the same pipeline the process runs.
/// </summary>
public static class PositionProcessorApplication
{
    /// <summary>Service name for telemetry and the Kafka client id.</summary>
    public const string ServiceName = "position-processor-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Redis and Redpanda, nothing else. The Postgres this service will eventually write to
            // is the Timescale hypertable, and that write path belongs to persistence-writer-svc
            // (C040) — ADD §9.5 batches it through COPY precisely so the hot path never holds a
            // database connection.
            UsePostgres = false,
            UseCommandLog = false,
            UseRedis = true,
            UseKafka = true,
            UseAuthentication = false,
        };

        builder.AddMageRideDefaults(serviceOptions);

        builder.Services.AddOptions<PositionProcessorOptions>()
            .Bind(builder.Configuration.GetSection(PositionProcessorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<ILivePositionIndex, LivePositionIndex>();
        builder.Services.AddSingleton<IDriverAvailabilityIndex, DriverAvailabilityIndex>();
        builder.Services.AddSingleton<IIngestRateGuard, IngestRateGuard>();

        // The filter holds nothing but its options and is pure; a scoped instance per sample would
        // be an allocation per position on the hottest path the platform has.
        builder.Services.AddSingleton<IPlausibilityFilter, PlausibilityFilter>();

        builder.Services.AddScoped<IPositionProcessor, Processing.PositionProcessor>();

        var processor = builder.Configuration.GetSection(PositionProcessorOptions.SectionName)
            .Get<PositionProcessorOptions>() ?? new PositionProcessorOptions();

        if (processor.Enabled)
        {
            builder.Services.AddHostedService<TelemetryRawConsumer>();
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        WarnAboutGatesThatCannotClose(app);

        return app;
    }

    /// <summary>
    /// Says once, loudly, which anti-spoof gates are switched off.
    /// </summary>
    /// <remarks>
    /// Every one of these fails <i>open</i> when disabled, and an open anti-spoof gate looks exactly
    /// like a working one from the outside — positions flow, the map is populated, nothing errors.
    /// The same reasoning as dispatch-svc's <c>WarnAboutGatesThatCannotClose</c>: a configuration
    /// that quietly disables a security control should be visible in the first screen of logs.
    /// </remarks>
    private static void WarnAboutGatesThatCannotClose(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PositionProcessorOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (!options.PlausibilityEnabled)
        {
            logger.LogWarning(
                "PositionProcessor:PlausibilityEnabled is off — the D-18/T-07 anti-spoof filter is not " +
                "running and a spoofed position will reach the live map and the dispatch pool.");
        }

        if (!options.RateCheckEnabled)
        {
            logger.LogWarning(
                "PositionProcessor:RateCheckEnabled is off — D-17's second line is not running, and the " +
                "broker's per-connection limiter is the only ceiling a vehicle faces.");
        }

        if (!options.AvailabilityIndexEnabled)
        {
            logger.LogWarning(
                "PositionProcessor:AvailabilityIndexEnabled is off — R-08's candidate index is not being " +
                "kept at drivers' live positions by this service.");
        }

        if (!options.PublishNormalized)
        {
            logger.LogWarning(
                "PositionProcessor:PublishNormalized is off — nothing is written to telemetry.normalized, so " +
                "persistence-writer, trip-state, fleet-health and dispatch-svc's presence heartbeat all stop.");
        }
    }
}

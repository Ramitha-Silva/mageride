using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Processing;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.Shared;

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
        builder.Services.AddScoped<IPositionProcessor, Processing.PositionProcessor>();

        var processor = builder.Configuration.GetSection(PositionProcessorOptions.SectionName)
            .Get<PositionProcessorOptions>() ?? new PositionProcessorOptions();

        if (processor.Enabled)
        {
            builder.Services.AddHostedService<TelemetryRawConsumer>();
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        return app;
    }
}

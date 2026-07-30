using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.HotPath.PersistenceWriter.Ingest;
using MageRide.HotPath.PersistenceWriter.Persistence;
using MageRide.HotPath.PersistenceWriter.Sampling;
using MageRide.HotPath.PersistenceWriter.Summaries;
using MageRide.Shared;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PersistenceWriter;

/// <summary>
/// Composition root for persistence-writer-svc. Lives here rather than in <c>Program.cs</c> so the
/// test suite drives the same pipeline the process runs.
/// </summary>
public static class PersistenceWriterApplication
{
    /// <summary>Service name for telemetry and the Kafka client id.</summary>
    public const string ServiceName = "persistence-writer-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Postgres and Redpanda. No Redis at all — this service must be structurally incapable of
            // touching the live map, which is the second of this component's two fences: "a slow or
            // failed write must not affect the live map". Not registering the client is a stronger
            // guarantee than not calling it.
            UsePostgres = true,
            UseCommandLog = false,
            UseRedis = false,
            UseKafka = true,
            UseAuthentication = false,
        };

        builder.AddMageRideDefaults(serviceOptions);

        builder.Services.AddOptions<PersistenceWriterOptions>()
            .Bind(builder.Configuration.GetSection(PersistenceWriterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // D7' §4.2 spells this service's two settings under a `Timescale` prefix
        // (Timescale__BatchRows, Timescale__FlushMs). Bound as a second pass so either name works and
        // the deployed environment files do not have to be rewritten; the PersistenceWriter section
        // wins where both are set, because it is the one that carries everything else.
        builder.Services.AddOptions<PersistenceWriterOptions>()
            .PostConfigure<IConfiguration>(static (writer, configuration) =>
            {
                var legacy = configuration.GetSection("Timescale");

                if (legacy["BatchRows"] is { Length: > 0 } rows
                    && int.TryParse(rows, out var parsed)
                    && configuration[$"{PersistenceWriterOptions.SectionName}:BatchRows"] is null)
                {
                    writer.BatchRows = parsed;
                }

                if (legacy["FlushMs"] is { Length: > 0 } flush
                    && int.TryParse(flush, out var milliseconds)
                    && configuration[$"{PersistenceWriterOptions.SectionName}:FlushInterval"] is null)
                {
                    writer.FlushInterval = TimeSpan.FromMilliseconds(milliseconds);
                }
            });

        builder.Services.AddSingleton<IVehicleContextResolver, VehicleContextResolver>();
        builder.Services.AddSingleton<IOperationalSampler, OperationalSampler>();
        builder.Services.AddSingleton<IDeadLetterSink, DeadLetterSink>();
        builder.Services.AddSingleton<IPositionBatchWriter, PositionBatchWriter>();

        // Scoped: one summary is one unit of work over its own connection, and the consumer opens a
        // scope per message the way every other event consumer on the platform does.
        builder.Services.AddScoped<ITripSummaryService, TripSummaryService>();

        var writerOptions = builder.Configuration.GetSection(PersistenceWriterOptions.SectionName)
            .Get<PersistenceWriterOptions>() ?? new PersistenceWriterOptions();

        if (writerOptions.Enabled)
        {
            // Registered as a singleton as well as a hosted service, so a test can read its counters
            // — RowsWritten and FailedFlushes are what the throughput and durability assertions are
            // made against, and neither is observable from outside.
            builder.Services.AddSingleton<TelemetryWriterWorker>();
            builder.Services.AddHostedService(static services =>
                services.GetRequiredService<TelemetryWriterWorker>());
        }

        if (writerOptions.SummariesEnabled)
        {
            builder.Services.AddSingleton<TripEventConsumer>();
            builder.Services.AddHostedService(static services =>
                services.GetRequiredService<TripEventConsumer>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        WarnAboutWhatIsNotBeingWritten(app);

        return app;
    }

    /// <summary>
    /// Says once, loudly, which durable write path is switched off.
    /// </summary>
    /// <remarks>
    /// Every one of these fails <i>silently</i>. A writer that is not writing looks exactly like a
    /// platform with no vehicles on it: ingest flows, the live map works, no request errors — and the
    /// system of record quietly has nothing in it. The same reasoning as dispatch-svc's
    /// <c>WarnAboutGatesThatCannotClose</c> and C039's.
    /// </remarks>
    private static void WarnAboutWhatIsNotBeingWritten(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PersistenceWriterOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (!options.Enabled)
        {
            logger.LogWarning(
                "PersistenceWriter:Enabled is off — nothing is being written to telemetry.positions. " +
                "The hypertable is the system of record for telemetry (T-06) and it is receiving nothing.");
        }

        if (!options.OperationalSamplingEnabled)
        {
            logger.LogWarning(
                "PersistenceWriter:OperationalSamplingEnabled is off — trips.position_samples is not " +
                "being written, so Mode A/B journeys have no operational history (ADD §9.2).");
        }

        if (!options.SummariesEnabled)
        {
            logger.LogWarning(
                "PersistenceWriter:SummariesEnabled is off — no trip summary is written when a session " +
                "ends, so distance and polyline are unavailable for every journey (ADD §9.2).");
        }

        if (!options.DeadLetterEnabled)
        {
            logger.LogInformation(
                "PersistenceWriter:DeadLetterEnabled is off — a row Postgres refuses will stall its " +
                "partition rather than being published to {Topic}. Loud rather than lossy.",
                DeadLetterSink.Topic);
        }
    }
}

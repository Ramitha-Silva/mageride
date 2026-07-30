using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Endpoints;
using MageRide.FleetHealth.Ingest;
using MageRide.FleetHealth.Mqtt;
using MageRide.FleetHealth.Rollups;
using MageRide.Shared;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.FleetHealth;

/// <summary>
/// Composition root for fleet-health-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class FleetHealthApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "fleet-health-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Postgres, Redpanda and an outbox. No Redis, deliberately: the live indexes are
            // position-processor's (C039) and fanout-svc's (C041), and a health rollup measured in
            // minutes has nothing to gain from a cache in front of a table it reads once per dashboard
            // load. Not registering the client is what makes that a property of the service rather
            // than a habit.
            UseRedis = false,

            // `fleet.health_alert` goes into telemetry.outbox inside the transaction that claims the
            // window, and the kernel's LISTEN/NOTIFY dispatcher drains it to `fleet.events` after
            // COMMIT (E-09, R-13). An alert that committed and then failed to publish would be an
            // outage nobody was told about, sitting behind a unique index that stops it being retried.
            UseKafka = true,
            UseOutbox = true,

            // No POST on this surface, so no Idempotency-Key replay log — and therefore no
            // `telemetry.command_log` table for a route family that does not exist. The same call
            // query-svc makes.
            UseCommandLog = false,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins, but this service's own
        // outbox is the default. The kernel's defaults describe rides.outbox on `ride_outbox` →
        // `ride.events`; the topic here is not one of D6' §2.1's six (see EventTopics.FleetEvents).
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "telemetry";
            outbox.Channel = "telemetry_outbox";
            outbox.Topic = EventTopics.FleetEvents;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddFleetHealthServices(builder.Configuration);

        var health = builder.Configuration.GetSection(FleetHealthOptions.SectionName);
        var enabled = health.GetValue("Enabled", true);

        // Bound after AddFleetHealthServices so the section is available; hosted separately from the
        // registrations there so a test can resolve a worker without it also ticking.
        if (enabled && health.GetValue("PingConsumerEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<TelemetryHealthConsumer>());
        }

        if (enabled && health.GetValue("ProvisioningConsumerEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<ProvisioningEventConsumer>());
        }

        if (enabled && health.GetValue("SweepEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<HealthSweepWorker>());
        }

        if (enabled && health.GetValue("AlertsEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<FleetHealthAlertWorker>());
        }

        if (enabled && health.GetValue("DevicePlaneEnabled", false))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<DevicePlaneWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapFleetHealthEndpoints();

        WarnAboutWhatIsNotBeingWatched(app);

        return app;
    }

    /// <summary>
    /// Says once, loudly, which inputs to the health plane are switched off.
    /// </summary>
    /// <remarks>
    /// Every one of these fails the same way: the dashboard renders, the counts add up, nothing errors —
    /// and every tracker on the platform sits in one state for ever. A fleet that is entirely
    /// <c>Offline</c> and a fleet whose ping consumer was never started look identical to an operator,
    /// which is exactly the failure this service exists to make visible. The same rule
    /// position-processor-svc, persistence-writer-svc and query-svc are written under.
    /// </remarks>
    private static void WarnAboutWhatIsNotBeingWatched(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FleetHealthOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (!options.Enabled)
        {
            logger.LogWarning(
                "Health:Enabled is off — nothing is consumed, nothing is swept and no alert can be " +
                "raised. GET /v1/fleets/{{fleetId}}/health still answers, from whatever " +
                "telemetry.device_health last held.");

            return;
        }

        if (!options.PingConsumerEnabled)
        {
            logger.LogWarning(
                "Health:PingConsumerEnabled is off — telemetry.normalized is not consumed, so no " +
                "tracker's last-seen advances and every device on every fleet dashboard will read as " +
                "Offline (US-3.13).");
        }

        if (!options.ProvisioningConsumerEnabled)
        {
            logger.LogWarning(
                "Health:ProvisioningConsumerEnabled is off — provisioning.events is not consumed, so a " +
                "newly bound tracker never appears on its fleet's dashboard and a decommissioned one " +
                "(US-3.8) reads as merely Offline instead of Decommissioned.");
        }

        if (!options.DevicePlaneEnabled)
        {
            logger.LogInformation(
                "Health:DevicePlaneEnabled is off — the EMQX last will (R-15, T-04) and " +
                "sys/diag/{{vehicleId}} are not consumed. The four states still work (they are " +
                "thresholds on silence) but a dropped session is noticed at Health:StaleAfter rather " +
                "than immediately, and no battery, signal or satellite reading is recorded (US-3.12).");
        }

        if (!options.SweepEnabled)
        {
            logger.LogWarning(
                "Health:SweepEnabled is off — no state change is recorded, so the transition metrics " +
                "and every device's `since` timestamp are frozen. The dashboard's counts are " +
                "unaffected: they are derived at read time.");
        }

        if (!options.AlertsEnabled)
        {
            logger.LogWarning(
                "Health:AlertsEnabled is off — no fleet.health_alert is raised however much of a fleet " +
                "goes dark (US-3.16), and notification-svc has nothing to send.");
        }

        if (!options.BindingSyncEnabled)
        {
            logger.LogInformation(
                "Health:BindingSyncEnabled is off — prov.tracker_bindings.last_seen_at, " +
                "signal_strength, battery_mv and sat_count are not written, so the Admin Portal's " +
                "per-tracker panel (US-3.12) stays blank. This service is their only writer.");
        }

        if (!options.RefreshAggregateEnabled)
        {
            logger.LogInformation(
                "Health:RefreshAggregateEnabled is off — telemetry.fleet_health_5m is materialised only " +
                "by its own policy, so a just-closed window is served by rescanning raw hypertable " +
                "chunks. Correct, and the scan the rollup exists to avoid.");
        }
    }
}

using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using MageRide.TripState.Configuration;
using MageRide.TripState.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.TripState;

/// <summary>
/// Composition root for trip-state-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class TripStateApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Redis client id.</summary>
    public const string ServiceName = "trip-state-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // `lock:session:{driverId}` — the D-03 fact the dispatch and tracking planes read so
            // they need not ask this service which session a vehicle's positions belong to. Best
            // effort: ux_sessions_active_driver is the invariant, so a Redis outage costs a lookup
            // rather than correctness.
            UseRedis = true,

            // session.started / session.ended / session.restarted are written into trips.outbox
            // inside the transaction that changes the session, and the kernel's LISTEN/NOTIFY
            // dispatcher drains them to `trip.events` after COMMIT (E-09, R-13). An end that
            // committed and then failed to publish would leave fanout-svc showing a finished
            // journey on the passenger map with no way for the driver to take it off.
            UseKafka = true,
            UseOutbox = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins, but this service's own
        // outbox is the default. The kernel's defaults describe rides.outbox on `ride_outbox` →
        // `ride.events`. The topic itself is D6' §2.1's, unlike C028's and C030's.
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "trips";
            outbox.Channel = "trips_outbox";
            outbox.Topic = EventTopics.TripEvents;
        });

        // Likewise for the R-14 replay log (migration 0505). AggregateIdColumn is null because a
        // start targets a session that does not exist yet, so there is no id column to write.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "trips";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddTripStateServices(builder.Configuration);

        var tripState = builder.Configuration.GetSection(TripStateOptions.SectionName);

        // Bound after AddTripStateServices so the section is available; hosted separately from the
        // registrations there so a test can resolve a worker without it also ticking.
        if (tripState.GetValue("SweepEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Sessions.SessionSweepWorker>());
        }

        if (tripState.GetValue("PositionConsumerEnabled", true))
        {
            builder.Services.AddHostedService(
                services => services.GetRequiredService<Telemetry.SessionPositionConsumer>());
        }

        if (tripState.GetValue("VehicleStatusEnabled", false))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Mqtt.VehicleStatusWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapSessionEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<TripStateOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalSessionEndpoints(settings.InternalApiKey);
        }
        else
        {
            // The tracker plane reports ACC on/off through this prefix (US-3.22/3.23). Without it
            // those calls 404 and ignition auto-sessions silently stop happening — which looks
            // from the driver's side like the platform ignoring their bus, so it is said loudly
            // here instead.
            app.Logger.LogWarning(
                "TripState:InternalApiKey is not configured, so /v1/internal/sessions/** is unmapped. " +
                "Tracker-equipped Mode A/B vehicles will not auto-start or auto-end on ignition (AL-32, " +
                "US-3.22/3.23), and a fired timer has no route to end a session through (US-5.9).");
        }

        if (!settings.SweepEnabled)
        {
            // US-5.3 and US-5.4 are platform guarantees; a deployment running with the sweep off
            // leaves every forgotten session live forever.
            app.Logger.LogWarning(
                "TripState:SweepEnabled is off: no session will be auto-ended by the idle timer (US-5.3), " +
                "the destination geofence (US-5.4) or the broker's last will (R-15, T-04).");
        }

        return app;
    }
}

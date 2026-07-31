using MageRide.Safety.Configuration;
using MageRide.Safety.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;

namespace MageRide.Safety;

/// <summary>
/// Composition root for safety-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class SafetyApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "safety-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var settings = builder.Configuration.GetSection(SafetyOptions.SectionName).Get<SafetyOptions>()
                       ?? new SafetyOptions();

        var dispatcherEnabled =
            (builder.Configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions())
            .DispatcherEnabled;

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            UsePostgres = true,

            // D-34's 60/min per token and per IP, and the live position the public view draws. Both
            // have to be shared: a per-process bucket is a limit on nothing across replicas, and the
            // position is written by position-processor-svc into `veh:meta`.
            UseRedis = true,

            // The dispatcher needs a producer; nothing else here publishes. Tied to the kernel's own
            // switch so a deployment that is not draining the outbox needs no broker address.
            UseKafka = dispatcherEnabled,

            // **An outbox, and it is load-bearing.** D3' lists "admin live-feed WS" as a side effect
            // of POST /v1/sos and `realtime/signalr-hub.md` has no group for it, so the event is the
            // half this service can own — written inside the transaction that records the alert
            // (R-13), because an operator has to learn about an SOS whether or not a gateway took it.
            // Always on: the *row* is part of the transaction, and only its publication is optional.
            UseOutbox = true,

            // R-14. `safety.yaml` declares `Idempotency-Key` on all four POSTs, and it matters most
            // on the SOS: a double-tapped panic button under one key must send one SMS, and the
            // second tap is *likely* — it is what somebody does when nothing appears to happen.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // rides.command_log and rides.outbox; both of this service's are `safety.*` (migration
        // 0905), and the command log has no aggregate-id column because an SOS targets no aggregate
        // this service owns.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "safety";
            commandLog.AggregateIdColumn = null;
        });

        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "safety";
            outbox.Table = "outbox";
            outbox.Channel = "safety_outbox";
            outbox.Topic = "safety.events";
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddSafetyServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapSafetyEndpoints();

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalSafetyEndpoints(settings.InternalApiKey);
        }

        WarnAboutAlertsThatCannotBeRaised(app, settings, dispatcherEnabled);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service cannot reach anybody.
    /// </summary>
    /// <remarks>
    /// The same rule fare-svc, wallet-svc, content-svc and notification-svc are written under, and
    /// it matters most here: <b>an SOS that goes nowhere looks exactly like one that worked.</b> The
    /// button animates, the row is written, the response is a 200, and nobody's phone rings.
    /// </remarks>
    private static void WarnAboutAlertsThatCannotBeRaised(
        WebApplication app, SafetyOptions settings, bool dispatcher)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(settings.NotificationBaseUrl))
        {
            logger.LogError(
                "Safety:NotificationBaseUrl is not configured, so NO SOS CAN BE DISPATCHED. Every alert is recorded "
                + "and pushed to the admin live feed, and no SMS is sent to anybody's emergency contact (D-33).");
        }

        if (string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            logger.LogError(
                "Safety:InternalApiKey is not configured, so /v1/internal/safety/** is not mapped: no vehicle report "
                + "can be confirmed (US-12.6's third confirmation is what delists), and no trip-end revocation can "
                + "close a share link before Safety:ShareMaxLifetime.");
        }

        if (!dispatcher)
        {
            logger.LogError(
                "Outbox:DispatcherEnabled is off. sos.raised rows are written and never published, so the admin live "
                + "feed stays silent while the SMS still goes out — an operator learns about an emergency only if "
                + "somebody tells them. The rows drain when a dispatcher is turned on.");
        }

        if (!settings.ReputationReportingEnabled)
        {
            logger.LogWarning(
                "Safety:ReputationReportingEnabled is off. Reports are filed and never counted, so no vehicle is ever "
                + "auto-delisted (US-12.6) — the moderation queue fills and the third confirmation does nothing.");
        }

        if (string.IsNullOrWhiteSpace(settings.ShareBaseUrl))
        {
            logger.LogWarning(
                "Safety:ShareBaseUrl is not configured, so POST /v1/trip-share/{{tripId}} is refused and an SOS SMS "
                + "carries no live-tracking link (D-34).");
        }

        if (!settings.RequireEmergencyContact)
        {
            logger.LogWarning(
                "Safety:RequireEmergencyContact is off, so an SOS from a user with nobody on file is recorded rather "
                + "than refused. D3' answers 400 no-emergency-contact; the row's sms_status says NoContact.");
        }

        logger.LogInformation(
            "safety-svc is up: share window trip end + {Grace}, {PerMinute}/min per token, delist at {Threshold} confirmed reports.",
            settings.ShareGrace, settings.PublicViewPerMinute, settings.ReportDelistThreshold);
    }
}

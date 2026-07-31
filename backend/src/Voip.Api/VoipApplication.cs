using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Voip.Configuration;
using MageRide.Voip.Endpoints;
using MageRide.Voip.Messaging;
using MageRide.Voip.Signalling;
using Microsoft.Extensions.Options;

namespace MageRide.Voip;

/// <summary>
/// Composition root for voip-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class VoipApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "voip-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var settings = builder.Configuration.GetSection(VoipOptions.SectionName).Get<VoipOptions>()
                       ?? new VoipOptions();

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // `rides.rides` (read), `comms.voip_sessions` and `comms.call_log` (written).
            UsePostgres = true,

            // **No Redis.** Nothing here is rate-limited by a shared bucket and nothing is cached:
            // a token is minted per call from a row that was just read, and caching a ride's
            // participants is how a cancelled ride keeps issuing tokens.
            UseRedis = false,

            // **In, not out.** The room teardown consumes `ride.events`; this service publishes
            // nothing, so there is no outbox. A `voip.*` topic would carry "a call started", which
            // no service in this build acts on — the SLO reader is `comms.call_log`.
            UseKafka = settings.RoomTeardownEnabled,
            UseOutbox = false,

            // R-14. `voip.yaml` declares `Idempotency-Key` on both POSTs, and it earns it on
            // `/v1/calls/start`: a double-tapped Call button under one key must produce one
            // `comms.call_log` row, or the direct-dial fallback rate — the only measure of AL-48's
            // fallback — counts the same tap twice.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // `rides.command_log`; this service's is `comms.command_log` (migration 1308, C051), with no
        // aggregate-id column because a call targets a ride this service does not own.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "comms";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddVoipServices(builder.Configuration);

        if (settings.RoomTeardownEnabled)
        {
            builder.Services.AddHostedService<RideEventConsumer>();
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapVoipEndpoints();

        Announce(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service can actually carry a call.
    /// </summary>
    /// <remarks>
    /// The same rule content-svc, notification-svc, support-svc and ocr-svc are written under, and
    /// it matters here for its own reason: <b>a voip-svc with no LiveKit behind it looks exactly
    /// like one whose calls keep failing.</b> Every attempt answers <c>503</c>, every client shows
    /// "Call normally instead?", every user dials directly — and the feature reads as flaky rather
    /// than as absent.
    /// </remarks>
    private static void Announce(WebApplication app, VoipOptions settings)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        var minter = app.Services.GetRequiredService<ILiveKitTokenMinter>();
        var rooms = app.Services.GetRequiredService<ILiveKitRoomService>();

        if (!minter.IsConfigured)
        {
            logger.LogError(
                "LiveKit is not configured (Voip:LiveKit:WsUrl/ApiKey/ApiSecret), so NO IN-APP CALL CAN BE "
                + "PLACED: every attempt answers 503 dependency-unavailable and every client falls back to a "
                + "direct tel: dial (AL-48). Calling is not broken — it is absent.");
        }

        if (!rooms.IsConfigured)
        {
            logger.LogError(
                "No LiveKit server API is configured (Voip:LiveKit:ApiUrl), so a room is NEVER TORN DOWN at "
                + "trip end. A call that connected before the ride ended will run until LiveKit's own empty-room "
                + "timeout — D6' §6 requires signalling to expire with the trip.");
        }

        if (!settings.RoomTeardownEnabled)
        {
            logger.LogError(
                "Voip:RoomTeardownEnabled is off, so ride.events is not consumed and no call is ended when its "
                + "ride is. Minting is still refused for a terminal ride, but a call already in progress outlives "
                + "it (D6' §6).");
        }

        logger.LogInformation(
            "voip-svc is up: LiveKit {LiveKit} at {WsUrl}, server API {Api}, join tokens live {Ttl}, room "
            + "teardown {Teardown}. Number masking is withdrawn (AL-48) — this service places no PSTN call and "
            + "serves no phone number.",
            minter.IsConfigured ? "configured" : "NOT CONFIGURED",
            string.IsNullOrWhiteSpace(minter.WsUrl) ? "(unset)" : minter.WsUrl,
            rooms.IsConfigured ? "configured" : "NOT CONFIGURED",
            settings.TokenTtl,
            settings.RoomTeardownEnabled ? "on" : "OFF");
    }
}

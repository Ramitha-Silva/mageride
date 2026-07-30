using MageRide.Fanout.Configuration;
using MageRide.Fanout.Messaging;
using MageRide.Fanout.Realtime;
using MageRide.Fanout.Rides;
using MageRide.Fanout.Visibility;
using MageRide.Shared;
using MageRide.Shared.Http;
using MageRide.Shared.Mqtt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout;

/// <summary>
/// Composition root for fanout-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class FanoutApplication
{
    /// <summary>Service name for telemetry and the Kafka client id.</summary>
    public const string ServiceName = "fanout-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var fanout = builder.Configuration.GetSection(FanoutOptions.SectionName).Get<FanoutOptions>()
                     ?? new FanoutOptions();

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Redis is the whole of this service's durable state: the cell streams
            // position-processor-svc writes, the entitlement SET, the engagement marks, the ride
            // projection and the directed-send channel. No Postgres and no outbox — everything this
            // service knows it learned from somebody else's.
            UsePostgres = false,
            UseCommandLog = false,
            UseRedis = true,

            // `ride.events` (US-7.16, US-6A.12, P-13, US-20.7) and `registry.events` (D-22/D-23).
            UseKafka = fanout.EventsEnabled,
            UseAuthentication = true,
        };

        builder.AddMageRideDefaults(serviceOptions);

        // SignalR's own convention, and unavoidable: a browser WebSocket cannot set an
        // Authorization header, so the access token travels in the query string
        // (signalr-hub.md §1). Scoped to the hub path — anywhere else, a token in a URL is a token
        // in a proxy log. PostConfigure so the kernel's problem+json challenge handlers survive.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .PostConfigure(bearer =>
            {
                var events = bearer.Events ??= new JwtBearerEvents();
                var inner = events.OnMessageReceived;

                events.OnMessageReceived = async context =>
                {
                    if (inner is not null)
                    {
                        await inner(context);
                    }

                    var token = context.Request.Query[Contract.AccessTokenQueryParam];

                    if (!string.IsNullOrEmpty(token)
                        && context.Request.Path.StartsWithSegments(Contract.Path, StringComparison.Ordinal))
                    {
                        context.Token = token;
                    }
                };
            });

        builder.Services.AddOptions<FanoutOptions>()
            .Bind(builder.Configuration.GetSection(FanoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<ICellSubscriptions, CellSubscriptions>();
        builder.Services.AddSingleton<ICellStreamReader, CellStreamReader>();
        builder.Services.AddSingleton<IVehicleSnapshotReader, VehicleSnapshotReader>();
        builder.Services.AddSingleton<IHubConnections, HubConnections>();
        builder.Services.AddSingleton<IVisibilityIndex, VisibilityIndex>();
        builder.Services.AddSingleton<IEntitlementCache, EntitlementCache>();
        builder.Services.AddSingleton<IDriverVehicles, DriverVehicles>();
        builder.Services.AddSingleton<IRideProjection, RideProjection>();
        builder.Services.AddSingleton<FanoutSignalApplier>();
        builder.Services.AddSingleton<IFanoutControlPlane, RedisFanoutControlPlane>();
        builder.Services.AddScoped<IRideEventHandler, RideEventHandler>();

        // Without this, `Clients.User(...)` addresses nobody: SignalR's default provider reads a
        // claim type the kernel deliberately does not map (see SubjectUserIdProvider).
        builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();

        builder.Services
            .AddSignalR(signalr =>
            {
                signalr.KeepAliveInterval = Contract.KeepAlive;
                signalr.ClientTimeoutInterval = Contract.ClientTimeout;
            })
            .AddJsonProtocol(json =>
            {
                // camelCase, matching the REST surface, so a client can share one set of models
                // between the socket and the API (signalr-hub.md §3, C012).
                json.PayloadSerializerOptions = MageRideJson.Options;
            });

        // Deliberately NOT .AddStackExchangeRedis(). D6' §5 offers a Redis backplane and it would be
        // wrong for the per-cell batches: every replica reads the cell streams it has members in and
        // pushes to its own local group, so coverage is already complete and a backplane would
        // deliver one copy of every batch per replica. The directed sends that genuinely have to
        // cross replicas go over `fanout:control` instead — see RedisFanoutControlPlane.
        if (fanout.ControlPlaneEnabled)
        {
            builder.Services.AddHostedService<FanoutControlSubscriber>();
        }

        // Registered whether or not they are hosted, so a test can step a tick deterministically
        // instead of racing a background loop that is also running.
        builder.Services.AddSingleton<CellStreamPump>();
        builder.Services.AddSingleton<VehicleStreamPump>();

        if (fanout.PumpEnabled)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<CellStreamPump>());
            builder.Services.AddHostedService(services => services.GetRequiredService<VehicleStreamPump>());
        }

        if (fanout.EventsEnabled)
        {
            builder.Services.AddHostedService<RideEventConsumer>();
            builder.Services.AddHostedService<RegistryEventConsumer>();
        }

        if (fanout.PresenceEnabled)
        {
            builder.Services.AddMageRideMqtt(builder.Configuration);
            builder.Services.AddSingleton<PresenceWorker>();
            builder.Services.AddHostedService(services => services.GetRequiredService<PresenceWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapHub<LiveHub>(Contract.Path);

        WarnAboutFiltersThatCannotClose(app, fanout);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of the visibility model are switched off.
    /// </summary>
    /// <remarks>
    /// An open filter looks exactly like a working one from the outside: positions flow, the map is
    /// populated, nothing errors — and the difference only surfaces when somebody sees a vehicle
    /// they should not. position-processor-svc warns about its disabled gates for the same reason,
    /// and this plane's failures are the ones a passenger cannot report.
    /// </remarks>
    private static void WarnAboutFiltersThatCannotClose(WebApplication app, FanoutOptions fanout)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FanoutApplication));

        if (!fanout.EventsEnabled)
        {
            logger.LogWarning(
                "Fanout:EventsEnabled is off: no ride.events and no registry.events. Engaged Mode C "
                + "vehicles stay on the public map (US-7.16), Mode B entitlements are never granted "
                + "or revoked (D-22/D-23), and no ride may be subscribed to.");
        }

        if (!fanout.ControlPlaneEnabled)
        {
            logger.LogWarning(
                "Fanout:ControlPlaneEnabled is off: a directed send reaches only the replica that "
                + "consumed the event. Correct on one replica and a silent half-delivery on any more.");
        }

        if (!fanout.PresenceEnabled)
        {
            logger.LogWarning(
                "Fanout:PresenceEnabled is off: an EMQX last will no longer removes a vehicle from "
                + "the map. The {Window} freshness window is the only remaining half of US-7.17.",
                fanout.FreshnessWindow);
        }

        if (!fanout.PumpEnabled)
        {
            logger.LogWarning("Fanout:PumpEnabled is off: this replica pushes no positions at all.");
        }
    }
}

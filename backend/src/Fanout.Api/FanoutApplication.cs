using MageRide.Fanout.Configuration;
using MageRide.Fanout.Realtime;
using MageRide.Shared;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Redis is the whole of this service's state: it reads the cell streams
            // position-processor-svc writes and holds nothing durable of its own. No Postgres, no
            // outbox, and no Kafka — D6' §5 offers a Redpanda backplane "at scale, > 5 pods", and
            // this slice does not have a backplane at all (see ICellSubscriptions).
            UsePostgres = false,
            UseCommandLog = false,
            UseRedis = true,
            UseKafka = false,
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

        var fanout = builder.Configuration.GetSection(FanoutOptions.SectionName).Get<FanoutOptions>()
                     ?? new FanoutOptions();

        if (fanout.PumpEnabled)
        {
            builder.Services.AddHostedService<CellStreamPump>();
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapHub<LiveHub>(Contract.Path);

        return app;
    }
}

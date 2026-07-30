using System.Net;
using MageRide.Query.Configuration;
using MageRide.Query.Endpoints;
using MageRide.Query.Geo;
using MageRide.Query.Grpc;
using MageRide.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Query;

/// <summary>
/// Composition root for query-svc. Lives here rather than in <c>Program.cs</c> so the test suite drives
/// the same pipeline the process runs.
/// </summary>
public static class QueryApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Redis client id.</summary>
    public const string ServiceName = "query-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Both planes: Redis is the live index (`geo:live`, `veh:meta`, and the three keys
            // fanout-svc maintains), Postgres is the historical one.
            UsePostgres = true,
            UseRedis = true,

            // **No Kafka and no outbox, and that is structural.** This service publishes no event and
            // consumes none: everything it knows it read from state somebody else owns. A read model
            // that also emitted events would be a second source of truth about a fact it does not own.
            UseKafka = false,
            UseOutbox = false,

            // **No command log.** R-14's replay applies to POST mutations and there are none here —
            // every route is a GET. Registering it would create a `query.command_log` table nothing
            // writes and put the idempotency middleware in front of reads that must not be replayed
            // from a cache.
            UseCommandLog = false,

            UseAuthentication = true,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddQueryServices(builder.Configuration);

        var settings = builder.Configuration.GetSection(QueryOptions.SectionName).Get<QueryOptions>()
                       ?? new QueryOptions();

        if (settings.GrpcEnabled)
        {
            ConfigureListeners(builder, settings);

            if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
            {
                builder.Services.AddGrpc(grpc =>
                {
                    grpc.Interceptors.Add<InternalKeyInterceptor>(settings.InternalApiKey);
                    grpc.EnableDetailedErrors = false;
                });
            }
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapNearbyEndpoints();
        app.MapTripEndpoints();
        app.MapEarningsEndpoints();
        app.MapGeoEndpoints();

        if (settings.GrpcEnabled && !string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            // AllowAnonymous because the caller is a service and presents no bearer; the interceptor
            // above is what authenticates it, and the kernel's deny-by-default fallback policy would
            // otherwise 401 every call before the interceptor ran.
            app.MapGrpcService<QueryGrpcService>().AllowAnonymous();
        }

        WarnAboutWhatIsNotBeingEnforced(app, settings);

        return app;
    }

    /// <summary>
    /// Binds two endpoints: HTTP/1.1 for the REST routes and a separate HTTP/2-only one for
    /// <c>query.v1.Query</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>They cannot be the same socket.</b> Cleartext HTTP has no ALPN, so Kestrel cannot negotiate
    /// between HTTP/1.1 and HTTP/2 on one port — an endpoint serving the REST routes answers a gRPC
    /// client's HTTP/2 preface with <c>GOAWAY HTTP_1_1_REQUIRED</c>. reputation-svc (C033) is under the
    /// same constraint and D7' §4.2 gives it a <c>Grpc__ListenPort</c> for exactly this reason;
    /// <b>D7' §4.2 has no row for query-svc</b>, so <c>Query:GrpcListenPort</c> defaults to 5006 — a
    /// micro-change-set in the C042 handoff. It must not be 5005: both services run in the combined
    /// <c>app-services</c> container in the dev compose and would fight over the port.
    /// </para>
    /// <para>
    /// The listener is bound whenever <c>Query:GrpcEnabled</c> is on, even without an internal key. The
    /// key gates whether the <em>service</em> is mapped, not whether the port exists — separating the
    /// two keeps a keyless deployment's failure at "UNIMPLEMENTED/Unauthenticated on a port that
    /// answers" rather than "connection refused", which is the difference between a diagnosable
    /// misconfiguration and an apparent network fault.
    /// </para>
    /// <para>
    /// <c>urls</c> / <c>ASPNETCORE_URLS</c> is still honoured and decides the HTTP endpoints; calling
    /// <c>Listen</c> at all makes Kestrel ignore the URL set, so it is parsed here.
    /// </para>
    /// </remarks>
    private static void ConfigureListeners(WebApplicationBuilder builder, QueryOptions settings)
    {
        var configured = builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"];

        var httpAddresses = string.IsNullOrWhiteSpace(configured)
            ? [$"http://0.0.0.0:{settings.HttpListenPort}"]
            : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var host = IPAddress.Any;

            foreach (var address in httpAddresses)
            {
                var binding = BindingAddress.Parse(address);
                host = ResolveHost(binding.Host);

                kestrel.Listen(host, binding.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            }

            // Second, and the order is load-bearing: IServerAddressesFeature reports bound addresses in
            // Listen order, which is how a caller — and the test harness — tells the gRPC endpoint from
            // the HTTP one when both were given port 0.
            kestrel.Listen(
                host, settings.GrpcListenPort, endpoint => endpoint.Protocols = HttpProtocols.Http2);
        });
    }

    private static IPAddress ResolveHost(string host) => host switch
    {
        "localhost" => IPAddress.Loopback,
        "*" or "+" => IPAddress.Any,
        _ => IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any,
    };

    /// <summary>
    /// Says, once and loudly, which of this service's guarantees are switched off.
    /// </summary>
    /// <remarks>
    /// The same rule position-processor-svc and fanout-svc are written under, and for the same reason:
    /// an open filter is indistinguishable from a working one from the outside. Positions flow, the map
    /// fills, nothing errors — and the difference only surfaces when somebody sees a vehicle, a plate or
    /// a journey that is not theirs to see. Every line below names the specific disclosure or absence
    /// the setting causes.
    /// </remarks>
    private static void WarnAboutWhatIsNotBeingEnforced(WebApplication app, QueryOptions settings)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(QueryApplication));

        if (!settings.VisibilityEnabled)
        {
            logger.LogWarning(
                "Query:VisibilityEnabled is off: GET /v1/nearby returns every vehicle in radius to every "
                + "caller. Engaged Mode C vehicles stay on the public map (US-7.16), stale and offline "
                + "vehicles are drawn as live (US-7.17), and unshared Mode B vehicles are disclosed to "
                + "strangers (D-22/D-23).");
        }
        else if (!settings.EntitlementEnabled)
        {
            logger.LogWarning(
                "Query:EntitlementEnabled is off: every Mode B vehicle is visible to every caller, not "
                + "only to the passengers it is shared with (D-23).");
        }

        if (!settings.OwnRideEnabled)
        {
            logger.LogWarning(
                "Query:OwnRideEnabled is off: a passenger on an active hire cannot see their own vehicle "
                + "in the snapshot (US-7.16's second half), and driverName / registrationNumber are never "
                + "populated (US-7.12).");
        }

        if (!settings.EtaEnabled)
        {
            logger.LogInformation("Query:EtaEnabled is off: etaSeconds is never populated (US-7.11).");
        }

        var geocoder = app.Services.GetRequiredService<IGeocoder>();

        if (!geocoder.IsConfigured)
        {
            logger.LogWarning(
                "Query:NominatimBaseUrl is not set. GET /v1/geo/search answers from the caller's saved and "
                + "recent places only — which looks like a working search box with a thin index — and "
                + "GET /v1/geo/reverse answers 503. There is no third-party geocoder fallback by design "
                + "(D3' map hard rule, D-14).");
        }

        if (string.IsNullOrWhiteSpace(settings.TransitBaseUrl))
        {
            logger.LogWarning(
                "Query:TransitBaseUrl is not set: GET /v1/transport-options offers no public-transport "
                + "options, so US-7.15's trains and buses are missing from the destination screen.");
        }

        if (string.IsNullOrWhiteSpace(settings.FareBaseUrl))
        {
            logger.LogWarning(
                "Query:FareBaseUrl is not set: GET /v1/transport-options offers no Mode C tiers.");
        }

        if (!settings.GrpcEnabled || string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            logger.LogInformation(
                "query.v1.Query is unmapped (Query:GrpcEnabled={Enabled}, InternalApiKey set={HasKey}). "
                + "Internal callers — admin-bff's trips and earnings tabs, fleet-svc's live map — will get "
                + "UNIMPLEMENTED rather than an unauthenticated read surface.",
                settings.GrpcEnabled,
                !string.IsNullOrWhiteSpace(settings.InternalApiKey));
        }
    }
}

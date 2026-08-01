using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Endpoints;
using MageRide.PublicBff.Upstream;
using MageRide.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.PublicBff;

/// <summary>
/// Composition root for public-bff. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs — including the start-up guard.
/// </summary>
public static class PublicBffApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "public-bff";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // The share token, the ride behind it, the location request, the settled payment and the
            // delivery photograph — all read, none written. The only rows this service writes are the
            // token's own meter and burn.
            UsePostgres = true,

            // Three things, and each has to be shared across replicas: the per-token and per-IP
            // buckets (a per-process bucket is a limit on nothing), position-processor-svc's
            // `veh:meta` fix, and the delivery code notification-svc leaves for the recipient's page.
            UseRedis = true,

            // **No producer, no consumer, no outbox.** This service announces nothing: the events
            // the six pages cause are written by the services that own the rows — `sos.raised` by
            // safety-svc, `location.request.confirmed` by ride-svc. A topic here would be a second
            // announcement of somebody else's fact.
            UseKafka = false,
            UseOutbox = false,

            // **No command log.** There is nothing local to replay: both writes are forwarded, and
            // the caller's `Idempotency-Key` — or one derived from the token when a page sends none
            // — travels to the service that owns the operation and its command log. See
            // `PublicIdempotency`.
            UseCommandLog = false,

            // **No authentication scheme, and this is the fence rather than a saving.** AL-44 makes
            // the token the whole credential; a registered JWT handler would be a second way into a
            // surface that has exactly one, and nothing on the six SCR-WT pages could ever present a
            // bearer. `AddMageRideAuthorization` still runs, so the fallback policy is deny — which
            // is what makes the group's explicit `AllowAnonymous` a decision rather than a default.
            UseAuthentication = false,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddPublicBffServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapPublicTrackEndpoints();

        GuardTheSurface(app);
        Announce(app);

        return app;
    }

    /// <summary>
    /// The four fences, checked against the route table before the first request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every route is under <c>/public/track</c>, went through the group, and is anonymous.</b>
    /// The three are one fence seen from three sides: a route mapped elsewhere has not been through
    /// the token gate, a route outside the group carries no scope shaping, and a route that asks for
    /// authorization is a route no SCR-WT page can reach. Asserted here rather than trusted, so a
    /// route added by a later component cannot widen the surface without failing the build.
    /// </para>
    /// <para>
    /// <b>And <c>/call</c> is refused by name.</b> AL-48 removed the proxy-DID lease in full and
    /// several pre-AL-48 spec lines still describe it; somebody implementing from one of those would
    /// otherwise get a service that starts. The check costs one string comparison and is the only
    /// thing standing between an earlier-dated document and a CPaaS dependency.
    /// </para>
    /// <para>
    /// Health probes and the metrics endpoint are the kernel's and are exempt by name: they are
    /// infrastructure, they read nothing about a ride, and D7' §5.1 requires them at the root.
    /// </para>
    /// </remarks>
    internal static void GuardTheSurface(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var problems = new List<string>();

        foreach (var endpoint in app.DataSource().Endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;

            if (IsInfrastructure(route))
            {
                continue;
            }

            if (route.Contains("/call", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{route} looks like the proxy-DID lease AL-48 removed in full. The snapshot carries "
                    + "driver.phone and SCR-WT-002/004 dial it with a plain tel: link (US-26.3) — there is no "
                    + "server-brokered call on this surface, no DID pool and no CPaaS dependency.");
            }

            if (!route.StartsWith(PublicTrackEndpoints.Prefix, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{route} is not under {PublicTrackEndpoints.Prefix}. public-bff serves the six SCR-WT "
                    + "pages and nothing else (AL-44); anything needing an account belongs on a service "
                    + "that has one.");

                continue;
            }

            if (endpoint.Metadata.GetMetadata<PublicSurfaceMarker>() is null)
            {
                problems.Add(
                    $"{route} was mapped outside the public-track group, so it has not been through the "
                    + "token gate. Map it through MapPublicTrackEndpoints.");
            }

            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            {
                problems.Add(
                    $"{route} requires authorization. The share token is the only credential on this "
                    + "surface (AL-44, D-34) and no SCR-WT page has a bearer to present.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "public-bff refuses to start:" + Environment.NewLine + "  - " +
                string.Join(Environment.NewLine + "  - ", problems));
        }
    }

    /// <summary>
    /// Says at start-up which of the two upstreams are reachable and what it costs when they are not.
    /// </summary>
    /// <remarks>
    /// The rule safety-svc states most sharply: an SOS that goes nowhere looks exactly like one that
    /// worked. Here the button would answer 503 rather than 202, which is honest — but a deployment
    /// finds that out from somebody pressing it unless the log says so first.
    /// </remarks>
    private static void Announce(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PublicBffApplication));
        var options = app.Services.GetRequiredService<IOptions<PublicBffOptions>>().Value;

        if (!options.Ride.IsConfigured)
        {
            logger.LogError(
                "PublicBff:Ride:BaseUrl is unset: SCR-WT-003's Share and Decline answer 503, so an "
                + "unregistered rider cannot answer a pickup request at all (AL-45).");
        }

        if (!options.Safety.IsConfigured)
        {
            logger.LogError(
                "PublicBff:Safety:BaseUrl is unset: the web SOS answers 503. No alert is recorded and "
                + "nobody is SMSed (US-25.5, D-33).");
        }

        logger.LogInformation(
            "public-bff serving {Prefix}: {PerToken}/min per token, {PerIp}/min per address; "
            + "positions older than {PositionMaxAge} are omitted.",
            PublicTrackEndpoints.Prefix, options.PerTokenPerMinute, options.PerIpPerMinute, options.PositionMaxAge);
    }

    /// <summary>The kernel's own routes: <c>/health/live</c>, <c>/health/ready</c>, <c>/metrics</c>.</summary>
    private static bool IsInfrastructure(string route) =>
        route.StartsWith("/health", StringComparison.Ordinal)
        || route.StartsWith("/metrics", StringComparison.Ordinal);

    private static EndpointDataSource DataSource(this IEndpointRouteBuilder app) =>
        new CompositeEndpointDataSource(app.DataSources);
}

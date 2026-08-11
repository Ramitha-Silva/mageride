using MageRide.ApiGateway.Attestation;
using MageRide.ApiGateway.Configuration;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.RateLimiting;
using MageRide.ApiGateway.Versioning;
using MageRide.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace MageRide.ApiGateway;

/// <summary>
/// Composition root for the edge. Lives here rather than in <c>Program.cs</c> so the tests exercise
/// the same pipeline the process runs — a gateway whose middleware order is assembled twice is a
/// gateway whose tests prove nothing about production.
/// </summary>
public static class GatewayApplication
{
    /// <summary>Builds the gateway. <paramref name="configure"/> runs after the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        // The route table is its own file so it can be reviewed and diffed on its own; environment
        // variables still layer over it
        // (ReverseProxy__Clusters__ride-svc__Destinations__primary__Address).
        builder.Configuration.AddJsonFile("gateway-routes.json", optional: false, reloadOnChange: true);

        // Δ C125: and the two lines below are what makes the sentence above TRUE. `CreateBuilder` has
        // already added the environment and the command line, and the last source added wins — so the
        // file we just added outranked both, and every
        // `ReverseProxy__Clusters__*__Destinations__primary__Address` in the repository was silently
        // ignored. Two deployment descriptors depended on it and both were dead: the replica's compose
        // pointed all 24 clusters at `http://app-services:5000/` (correct, since the 22 domain
        // services are co-located there) and Kubernetes pointed them at `http://iam-svc/` (correct,
        // since the generated Service listens on port 80). Neither took effect, so both would have
        // used the file's `http://iam-svc:5000/` — a host that does not exist in compose, and a port
        // the Kubernetes Service does not expose. Every route, 502.
        //
        // Re-adding both, in `CreateBuilder`'s own order, restores the conventional precedence:
        // file < environment < command line < whatever `configure` adds. `ClusterAddressPrecedenceTests`
        // pins all four.
        builder.Configuration.AddEnvironmentVariables();

        if (options.Args is { Length: > 0 })
        {
            builder.Configuration.AddCommandLine(options.Args);
        }

        configure?.Invoke(builder);

        var stateStore = builder.Configuration.GetValue(
            $"{GatewayOptions.SectionName}:{nameof(GatewayOptions.StateStore)}", GatewayStateStore.Redis);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = "api-gateway",

            // The edge holds no data and publishes no events. It reads Redis only for the
            // rate-limit buckets and the App Attest replay counters, and even that is optional.
            UsePostgres = false,
            UseCommandLog = false,
            UseKafka = false,
            UseOutbox = false,
            UseRedis = stateStore == GatewayStateStore.Redis,

            // No edge authentication. AL-06 makes authorization deny-by-default *in the services*,
            // which are the only place the caller's role set and the target resource are both
            // known; validating the same token again here would put a JWKS dependency in every
            // request path and open a rotation window in which the edge rejects a token the owning
            // service would have accepted.
            UseAuthentication = false,

            UseTelemetry = true,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.AddMageRideGateway();

        var app = builder.Build();

        // Ahead of the exception handler, as ASP.NET Core recommends: everything downstream — logs,
        // rate-limit buckets, redirects — should already see the real client address and scheme.
        app.UseForwardedHeaders();

        // Ahead of the exception handler too, so an id exists for the response the handler writes.
        app.UseMiddleware<RequestContextMiddleware>();

        app.UseMageRideDefaults(serviceOptions);

        // Ahead of endpoint dispatch: /v1/internal/** must be unreachable whatever the route table
        // says, so it is refused by path rather than by the absence of a route.
        app.UseMiddleware<BlockedPathMiddleware>();

        // Served by the gateway itself, so a client below the floor can still ask what to install.
        app.MapVersionCheck();

        app.MapReverseProxy(proxy =>
        {
            // Turns a 502-with-no-body into the platform's problem+json. Outermost of the proxy
            // pipeline so it also covers a failure raised by YARP's own stages below.
            proxy.UseMiddleware<ForwarderErrorMiddleware>();

            // D-31 first: an unsupported build should be told to update rather than have its
            // attestation rejected for a reason it cannot act on.
            proxy.UseMiddleware<AppVersionGateMiddleware>();

            // D-30 before the limiter, so a forged request cannot consume a genuine caller's tokens.
            proxy.UseMiddleware<AttestationMiddleware>();

            proxy.UseMiddleware<GatewayRateLimitMiddleware>();

            // YARP's own stages. Supplying a custom pipeline replaces the defaults, so these are
            // added back explicitly — omitting them silently disables session affinity, load
            // balancing and passive health checks.
            proxy.UseSessionAffinity();
            proxy.UseLoadBalancing();
            proxy.UsePassiveHealthChecks();
        });

        return app;
    }
}

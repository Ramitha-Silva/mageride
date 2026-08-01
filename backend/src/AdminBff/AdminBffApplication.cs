using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Configuration;
using MageRide.AdminBff.Endpoints;
using MageRide.AdminBff.Upstream;
using MageRide.Shared;
using Microsoft.AspNetCore.Routing;

namespace MageRide.AdminBff;

/// <summary>
/// Composition root for admin-bff. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs — including the two start-up guards.
/// </summary>
public static class AdminBffApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "admin-bff";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Every read model this console renders, plus `audit.events` and the three
            // configuration tables it owns.
            UsePostgres = true,

            // **No Redis.** Nothing here is on a hot path: the dashboard's five figures come from a
            // rollup that is already a derived copy, and a cached KPI would be a second opinion
            // about a number nobody would know was stale. The rate limits this surface needs are
            // the gateway's `admin` bucket (C008).
            UseRedis = false,

            // **Kafka producer, no consumer and no outbox.** D6' §2.1 registers `audit.events` with
            // the producer "all (admin-bff interceptor)" and D7' §4.2 gives this service
            // `Audit__Topic`; that is the one thing published here. There is no outbox because the
            // durable record is the `audit.events` ROW — the topic is §2.1's cold-storage sink, and
            // an outbox would add a table and a dispatcher to guarantee delivery of a copy.
            UseKafka = true,
            UseOutbox = false,

            // **No command log.** Every mutation here is idempotent by shape: a suspension is an
            // upsert of a state, a tariff publish keys on (vehicle_type, effective_from), a feature
            // flag is an upsert, and the two forwarded queues carry the caller's `Idempotency-Key`
            // to services that own their own command logs. Adding an `admin.command_log` would give
            // this surface a fourteenth instance of D4' §5's gap to guard operations that already
            // cannot double-apply.
            UseCommandLog = false,

            UseAuthentication = true,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddAdminBffServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapAdminBffEndpoints();

        GuardTheSurface(app);
        Announce(app);

        return app;
    }

    /// <summary>
    /// The two fences, checked against the route table before the first request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-35 is not a convention here; it is a start-up condition.</b> Every mutating endpoint on
    /// this surface must sit inside the audited group and must declare what it records. A route that
    /// does neither is not a route that quietly misses an audit row — it is a service that does not
    /// start. That is what makes "a mutation without an audit row is a bug" checkable rather than
    /// aspirational, and it is why the interceptor has no off switch to find.
    /// </para>
    /// <para>
    /// <b>AL-02's fence is the same shape.</b> No driver-facing or passenger-facing page is served
    /// from here, which for an API means every route is under <c>/v1/admin</c> — asserted against
    /// the running table rather than trusted, so a route added later cannot widen the surface
    /// without failing the build.
    /// </para>
    /// <para>
    /// Health probes and the metrics endpoint are the kernel's and are exempt by name: they are
    /// infrastructure, they mutate nothing, and D7' §5.1 requires them at the root.
    /// </para>
    /// </remarks>
    internal static void GuardTheSurface(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var problems = new List<string>();

        foreach (var endpoint in app.DataSource().Endpoints.OfType<RouteEndpoint>())
        {
            var route = $"{endpoint.RoutePattern.RawText}";

            if (IsInfrastructure(route))
            {
                continue;
            }

            if (!route.StartsWith(AdminEndpoints.Prefix, StringComparison.Ordinal))
            {
                problems.Add(
                    $"{route} is not under {AdminEndpoints.Prefix}. admin-bff serves the Admin Portal and nothing "
                    + "else (AL-02); a driver-facing or passenger-facing route does not belong here.");

                continue;
            }

            if (endpoint.Metadata.GetMetadata<AdminSurfaceMarker>() is null)
            {
                problems.Add(
                    $"{route} was mapped outside AdminEndpoints' group, so the D-35 audit interceptor is not "
                    + "attached to it. Map it through MapAdminBffEndpoints.");
            }

            if (AuditInterceptor.IsMutating(endpoint) &&
                endpoint.Metadata.GetMetadata<AuditActionMetadata>() is null)
            {
                problems.Add(
                    $"{endpoint.DisplayName ?? route} changes state and declares no audit action. Add "
                    + ".Audited(AdminAuditActions.…, …) — every mutation on this surface writes audit.events (D-35).");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "admin-bff refuses to start:" + Environment.NewLine + "  - " +
                string.Join(Environment.NewLine + "  - ", problems));
        }
    }

    /// <summary>The kernel's own routes: <c>/health/live</c>, <c>/health/ready</c>, <c>/metrics</c>.</summary>
    private static bool IsInfrastructure(string? route) =>
        route is not null &&
        (route.StartsWith("/health", StringComparison.Ordinal) ||
         route.StartsWith("/metrics", StringComparison.Ordinal));

    private static EndpointDataSource DataSource(this IEndpointRouteBuilder app) =>
        new CompositeEndpointDataSource(app.DataSources);

    /// <summary>
    /// Says, once and loudly, which upstreams are missing.
    /// </summary>
    /// <remarks>
    /// The same rule content-svc, support-svc, transit-svc and the rest are written under. It
    /// matters here because an unconfigured upstream is invisible from the outside until an operator
    /// clicks a button and is told the platform is unavailable: the route is in the table, the RBAC
    /// gate opens, and only the forward fails.
    /// </remarks>
    private static void Announce(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);
        var upstream = app.Services.GetRequiredService<IAdminUpstream>();
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminBffOptions>>().Value;

        foreach (var name in AdminUpstreams.All.Where(name => !upstream.IsConfigured(name)))
        {
            logger.LogError(
                "AdminBff:Upstreams:{Upstream}:BaseUrl is unset, so every Admin Portal action that {Upstream} owns "
                + "answers 503 dependency-unavailable. The routes are still mapped and still RBAC-gated — only the "
                + "forward fails.",
                Section(name),
                name);
        }

        if (!options.Audit.PublishToTopic)
        {
            logger.LogWarning(
                "AdminBff:Audit:PublishToTopic is off: audit.events rows are stored in Postgres and NOT mirrored "
                + "onto {Topic}. GET /v1/admin/audit-log is unaffected — D-35's immutable log is the row — but the "
                + "D6' §2.1 cold-storage sink receives nothing.",
                options.Audit.Topic);
        }

        logger.LogInformation(
            "admin-bff is up for the six internal roles (AL-02): deny-by-default RBAC against URD §2.3 on every "
            + "route, the D-35 audit interceptor on every mutation, and no second factor anywhere (AL-37). "
            + "Audit rows go to audit.events and, when enabled, to {Topic}.",
            options.Audit.Topic);
    }

    private static string Section(string upstream) => upstream switch
    {
        AdminUpstreams.Safety => "Safety",
        AdminUpstreams.Support => "Support",
        AdminUpstreams.Content => "Content",
        _ => "Transit",
    };
}

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace MageRide.Contract.Tests.Runtime;

/// <summary>One route a running service actually serves.</summary>
internal sealed record ServiceRoute(string Method, string Template)
{
    public override string ToString() => $"{Method} {Template}";
}

/// <summary>
/// The route table of every service, read from the composed pipeline rather than from source.
///
/// <para>
/// <b>This is the cheapest half of "every operation is exercised against a running service", and it
/// is the half that never flakes.</b> A minimal-API route exists the moment
/// <c>XApplication.Build</c> returns — <c>MapXEndpoints</c> runs during composition — so the table
/// can be read with no database, no broker and no socket. What it proves is exact and two-way: every
/// operation the contract declares is mapped, and every route the service maps is declared. The
/// second direction is the one a request-based sweep cannot give you at all, because you cannot send
/// a request to an endpoint you do not know exists.
/// </para>
///
/// <para>
/// Composition is cached per service: twenty-four pipelines is a second of work, and every test
/// class in this suite wants the same tables.
/// </para>
/// </summary>
internal static partial class ServiceRoutes
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ServiceRoute>> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Routes every service serves that no contract describes, and which are correct not to.
    /// </summary>
    /// <remarks>
    /// The kernel maps these on every service (<c>UseMageRideDefaults</c>): the two health probes
    /// D7' §3 wires the compose health checks to, and the Prometheus scrape D7' §12 asks for. They
    /// are operational surface rather than API — no client calls them, they carry no version prefix,
    /// and putting them in twenty-four contract documents would be twenty-four copies of the same
    /// three lines.
    /// </remarks>
    public static readonly IReadOnlySet<string> OperationalRoutes = new HashSet<string>(StringComparer.Ordinal)
    {
        "/health/live",
        "/health/ready",
        "/health",
        "/metrics",
        // iam-svc's public key set. A well-known URI (RFC 8615) rather than an operation: it takes
        // no credential, has no version prefix, and its shape is RFC 7517's, not this platform's.
        // Every service in the fleet fetches it and no client calls it. Raised in the C118 handoff
        // as a candidate for `iam.yaml` all the same — it is a public endpoint on the edge.
        "/.well-known/jwks.json",
    };

    /// <summary>
    /// Whether a route is outside the surface OpenAPI describes at all.
    /// </summary>
    /// <remarks>
    /// Three kinds, and none of them is drift:
    /// <list type="bullet">
    /// <item>the kernel's operational routes above;</item>
    /// <item><b>gRPC</b> — reputation-svc's D-04 block-status plane and query-svc's read plane are
    /// described by <c>backend/contracts/proto/*.proto</c>, which is the IDL "for the internal
    /// surfaces OpenAPI cannot express". A gRPC method is routed as
    /// <c>/{package}.{Service}/{Method}</c>, so the first segment carries a dot no HTTP path in this
    /// platform does;</item>
    /// <item><b>`OPTIONS`/`HEAD`</b>, which ASP.NET maps alongside a verb on some route groups and
    /// which are not operations.</item>
    /// </list>
    /// </remarks>
    public static bool IsOutsideTheOpenApiSurface(ServiceRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (route.Method is "OPTIONS" or "HEAD" || OperationalRoutes.Contains(route.Template))
        {
            return true;
        }

        var firstSegment = route.Template.TrimStart('/').Split('/')[0];
        return firstSegment.Contains('.', StringComparison.Ordinal)
               // The gRPC catch-all every Grpc.AspNetCore host maps for an unimplemented method.
               || route.Template == "/{}/{}";
    }

    /// <summary>The composed route table of one service.</summary>
    public static IReadOnlyList<ServiceRoute> Of(ServiceDefinition service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return Cache.GetOrAdd(service.Document, _ => Read(service));
    }

    /// <summary>
    /// A template both sides can be compared on.
    /// </summary>
    /// <remarks>
    /// Parameter <b>names</b> are erased. Two things follow, and both are wanted: a route that
    /// spells a segment <c>{id}</c> where its contract spells it <c>{rideId}</c> still counts as the
    /// same operation — which it is, because a name in a path template is not on the wire — and a
    /// document with two paths differing only in a parameter name collapses to one, which is what
    /// OpenAPI says such a document already is. Route constraints (<c>{id:guid}</c>) and optional
    /// markers go the same way, for the same reason: they are a server's parsing detail.
    /// </remarks>
    public static string Normalise(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var path = TemplateParameter().Replace(template.Trim(), "{}");
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static IReadOnlyList<ServiceRoute> Read(ServiceDefinition service)
    {
        var application = ServiceComposition.Compose(service);
        var routes = new List<ServiceRoute>();

        foreach (var endpoint in ((IEndpointRouteBuilder)application).DataSources.SelectMany(source => source.Endpoints))
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            var template = Normalise(route.RoutePattern.RawText ?? string.Empty);
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                          ?? (IReadOnlyList<string>)["GET"];

            foreach (var method in methods)
            {
                routes.Add(new ServiceRoute(method.ToUpperInvariant(), template));
            }
        }

        return routes;
    }

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TemplateParameter();
}

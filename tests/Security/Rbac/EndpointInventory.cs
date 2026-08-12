using System.Collections.Concurrent;
using MageRide.Contract.Tests.Runtime;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MageRide.Security.Tests.Rbac;

/// <summary>How a caller is required to prove itself before an endpoint runs.</summary>
internal enum Guard
{
    /// <summary>
    /// Nothing. No authorization metadata, no <c>AllowAnonymous</c>, and no deny-by-default
    /// fallback on the service that maps it. This is the finding the probe exists to produce.
    /// </summary>
    Open,

    /// <summary>
    /// <c>AllowAnonymous</c>. The credential, if there is one, is an endpoint filter or a value in
    /// the path — neither of which is metadata, so <see cref="AnonymousSurface"/> is what says
    /// which, and a route not named there is treated as <see cref="Open"/>.
    /// </summary>
    Anonymous,

    /// <summary>
    /// The kernel's fallback policy only: an authenticated caller, any role. The endpoint itself
    /// names no permission.
    /// </summary>
    AuthenticatedOnly,

    /// <summary>A role, a fleet sub-role, or a claim named at the endpoint.</summary>
    Role,

    /// <summary>A URD §2.3 (feature area, capability) pair — the AL-06 form.</summary>
    Feature,
}

/// <summary>One endpoint, with what it demands of a caller.</summary>
/// <param name="Service">Contract-document stem, e.g. <c>wallet</c>.</param>
/// <param name="Route">Verb and normalised template, e.g. <c>GET /v1/wallet/{}</c>.</param>
/// <param name="Guard">The strongest thing the caller must present.</param>
/// <param name="Detail">
/// The permission in the words the code used — <c>feature:driver-wallet:Write</c>,
/// <c>role:admin|super_admin</c>, <c>fleet:manager</c>. This is the ASVS evidence column: it comes
/// off the composed pipeline, so it cannot describe a policy the service does not actually apply.
/// </param>
internal sealed record GuardedEndpoint(string Service, string Route, Guard Guard, string Detail)
{
    public string Key => $"{Service} {Route}";

    public override string ToString() => $"{Key} — {Detail}";
}

/// <summary>
/// Every endpoint every service maps, and the authorization each one declares, read off the
/// composed pipeline.
///
/// <para>
/// <b>Why the pipeline and not the source.</b> A grep for <c>RequireFeature</c> finds call sites; it
/// does not find an endpoint mapped in a loop, one whose group carries the policy, or one where a
/// later <c>RequireAuthorization</c> replaced an earlier decision. ASP.NET resolves all of that into
/// endpoint metadata, and metadata is what the authorization middleware actually reads at request
/// time — so reading it here asks the same question the server asks, in the same order.
/// </para>
///
/// <para>
/// <b>What it cannot see, and how that is handled.</b> An <c>IEndpointFilter</c> — every
/// <c>InternalKeyFilter</c>, every signed-link check — is compiled into the request delegate and
/// leaves no metadata. So an anonymous endpoint is classified <see cref="Guard.Anonymous"/> and
/// nothing more; <see cref="AnonymousSurface"/> carries the reviewed reason and the compensating
/// credential for each one, and the probe fails on any anonymous route that is not in it. That
/// makes the anonymous surface a list somebody signed rather than a property of a grep.
/// </para>
/// </summary>
internal static class EndpointInventory
{
    private static readonly Lazy<IReadOnlyList<GuardedEndpoint>> Inventory = new(Build);

    private static readonly ConcurrentDictionary<string, bool> DenyByDefault = new(StringComparer.Ordinal);

    /// <summary>Every endpoint in the fleet, ordered by service then route.</summary>
    public static IReadOnlyList<GuardedEndpoint> All => Inventory.Value;

    /// <summary>The services whose route tables were read. The probe's denominator.</summary>
    public static IReadOnlyList<string> Services { get; } =
        [.. ServiceCatalog.All.Select(static service => service.Document).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Whether a service registered the kernel's deny-by-default fallback policy.
    /// </summary>
    /// <remarks>
    /// Read from the service's own <see cref="AuthorizationOptions"/> rather than assumed from the
    /// fact that it calls <c>AddMageRideAuth</c>. api-gateway clears the fallback deliberately (it
    /// does not authorize — the services do), and a service that cleared it by accident would look
    /// identical from the outside until every unmarked endpoint on it were public.
    /// </remarks>
    public static bool HasDenyByDefaultFallback(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        return DenyByDefault.GetOrAdd(service, static name =>
        {
            var definition = ServiceCatalog.All.Single(entry => entry.Document == name);
            var application = ServiceComposition.Compose(definition);
            var fallback = application.Services.GetRequiredService<IOptions<AuthorizationOptions>>()
                .Value.FallbackPolicy;

            return fallback is not null && RequiresAuthenticatedUser(fallback);
        });
    }

    private static IReadOnlyList<GuardedEndpoint> Build()
    {
        var endpoints = new List<GuardedEndpoint>();

        foreach (var service in ServiceCatalog.All)
        {
            var application = ServiceComposition.Compose(service);
            var denyByDefault = HasDenyByDefaultFallback(service.Document);

            foreach (var endpoint in ((IEndpointRouteBuilder)application).DataSources
                         .SelectMany(static source => source.Endpoints))
            {
                if (endpoint is not RouteEndpoint route)
                {
                    continue;
                }

                var template = ServiceRoutes.Normalise(route.RoutePattern.RawText ?? string.Empty);
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                              ?? (IReadOnlyList<string>)["GET"];

                var (guard, detail) = Classify(endpoint, denyByDefault);

                foreach (var method in methods)
                {
                    // OPTIONS and HEAD are mapped alongside a verb by some route groups and are not
                    // operations. They inherit whatever the verb they shadow declares, so counting
                    // them would double every finding and add none.
                    if (method is "OPTIONS" or "HEAD")
                    {
                        continue;
                    }

                    endpoints.Add(new GuardedEndpoint(
                        service.Document, $"{method.ToUpperInvariant()} {template}", guard, detail));
                }
            }
        }

        return [.. endpoints
            .DistinctBy(static entry => entry.Key)
            .OrderBy(static entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Route, StringComparer.Ordinal)];
    }

    private static (Guard Guard, string Detail) Classify(Endpoint endpoint, bool denyByDefault)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return (Guard.Anonymous, "AllowAnonymous");
        }

        // `RequireAuthorization(policy => …)` puts the BUILT policy in metadata alongside the
        // IAuthorizeData, which is what lets a feature requirement be reported by name instead of
        // as an opaque generated policy id.
        var policy = endpoint.Metadata.GetMetadata<AuthorizationPolicy>();

        if (policy is not null)
        {
            var features = policy.Requirements.OfType<FeaturePermissionRequirement>().ToList();
            if (features.Count > 0)
            {
                return (Guard.Feature, string.Join(
                    " + ", features.Select(static requirement =>
                        $"feature:{requirement.Area.Key}:{requirement.Needed}")));
            }

            var fleet = policy.Requirements.OfType<FleetRoleRequirement>().ToList();
            if (fleet.Count > 0)
            {
                return (Guard.Role, string.Join(
                    " + ", fleet.Select(static requirement => $"fleet:{requirement.MinimumFleetRole}")));
            }

            var claims = policy.Requirements.OfType<ClaimsAuthorizationRequirement>()
                .Where(static requirement => requirement.ClaimType == MageRideClaims.Role)
                .SelectMany(static requirement => requirement.AllowedValues ?? [])
                .ToList();

            if (claims.Count > 0)
            {
                return (Guard.Role, "role:" + string.Join("|", claims.Order(StringComparer.Ordinal)));
            }
        }

        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

        if (authorize.Count > 0)
        {
            var named = authorize
                .Select(static data => data.Policy)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            // A named policy the endpoint asked for by string — `MageRidePolicies.Role(...)` and
            // the fleet-scope policies both arrive this way.
            if (named.Count > 0)
            {
                return (Guard.Role, string.Join("|", named!));
            }

            var roles = authorize
                .Select(static data => data.Roles)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (roles.Count > 0)
            {
                return (Guard.Role, "roles:" + string.Join("|", roles!));
            }

            return (Guard.AuthenticatedOnly, "RequireAuthorization() — authenticated caller, no permission named");
        }

        return denyByDefault
            ? (Guard.AuthenticatedOnly, "kernel fallback policy — authenticated caller, no permission named")
            : (Guard.Open, "no authorization metadata and no deny-by-default fallback on this service");
    }

    /// <summary>
    /// Whether a policy actually demands an authenticated caller, rather than merely existing.
    /// </summary>
    /// <remarks>
    /// <c>DenyAnonymousAuthorizationRequirement</c> is what
    /// <c>AuthorizationPolicyBuilder.RequireAuthenticatedUser</c> adds. A fallback policy built
    /// without it — one that only asserted a claim, say — would let an anonymous request through
    /// on every unmarked endpoint, so the requirement is checked by type rather than inferred from
    /// the policy being non-null.
    /// </remarks>
    private static bool RequiresAuthenticatedUser(AuthorizationPolicy policy) =>
        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any();
}

namespace MageRide.Security.Tests.Rbac;

/// <summary>
/// C127 finding 04, as a regression test: <b>a route whose only credential is the shared internal
/// key must not be addressable from the public internet</b> (ADD §12.2/§12.3, D3' §0).
///
/// <para>
/// <b>Why the prefix convention is not enough on its own.</b> The gateway refuses
/// <c>/v1/internal/**</c> ahead of routing, which covers fifty-odd routes and is the reason a shared
/// secret is tolerable on them at all. Three service-to-service routes were written outside that
/// prefix — <c>POST /v1/fare/calculate</c>, <c>GET /v1/content/templates/{key}</c> and
/// <c>GET /v1/users/lookup</c> — and each was therefore picked up by an ordinary per-service rule
/// and published. Every one of them fails closed on its own filter, so nothing was exploitable; what
/// was lost is the second, independent control, and on the template render the filter degrades to
/// *open* when its key is unset.
/// </para>
///
/// <para>
/// The fix is three entries in <c>Gateway:BlockedPathPrefixes</c>. This test is what stops the
/// fourth such route from being written without one — it joins the endpoint inventory to the edge's
/// own route table, so it fails on the day the route lands rather than on the day somebody reads the
/// route table again.
/// </para>
/// </summary>
public sealed class InternalPlaneExposureTests
{
    [Fact]
    public void No_internal_key_route_is_addressable_from_the_public_edge()
    {
        var exposed = EndpointInventory.All
            .Where(static endpoint => endpoint.Guard == Guard.Anonymous)
            .Select(static endpoint => (endpoint, review: AnonymousSurface.Find(endpoint)))
            .Where(static pair => pair.review?.Credential == AnonymousCredential.InternalKey)
            .Where(static pair => GatewayRouteTable.RoutesFromTheInternet(pair.endpoint.Route))
            .Select(static pair => pair.endpoint.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            exposed.Count == 0,
            $"{exposed.Count} service-to-service route(s) are reachable from the public internet with a shared "
            + "secret as the only credential. Add the path to `Gateway:BlockedPathPrefixes` in "
            + "backend/src/ApiGateway/appsettings.json — every caller uses a direct service address, so the "
            + $"edge does not need to route them:\n  {string.Join("\n  ", exposed)}");
    }

    [Fact]
    public void The_internal_prefix_is_still_the_first_blocked_prefix()
    {
        // The fifty-odd routes under it are the reason the arrangement scales at all. An edit that
        // replaced the list rather than extending it would take the prefix out and leave the three
        // literals looking like the whole control.
        Assert.Contains("/v1/internal", GatewayRouteTable.BlockedPrefixes);
    }

    [Fact]
    public void Blocking_the_lookup_oracle_does_not_block_the_users_own_profile()
    {
        // `/v1/users/lookup` and `/v1/users/me` share a prefix segment, and `StartsWithSegments`
        // is what keeps them apart. Getting this wrong would 404 every profile read on the
        // platform — a failure loud enough to notice, and cheap enough to prevent.
        Assert.False(GatewayRouteTable.RoutesFromTheInternet("GET /v1/users/lookup"));
        Assert.True(GatewayRouteTable.RoutesFromTheInternet("GET /v1/users/me"));
        Assert.True(GatewayRouteTable.RoutesFromTheInternet("GET /v1/users/{}"));

        Assert.False(GatewayRouteTable.RoutesFromTheInternet("POST /v1/fare/calculate"));
        Assert.True(GatewayRouteTable.RoutesFromTheInternet("GET /v1/fare/estimate"));

        Assert.False(GatewayRouteTable.RoutesFromTheInternet("GET /v1/content/templates/{}"));
        Assert.True(GatewayRouteTable.RoutesFromTheInternet("GET /v1/content/onboarding/{}"));
    }
}

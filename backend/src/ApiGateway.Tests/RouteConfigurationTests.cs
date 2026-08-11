using System.Globalization;
using System.Text.Json;
using MageRide.ApiGateway.Configuration;
using MageRide.ApiGateway.RateLimiting;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// Integrity of the two configuration artifacts the edge is built from. These are the mistakes a
/// running-gateway test cannot catch: a route that silently loses its ceiling, a metadata key
/// spelled slightly wrong, a streaming cluster left on YARP's 100 s default.
/// </summary>
public sealed class RouteConfigurationTests
{
    private static readonly JsonElement Routes = Load("gateway-routes.json").GetProperty("ReverseProxy");
    // The gateway's own appsettings.json, which the csproj's GatewaySettingsWinTheOutputDirectory
    // target guarantees is the one in this directory — two referenced projects ship a file by that
    // name, and until C126 the other one was winning.
    private static readonly JsonElement Settings = Load("appsettings.json").GetProperty(GatewayOptions.SectionName);

    private static readonly string[] KnownMetadataKeys =
    [
        GatewayOptions.MetadataKeys.RateLimit,
        GatewayOptions.MetadataKeys.VersionGate,
        GatewayOptions.MetadataKeys.Streaming,
    ];

    public static TheoryData<string> RouteIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var route in Routes.GetProperty("Routes").EnumerateObject())
            {
                if (!route.Name.StartsWith("//", StringComparison.Ordinal))
                {
                    data.Add(route.Name);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RouteIds))]
    public void Every_route_names_a_declared_cluster(string routeId)
    {
        var cluster = Route(routeId).GetProperty("ClusterId").GetString();

        Assert.NotNull(cluster);
        Assert.True(Routes.GetProperty("Clusters").TryGetProperty(cluster!, out _),
            $"Route '{routeId}' points at cluster '{cluster}', which is not declared.");
    }

    [Theory]
    [MemberData(nameof(RouteIds))]
    public void Every_route_is_explicitly_anonymous(string routeId)
    {
        // The gateway does not authorize (AL-06 lives in the services). Saying so on each route
        // makes that a reviewable decision rather than a consequence of an unset field.
        Assert.Equal("anonymous", Route(routeId).GetProperty("AuthorizationPolicy").GetString());
    }

    [Theory]
    [MemberData(nameof(RouteIds))]
    public void Every_route_names_a_configured_rate_limit_policy(string routeId)
    {
        var metadata = Route(routeId).GetProperty("Metadata");

        Assert.True(metadata.TryGetProperty(GatewayOptions.MetadataKeys.RateLimit, out var policy),
            $"Route '{routeId}' declares no {GatewayOptions.MetadataKeys.RateLimit} metadata, so it would fall back to " +
            $"'{GatewayRateLimitOptions.DefaultPolicyName}' by accident rather than by choice.");

        Assert.True(
            Settings.GetProperty("RateLimits").GetProperty("Policies").TryGetProperty(policy.GetString()!, out _),
            $"Route '{routeId}' names rate-limit policy '{policy.GetString()}', which appsettings.json does not define.");
    }

    [Theory]
    [MemberData(nameof(RouteIds))]
    public void Every_metadata_key_is_one_the_gateway_reads(string routeId)
    {
        foreach (var entry in Route(routeId).GetProperty("Metadata").EnumerateObject())
        {
            Assert.Contains(entry.Name, KnownMetadataKeys);
        }
    }

    [Theory]
    [MemberData(nameof(RouteIds))]
    public void A_version_gate_exemption_uses_the_only_value_that_means_anything(string routeId)
    {
        if (Route(routeId).GetProperty("Metadata").TryGetProperty(GatewayOptions.MetadataKeys.VersionGate, out var value))
        {
            Assert.Equal(GatewayOptions.ExemptValue, value.GetString());
        }
    }

    [Theory]
    [MemberData(nameof(RouteIds))]
    public void Every_route_path_is_on_a_public_prefix(string routeId)
    {
        var path = Route(routeId).GetProperty("Match").GetProperty("Path").GetString()!;

        Assert.True(
            path.StartsWith("/v1/", StringComparison.Ordinal)
            || path.StartsWith("/public/", StringComparison.Ordinal)
            || path.StartsWith("/hubs/", StringComparison.Ordinal),
            $"Route '{routeId}' matches '{path}', which is outside the /v1, /public and /hubs prefixes the platform serves.");

        foreach (var blocked in Settings.GetProperty("BlockedPathPrefixes").EnumerateArray())
        {
            Assert.False(path.StartsWith(blocked.GetString()! + "/", StringComparison.OrdinalIgnoreCase),
                $"Route '{routeId}' matches a blocked prefix; it would be dead config at best.");
        }
    }

    [Fact]
    public void A_streaming_route_gets_a_cluster_that_can_hold_a_websocket_open()
    {
        var streaming = Routes.GetProperty("Routes").EnumerateObject()
            .Where(static r => r.Value.TryGetProperty("Metadata", out var m)
                && m.TryGetProperty(GatewayOptions.MetadataKeys.Streaming, out var s)
                && s.GetString() == "true")
            .ToArray();

        Assert.NotEmpty(streaming);

        foreach (var route in streaming)
        {
            var cluster = Routes.GetProperty("Clusters").GetProperty(route.Value.GetProperty("ClusterId").GetString()!);

            Assert.True(cluster.TryGetProperty("HttpRequest", out var http),
                $"Streaming route '{route.Name}' has a cluster with no HttpRequest block, so it keeps YARP's 100 s idle timeout.");

            var timeout = TimeSpan.Parse(http.GetProperty("ActivityTimeout").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(timeout >= TimeSpan.FromMinutes(5),
                $"Streaming route '{route.Name}' would drop a quiet-but-live connection after {timeout}.");

            // A WebSocket upgrade cannot be negotiated over HTTP/2.
            Assert.Equal("1.1", http.GetProperty("Version").GetString());
        }
    }

    [Fact]
    public void Every_cluster_has_a_reachable_destination()
    {
        foreach (var cluster in Routes.GetProperty("Clusters").EnumerateObject())
        {
            if (cluster.Name.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var destinations = cluster.Value.GetProperty("Destinations").EnumerateObject().ToArray();
            Assert.NotEmpty(destinations);

            foreach (var destination in destinations)
            {
                var address = destination.Value.GetProperty("Address").GetString();
                Assert.True(Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                    $"Cluster '{cluster.Name}' destination '{destination.Name}' has address '{address}'.");
            }
        }
    }

    /// <summary>
    /// Δ C126. The one route the whole platform's authentication depends on, pinned in all three of
    /// the ways it can be broken.
    /// </summary>
    /// <remarks>
    /// Every service fetches the public signing key over this route to validate a bearer (D-29,
    /// D-21). It did not exist until C126's day-0 load became the first thing in this repository to
    /// present a real token to a deployed service and got <c>500 internal-error</c> from
    /// <c>JwksConfigurationManager</c>: <c>env/.env.common.example</c> named
    /// <c>/v1/internal/iam/.well-known/jwks.json</c>, which BlockedPathMiddleware refuses ahead of
    /// routing. Nothing else catches this — the fetch happens on the first authenticated request,
    /// not at readiness, and the smoke suites assert 401s, which need no key.
    /// </remarks>
    [Fact]
    public void The_jwks_route_is_reachable_exempt_and_rewritten()
    {
        var route = Route("iam-jwks");

        Assert.Equal("iam-svc", route.GetProperty("ClusterId").GetString());

        // Reachable: /v1, because RouteConfigurationTests holds the edge to three prefixes and
        // `/v1/internal/**` — the address this used to name — is blocked before routing.
        Assert.Equal("/v1/.well-known/jwks.json", route.GetProperty("Match").GetProperty("Path").GetString());

        // Rewritten: iam-svc serves the root path the JWKS specification expects, and the /v1 prefix
        // the edge requires has to come off somewhere. Without this the fetch is a 404 from iam-svc
        // instead of a 404 from the gateway — the same outage, one hop later.
        var transforms = route.GetProperty("Transforms").EnumerateArray().ToArray();
        Assert.Contains(transforms, t => t.TryGetProperty("PathSet", out var p) && p.GetString() == "/.well-known/jwks.json");

        // Exempt: a service fetching a key sends none of D-31's client headers, and a 426 here would
        // break authentication platform-wide rather than ask anybody to upgrade.
        Assert.Equal(
            GatewayOptions.ExemptValue,
            route.GetProperty("Metadata").GetProperty(GatewayOptions.MetadataKeys.VersionGate).GetString());
    }

    [Fact]
    public void Every_declared_rate_limit_policy_is_used_by_some_route()
    {
        var used = Routes.GetProperty("Routes").EnumerateObject()
            .Where(static r => !r.Name.StartsWith("//", StringComparison.Ordinal))
            .Select(static r => r.Value.GetProperty("Metadata").GetProperty(GatewayOptions.MetadataKeys.RateLimit).GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declared = Settings.GetProperty("RateLimits").GetProperty("Policies").EnumerateObject()
            .Where(static p => !p.Name.StartsWith("//", StringComparison.Ordinal))
            .Select(static p => p.Name)
            .ToArray();

        var unused = declared.Where(p => !used.Contains(p)).ToArray();

        Assert.True(unused.Length == 0, "Rate-limit policies no route uses: " + string.Join(", ", unused));
    }

    private static JsonElement Route(string routeId) => Routes.GetProperty("Routes").GetProperty(routeId);

    private static JsonElement Load(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName)));
        return document.RootElement.Clone();
    }
}

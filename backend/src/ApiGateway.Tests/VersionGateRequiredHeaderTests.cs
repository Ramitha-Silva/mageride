using System.Net;
using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.Shared.Http;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// The opt-in strict mode: with <c>RequirePlatformHeader</c> a proxied request that does not name
/// a platform is refused rather than waved through. Off by default because the portals and the
/// AL-44 public track pages are browsers.
/// </summary>
public sealed class VersionGateRequiredHeaderTests : IAsyncLifetime
{
    private GatewayHarness _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
    {
        ["Gateway:VersionGate:RequirePlatformHeader"] = "true",
    });

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task A_request_without_a_platform_is_refused()
    {
        using var response = await _gateway.Client.GetAsync("/v1/users/me");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);

        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("upgrade-required", problem.Code);

        // The three members stay present so a client deserialiser sees one shape either way, even
        // though no platform was known and therefore no store link can be offered.
        Assert.True(problem.Root.TryGetProperty("updateUrl", out _));
        Assert.True(problem.Root.TryGetProperty("latestVersion", out _));
        Assert.True(problem.GetBoolean("isMandatory"));
    }

    [Fact]
    public async Task An_exempt_route_is_still_reachable_without_a_platform()
    {
        using var response = await _gateway.Client.GetAsync("/public/track/abc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_current_app_is_unaffected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/users/me");
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Ios);
        request.Headers.Add(MageRideHeaders.AppVersion, "9.9.9");

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

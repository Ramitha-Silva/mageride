using System.Net;
using System.Text.Json;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.Shared.Http;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// D-31 (DoD: "a request below the version floor gets 426 with the documented body").
/// <para>
/// The floor used throughout: hard 1.4.0, soft 1.6.0, latest 1.6.2.
/// </para>
/// </summary>
public sealed class VersionGateTests : IAsyncLifetime
{
    private const string UpdateUrl = "https://play.google.com/store/apps/details?id=lk.mageride.driver";

    private GatewayHarness _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
    {
        ["Gateway:VersionGate:Platforms:android:MinimumVersion"] = "1.4.0",
        ["Gateway:VersionGate:Platforms:android:RecommendedVersion"] = "1.6.0",
        ["Gateway:VersionGate:Platforms:android:LatestVersion"] = "1.6.2",
        ["Gateway:VersionGate:Platforms:android:UpdateUrl"] = UpdateUrl,
        ["Gateway:VersionGate:Platforms:ios:MinimumVersion"] = "1.4.0",
        ["Gateway:VersionGate:Platforms:ios:RecommendedVersion"] = "1.6.0",
        ["Gateway:VersionGate:Platforms:ios:LatestVersion"] = "1.6.2",
        ["Gateway:VersionGate:Platforms:ios:UpdateUrl"] = "https://apps.apple.com/lk/app/mageride/id0000000000",
    });

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Below_the_floor_the_edge_answers_426_with_the_documented_body()
    {
        using var response = await SendAsync("/v1/users/me", ClientPlatforms.Android, "1.3.9");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.False(response.Headers.Contains(GatewayTransforms.UpstreamHeaderName),
            "A request below the floor must not reach a service.");

        var problem = await ProblemDocument.ReadAsync(response);

        // D3' §0: "below floor -> 426 Upgrade Required with body {updateUrl, latestVersion, isMandatory}".
        Assert.Equal("upgrade-required", problem.Code);
        Assert.Equal(UpdateUrl, problem.GetStringOrNull("updateUrl"));
        Assert.Equal("1.6.2", problem.GetStringOrNull("latestVersion"));
        Assert.True(problem.GetBoolean("isMandatory"));
    }

    [Theory]
    [InlineData("1.4.0")]      // exactly at the hard floor
    [InlineData("1.5.9")]      // above the hard floor, below the soft one
    [InlineData("1.6.2")]      // current
    [InlineData("2.0.0+118")]  // ahead of the floor, with a build suffix
    public async Task At_or_above_the_floor_the_request_is_forwarded(string version)
    {
        using var response = await SendAsync("/v1/users/me", ClientPlatforms.Android, version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("iam-svc", response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }

    [Fact]
    public async Task A_pre_release_of_the_floor_version_is_below_it()
    {
        // Semver orders 1.4.0-rc.1 below 1.4.0; a release candidate is not the release.
        using var response = await SendAsync("/v1/users/me", ClientPlatforms.Android, "1.4.0-rc.1");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Fact]
    public async Task An_unreadable_version_is_treated_as_below_the_floor()
    {
        using var response = await SendAsync("/v1/users/me", ClientPlatforms.Android, "not-a-version");

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
    }

    [Theory]
    [InlineData(null, null)]          // a portal: neither header
    [InlineData("web", "1.0.0")]      // a platform with no configured floor
    [InlineData(null, "1.0.0")]       // version without platform: nothing to compare against
    public async Task A_caller_that_is_not_one_of_our_apps_is_not_gated(string? platform, string? version)
    {
        using var response = await SendAsync("/v1/users/me", platform, version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_exempt_route_is_not_gated()
    {
        // AL-44's /public/track pages are opened in a browser from an SMS link.
        using var response = await SendAsync("/public/track/abc", ClientPlatforms.Android, "0.0.1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public-bff", response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }

    [Fact]
    public async Task A_client_below_the_floor_can_still_reach_the_version_check()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/version/check?platform=android&current=1.0.0");
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Android);
        request.Headers.Add(MageRideHeaders.AppVersion, "1.0.0");

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    // current, updateRequired, isMandatory
    [InlineData("1.0.0", true, true)]    // below the hard floor
    [InlineData("1.4.0", true, false)]   // at the hard floor, below the soft one: dismissible prompt
    [InlineData("1.5.9", true, false)]
    [InlineData("1.6.0", false, false)]  // at the soft floor
    [InlineData("1.7.0", false, false)]  // ahead of the store build
    public async Task Version_check_reports_the_same_verdict_the_gate_enforces(
        string current, bool updateRequired, bool isMandatory)
    {
        using var response = await _gateway.Client.GetAsync($"/v1/version/check?platform=android&current={current}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        Assert.Equal(updateRequired, body.GetProperty("updateRequired").GetBoolean());
        Assert.Equal(isMandatory, body.GetProperty("isMandatory").GetBoolean());
        Assert.Equal("1.6.2", body.GetProperty("latestVersion").GetString());
        Assert.Equal(UpdateUrl, body.GetProperty("updateUrl").GetString());
    }

    [Theory]
    [InlineData("/v1/version/check")]
    [InlineData("/v1/version/check?platform=android")]
    [InlineData("/v1/version/check?current=1.0.0")]
    [InlineData("/v1/version/check?platform=windows&current=1.0.0")]
    [InlineData("/v1/version/check?platform=android&current=one.two.three")]
    public async Task Version_check_rejects_a_malformed_query(string url)
    {
        using var response = await _gateway.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("validation-failed", problem.Code);
        Assert.True(problem.Root.TryGetProperty("errors", out _), "validation-failed carries an errors map (D3' §0).");
    }

    private Task<HttpResponseMessage> SendAsync(string path, string? platform, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (platform is not null)
        {
            request.Headers.Add(MageRideHeaders.Platform, platform);
        }

        if (version is not null)
        {
            request.Headers.Add(MageRideHeaders.AppVersion, version);
        }

        return _gateway.Client.SendAsync(request);
    }
}

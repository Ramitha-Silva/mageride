using System.Net;
using MageRide.ApiGateway.Attestation;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.Shared.Http;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// The three enforcement modes and the per-platform override that lets Android ship (Wave 4a)
/// while iOS is still in build (Wave 4b).
/// </summary>
public sealed class AttestationModeTests
{
    [Fact]
    public async Task Disabled_forwards_a_sensitive_operation_with_no_header()
    {
        await using var gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Gateway:Attestation:Mode"] = nameof(AttestationMode.Disabled),
        });

        using var response = await PostSosAsync(gateway, ClientPlatforms.Android);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("safety-svc", response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }

    [Fact]
    public async Task Audit_forwards_a_failure_instead_of_rejecting_it()
    {
        await using var gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Gateway:Attestation:Mode"] = nameof(AttestationMode.Audit),
        });

        using var response = await PostSosAsync(gateway, ClientPlatforms.Android);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("safety-svc", response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }

    [Fact]
    public async Task A_platform_override_beats_the_global_mode()
    {
        await using var gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Gateway:Attestation:Mode"] = nameof(AttestationMode.Enforce),
            ["Gateway:Attestation:PlatformModes:ios"] = nameof(AttestationMode.Audit),
        });

        using var android = await PostSosAsync(gateway, ClientPlatforms.Android);
        Assert.Equal(HttpStatusCode.Unauthorized, android.StatusCode);

        using var ios = await PostSosAsync(gateway, ClientPlatforms.Ios);
        Assert.Equal(HttpStatusCode.OK, ios.StatusCode);
    }

    private static Task<HttpResponseMessage> PostSosAsync(GatewayHarness gateway, string platform)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sos");
        request.Headers.Add(MageRideHeaders.Platform, platform);
        request.Headers.Add(MageRideHeaders.AppVersion, "9.9.9");
        return gateway.Client.SendAsync(request);
    }
}

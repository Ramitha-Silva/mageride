using System.Net;
using MageRide.ApiGateway.Attestation;
using MageRide.ApiGateway.Http;
using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.Shared.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// D-30 (DoD: "a sensitive route without a valid attestation header gets 401 attestation-failed;
/// a non-sensitive route is unaffected").
/// </summary>
public sealed class AttestationEnforcementTests : IAsyncLifetime
{
    /// <summary>
    /// Above every configured floor, so the D-31 gate never fires first and these assertions stay
    /// about D-30. The two gates are matched: a client that names a platform must name a version.
    /// </summary>
    private const string CurrentAppVersion = "9.9.9";

    private GatewayHarness _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHarness.StartAsync(new Dictionary<string, string?>
    {
        ["Gateway:Attestation:Mode"] = nameof(AttestationMode.Enforce),
    });

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Theory]
    // One per sensitive family D3' §0 names: auth, payments, ride accept, wallet, SOS.
    [InlineData("POST", "/v1/auth/otp/request")]
    [InlineData("POST", "/v1/auth/otp/verify")]
    [InlineData("POST", "/v1/fare/pay")]
    [InlineData("POST", "/v1/fare/pay/driver-qr/confirm")]
    [InlineData("POST", "/v1/rides/request")]
    [InlineData("POST", "/v1/rides/01JZ/offer/01JY/accept")]
    [InlineData("POST", "/v1/wallet/topup/onepay")]
    [InlineData("POST", "/v1/wallet/credit-transfer/initiate")]
    [InlineData("POST", "/v1/sos")]
    [InlineData("POST", "/v1/vouchers/purchase")]
    [InlineData("POST", "/v1/trackers/bind")]
    [InlineData("POST", "/v1/fleets/01JZ/trackers/bulk")]
    public async Task A_sensitive_operation_without_an_attestation_header_is_refused(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Android);
        request.Headers.Add(MageRideHeaders.AppVersion, CurrentAppVersion);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains(GatewayTransforms.UpstreamHeaderName),
            $"{method} {path} reached a service without attestation.");

        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("attestation-failed", problem.Code);
    }

    [Fact]
    public async Task A_present_but_unverifiable_token_is_refused()
    {
        // No Play Integrity package name is configured in the test harness, so the verifier has
        // nothing to check the token against and must fail closed.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sos");
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Android);
        request.Headers.Add(MageRideHeaders.AppVersion, CurrentAppVersion);
        request.Headers.Add(MageRideHeaders.Attestation, "a-token-that-is-not-a-play-integrity-verdict");

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("attestation-failed", problem.Code);

        // The reason is a log line, never a client-facing oracle for tuning a bypass.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("play-integrity", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_oversize_header_is_refused_without_being_parsed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/sos");
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Android);
        request.Headers.Add(MageRideHeaders.AppVersion, CurrentAppVersion);
        request.Headers.Add(MageRideHeaders.Attestation, new string('a', 9000));

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    // Same paths, methods the contracts do not mark sensitive.
    [InlineData("GET", "/v1/sos/01JZ/history", "safety-svc")]
    [InlineData("GET", "/v1/wallet/01JZ", "wallet-svc")]
    [InlineData("GET", "/v1/wallet/01JZ/transactions", "wallet-svc")]
    [InlineData("GET", "/v1/rides/01JZ", "ride-svc")]
    [InlineData("GET", "/v1/rides/history", "ride-svc")]
    [InlineData("POST", "/v1/auth/refresh", "iam-svc")]
    [InlineData("POST", "/v1/auth/logout", "iam-svc")]
    [InlineData("POST", "/v1/rides/01JZ/cancel", "ride-svc")]
    [InlineData("POST", "/v1/fare/pay/01JZ/fallback-cash", "fare-svc")]
    [InlineData("GET", "/v1/nearby", "query-svc")]
    public async Task A_non_sensitive_operation_is_unaffected(string method, string path, string cluster)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Android);
        request.Headers.Add(MageRideHeaders.AppVersion, CurrentAppVersion);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(cluster, response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }

    [Fact]
    public async Task Sensitivity_is_per_operation_not_per_path()
    {
        // POST /v1/rides/request is attested; GET on the same path is not an operation at all and
        // must not inherit the requirement (it routes on to ride-svc, which will 405 it).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/rides/request");
        request.Headers.Add(MageRideHeaders.Platform, ClientPlatforms.Android);
        request.Headers.Add(MageRideHeaders.AppVersion, CurrentAppVersion);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal("ride-svc", response.Headers.GetValues(GatewayTransforms.UpstreamHeaderName).First());
    }

    [Fact]
    public async Task A_sensitive_operation_without_a_platform_is_refused()
    {
        // Without X-Platform there is no verifier to pick, so there is no way to accept it.
        using var response = await _gateway.Client.PostAsync("/v1/sos", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ProblemDocument.ReadAsync(response);
        Assert.Equal("attestation-failed", problem.Code);
    }

    [Fact]
    public void The_enforced_operation_set_is_exactly_the_contract_set()
    {
        var configured = _gateway.Services
            .GetRequiredService<IOptionsMonitor<AttestationOptions>>().CurrentValue
            .SensitiveOperations
            .SelectMany(o => o.Methods.Select(m => $"{m.ToUpperInvariant()} {o.Path}"))
            .ToHashSet(StringComparer.Ordinal);

        var declared = ContractCatalog.Operations
            .Where(static o => o.RequiresAttestation)
            .Select(static o => $"{o.Method} {o.Template}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(declared);

        var missing = declared.Except(configured, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = configured.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0,
            "Contracts declare X-Attestation on operations the gateway does not enforce: " + string.Join(", ", missing));
        Assert.True(extra.Length == 0,
            "The gateway enforces attestation on operations no contract marks sensitive: " + string.Join(", ", extra));
    }
}

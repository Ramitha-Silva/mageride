using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.ApiGateway.Attestation;
using MageRide.ApiGateway.Tests.Infrastructure;
using MageRide.Shared.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MageRide.ApiGateway.Tests;

/// <summary>
/// The Android half of D-30: the gateway hands the <c>X-Attestation</c> token to Google's
/// <c>decodeIntegrityToken</c> and judges the decoded verdicts. Google is stubbed; the JWT-bearer
/// grant, the request shape and every verdict check are the real ones.
/// </summary>
public sealed class PlayIntegrityVerifierTests
{
    private const string PackageName = "lk.mageride.driver";
    private const string Token = "an-opaque-play-integrity-token";

    private static readonly DateTimeOffset Now = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_recognised_app_on_an_intact_device_is_accepted()
    {
        var (verifier, google) = Build(Payload());

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.True(result.IsValid, result.Reason);

        // One token exchange plus one decode: the grant is real, not skipped.
        Assert.Contains(google.Requests, r => r.Contains("oauth2", StringComparison.Ordinal));
        Assert.Contains(google.Requests, r => r.Contains(":decodeIntegrityToken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verdict_for_another_package_is_refused()
    {
        var (verifier, _) = Build(Payload(packageName: "com.example.repackaged"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-package-mismatch", result.Reason);
    }

    [Fact]
    public async Task A_repackaged_app_is_refused()
    {
        var (verifier, _) = Build(Payload(appVerdict: "UNRECOGNIZED_VERSION"));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-app-verdict", result.Reason);
    }

    [Fact]
    public async Task A_device_that_fails_integrity_is_refused()
    {
        var (verifier, _) = Build(Payload(deviceVerdicts: ["MEETS_BASIC_INTEGRITY"]));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-device-verdict", result.Reason);
    }

    [Fact]
    public async Task A_stale_token_is_refused()
    {
        // Bounds how long a token captured off the wire stays useful.
        var (verifier, _) = Build(Payload(timestamp: Now.AddMinutes(-30)));

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-token-stale", result.Reason);
    }

    [Fact]
    public async Task A_token_google_rejects_is_refused()
    {
        var (verifier, _) = Build(Payload(), decodeStatus: HttpStatusCode.BadRequest);

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-token-rejected", result.Reason);
    }

    [Fact]
    public async Task Google_being_unreachable_fails_closed()
    {
        // An outage must not silently switch D-30 off.
        var (verifier, _) = Build(Payload(), decodeStatus: HttpStatusCode.InternalServerError);

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-unavailable", result.Reason);
    }

    [Fact]
    public async Task A_positive_verdict_is_cached_but_a_rejection_is_not()
    {
        var (verifier, google) = Build(Payload());

        Assert.True((await verifier.VerifyAsync(Request(), CancellationToken.None)).IsValid);
        var afterFirst = google.Requests.Count(static r => r.Contains(":decodeIntegrityToken", StringComparison.Ordinal));

        Assert.True((await verifier.VerifyAsync(Request(), CancellationToken.None)).IsValid);
        var afterSecond = google.Requests.Count(static r => r.Contains(":decodeIntegrityToken", StringComparison.Ordinal));

        Assert.Equal(afterFirst, afterSecond);

        // A different token gets its own decision — the cache is keyed on the token, not global.
        google.Payload = Payload(appVerdict: "UNRECOGNIZED_VERSION");
        var other = await verifier.VerifyAsync(
            new AttestationRequest(ClientPlatforms.Android, "another-token", "POST", "/v1/sos"), CancellationToken.None);
        Assert.False(other.IsValid);

        // ...and the rejection is not remembered, so a transient failure cannot pin a device out.
        google.Payload = Payload();
        var retry = await verifier.VerifyAsync(
            new AttestationRequest(ClientPlatforms.Android, "another-token", "POST", "/v1/sos"), CancellationToken.None);
        Assert.True(retry.IsValid, retry.Reason);
    }

    [Fact]
    public async Task An_unconfigured_verifier_fails_closed()
    {
        var (verifier, _) = Build(Payload(), packageName: string.Empty);

        var result = await verifier.VerifyAsync(Request(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("play-integrity-not-configured", result.Reason);
    }

    private static AttestationRequest Request() => new(ClientPlatforms.Android, Token, "POST", "/v1/sos");

    private static string Payload(
        string? packageName = null,
        string appVerdict = "PLAY_RECOGNIZED",
        string[]? deviceVerdicts = null,
        DateTimeOffset? timestamp = null) =>
        JsonSerializer.Serialize(new
        {
            tokenPayloadExternal = new
            {
                requestDetails = new
                {
                    requestPackageName = packageName ?? PackageName,
                    timestampMillis = (timestamp ?? Now).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    nonce = "c29tZS1ub25jZQ",
                },
                appIntegrity = new { appRecognitionVerdict = appVerdict, packageName = packageName ?? PackageName },
                deviceIntegrity = new { deviceRecognitionVerdict = deviceVerdicts ?? ["MEETS_DEVICE_INTEGRITY"] },
                accountDetails = new { appLicensingVerdict = "LICENSED" },
            },
        });

    private static (PlayIntegrityVerifier Verifier, GoogleStub Google) Build(
        string payload,
        HttpStatusCode decodeStatus = HttpStatusCode.OK,
        string? packageName = null)
    {
        var google = new GoogleStub { Payload = payload, DecodeStatus = decodeStatus };

        var services = new ServiceCollection();
        services.AddHttpClient(PlayIntegrityVerifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => google);
        var provider = services.BuildServiceProvider();

        var options = new AttestationOptions
        {
            PlayIntegrity =
            {
                PackageName = packageName ?? PackageName,
                Endpoint = "https://playintegrity.test",
                ServiceAccountJson = ServiceAccountJson(),
            },
        };

        var verifier = new PlayIntegrityVerifier(
            provider.GetRequiredService<IHttpClientFactory>(),
            new TestOptionsMonitor<AttestationOptions>(options),
            new MemoryCache(new MemoryCacheOptions()),
            new FakeTimeProvider(Now),
            NullLogger<PlayIntegrityVerifier>.Instance);

        return (verifier, google);
    }

    private static string ServiceAccountJson()
    {
        using var rsa = RSA.Create(2048);

        return JsonSerializer.Serialize(new
        {
            type = "service_account",
            client_email = "gateway@mageride.iam.gserviceaccount.com",
            private_key_id = "test-key",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            token_uri = "https://oauth2.test/token",
        });
    }

    /// <summary>Stands in for both Google endpoints the verifier talks to.</summary>
    private sealed class GoogleStub : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public required string Payload { get; set; }

        public HttpStatusCode DecodeStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(uri);
            }

            if (uri.Contains("oauth2", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"access_token":"stub-access-token","expires_in":3600,"token_type":"Bearer"}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(DecodeStatus)
            {
                Content = new StringContent(
                    DecodeStatus == HttpStatusCode.OK ? Payload : """{"error":{"code":400,"message":"stub"}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}

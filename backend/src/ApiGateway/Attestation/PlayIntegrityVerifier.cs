using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Shared.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// Android half of D-30. Sends the <c>X-Attestation</c> header — a Play Integrity token — to
/// Google's <c>decodeIntegrityToken</c> and checks the decoded verdicts against configuration.
/// </summary>
internal sealed class PlayIntegrityVerifier(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<AttestationOptions> options,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<PlayIntegrityVerifier> logger) : IAttestationVerifier
{
    internal const string HttpClientName = "play-integrity";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IOptionsMonitor<AttestationOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IMemoryCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<PlayIntegrityVerifier> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _tokenSourceGate = new(1, 1);
    private GoogleServiceAccountTokenSource? _tokenSource;
    private string? _tokenSourceKeyFingerprint;

    public string Platform => ClientPlatforms.Android;

    public async ValueTask<AttestationResult> VerifyAsync(
        AttestationRequest request, CancellationToken cancellationToken)
    {
        var settings = _options.CurrentValue.PlayIntegrity;

        if (string.IsNullOrWhiteSpace(settings.PackageName))
        {
            return AttestationResult.Invalid("play-integrity-not-configured");
        }

        var cacheKey = CacheKey(request.Token);
        if (settings.VerdictCacheDuration > TimeSpan.Zero
            && _cache.TryGetValue<AttestationResult>(cacheKey, out var cached))
        {
            return cached;
        }

        AttestationResult result;
        try
        {
            result = await DecodeAndCheckAsync(request.Token, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Google unreachable, credentials wrong, JSON shape changed: fail closed. An open
            // failure mode here would make D-30 a control that any outage switches off.
            _logger.LogError(ex, "Play Integrity decode failed; rejecting the request.");
            return AttestationResult.Invalid("play-integrity-unavailable");
        }

        // Only a positive verdict is cached. Caching a rejection would pin a device out for the
        // cache duration after a transient failure.
        if (result.IsValid && settings.VerdictCacheDuration > TimeSpan.Zero)
        {
            _cache.Set(cacheKey, result, settings.VerdictCacheDuration);
        }

        return result;
    }

    private async Task<AttestationResult> DecodeAndCheckAsync(
        string token, PlayIntegrityOptions settings, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var tokenSource = await GetTokenSourceAsync(settings, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.RequestTimeout);

        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"{settings.Endpoint.TrimEnd('/')}/v1/{Uri.EscapeDataString(settings.PackageName)}:decodeIntegrityToken");

        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new { integrity_token = token }),
        };

        var accessToken = await tokenSource.GetAccessTokenAsync(settings.Scope, timeout.Token).ConfigureAwait(false);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(message, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 400 from Google means the token itself is unusable — malformed, wrong package, or
            // already too old for Google to decode. That is a rejection, not an outage.
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            _logger.LogWarning(
                "decodeIntegrityToken returned {Status}: {Body}", (int)response.StatusCode, Truncate(body));

            return (int)response.StatusCode is >= 400 and < 500
                ? AttestationResult.Invalid("play-integrity-token-rejected")
                : AttestationResult.Invalid("play-integrity-unavailable");
        }

        using var document = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token)
            .ConfigureAwait(false);

        return Check(document.RootElement, settings);
    }

    private AttestationResult Check(JsonElement root, PlayIntegrityOptions settings)
    {
        if (!root.TryGetProperty("tokenPayloadExternal", out var payload))
        {
            return AttestationResult.Invalid("play-integrity-no-payload");
        }

        if (payload.TryGetProperty("requestDetails", out var requestDetails))
        {
            var packageName = GetString(requestDetails, "requestPackageName");
            if (!string.Equals(packageName, settings.PackageName, StringComparison.Ordinal))
            {
                return AttestationResult.Invalid("play-integrity-package-mismatch");
            }

            if (settings.MaxTokenAge > TimeSpan.Zero)
            {
                var timestamp = GetString(requestDetails, "timestampMillis");
                if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
                {
                    return AttestationResult.Invalid("play-integrity-no-timestamp");
                }

                var age = _timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(millis);
                if (age > settings.MaxTokenAge || age < -settings.MaxTokenAge)
                {
                    return AttestationResult.Invalid("play-integrity-token-stale");
                }
            }
        }
        else
        {
            return AttestationResult.Invalid("play-integrity-no-request-details");
        }

        if (settings.RequiredAppVerdicts.Count > 0)
        {
            var appVerdict = payload.TryGetProperty("appIntegrity", out var appIntegrity)
                ? GetString(appIntegrity, "appRecognitionVerdict")
                : null;

            if (appVerdict is null || !settings.RequiredAppVerdicts.Contains(appVerdict, StringComparer.Ordinal))
            {
                return AttestationResult.Invalid("play-integrity-app-verdict");
            }
        }

        if (settings.RequiredDeviceVerdicts.Count > 0)
        {
            var verdicts = payload.TryGetProperty("deviceIntegrity", out var deviceIntegrity)
                && deviceIntegrity.TryGetProperty("deviceRecognitionVerdict", out var labels)
                && labels.ValueKind == JsonValueKind.Array
                    ? labels.EnumerateArray().Select(static v => v.GetString()).ToArray()
                    : [];

            if (!settings.RequiredDeviceVerdicts.Any(required => verdicts.Contains(required, StringComparer.Ordinal)))
            {
                return AttestationResult.Invalid("play-integrity-device-verdict");
            }
        }

        if (settings.RequiredLicensingVerdicts.Count > 0)
        {
            var licensing = payload.TryGetProperty("accountDetails", out var accountDetails)
                ? GetString(accountDetails, "appLicensingVerdict")
                : null;

            if (licensing is null || !settings.RequiredLicensingVerdicts.Contains(licensing, StringComparer.Ordinal))
            {
                return AttestationResult.Invalid("play-integrity-licensing-verdict");
            }
        }

        return AttestationResult.Valid();
    }

    private async ValueTask<GoogleServiceAccountTokenSource> GetTokenSourceAsync(
        PlayIntegrityOptions settings, CancellationToken cancellationToken)
    {
        var json = settings.ServiceAccountJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            if (string.IsNullOrWhiteSpace(settings.ServiceAccountJsonPath))
            {
                throw new InvalidOperationException(
                    "Gateway:Attestation:PlayIntegrity needs ServiceAccountJson or ServiceAccountJsonPath.");
            }

            json = await File.ReadAllTextAsync(settings.ServiceAccountJsonPath, cancellationToken).ConfigureAwait(false);
        }

        var fingerprint = Fingerprint(json);
        if (_tokenSource is not null && _tokenSourceKeyFingerprint == fingerprint)
        {
            return _tokenSource;
        }

        await _tokenSourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenSource is null || _tokenSourceKeyFingerprint != fingerprint)
            {
                _tokenSource = new GoogleServiceAccountTokenSource(
                    _httpClientFactory.CreateClient(HttpClientName), ServiceAccountKey.Parse(json), _timeProvider);
                _tokenSourceKeyFingerprint = fingerprint;
            }

            return _tokenSource;
        }
        finally
        {
            _tokenSourceGate.Release();
        }
    }

    private static string CacheKey(string token) => "attest:play:" + Fingerprint(token);

    /// <summary>Hash rather than the value itself: the cache holds no attestation token in clear.</summary>
    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];
}

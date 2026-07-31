using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Notification.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Push;

/// <summary>
/// Mints the OAuth2 access token FCM HTTP v1 authenticates with.
/// </summary>
/// <remarks>
/// <para>
/// HTTP v1 replaced the legacy server key with a short-lived bearer obtained from a service-account
/// assertion: an RS256 JWT signed with the account's private key, exchanged at Google's token
/// endpoint for something valid for an hour. Hand-rolled rather than taken from the Google client
/// libraries because the whole exchange is forty lines, and the alternative is a transitive
/// dependency tree on a build host that has to stay small (see the root CLAUDE.md).
/// </para>
/// <para>
/// <b>The token is cached and refreshed early.</b> A push is on E-01's three-second budget and a
/// token fetched in the middle of it would spend most of the window; the refresh happens a minute
/// before expiry, behind a semaphore so a burst of offers mints one token rather than one each.
/// </para>
/// </remarks>
public sealed class GoogleAccessTokenSource : IDisposable
{
    /// <summary>The named client the token exchange goes over.</summary>
    public const string HttpClientName = "google-oauth";

    /// <summary>The single scope FCM HTTP v1 needs.</summary>
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    /// <summary>Refresh this far before expiry, so an in-flight push never races the rollover.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _clients;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt;

    public GoogleAccessTokenSource(
        IHttpClientFactory clients, IOptions<NotificationOptions> options, TimeProvider clock)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>True when a service account is configured well enough to mint anything.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.FcmClientEmail) && !string.IsNullOrWhiteSpace(_options.FcmPrivateKeyPem);

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        if (_token is not null && _expiresAt - RefreshMargin > now)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            now = _clock.GetUtcNow();

            if (_token is not null && _expiresAt - RefreshMargin > now)
            {
                return _token;
            }

            var (token, lifetime) = await ExchangeAsync(now, cancellationToken);

            _token = token;
            _expiresAt = now + lifetime;

            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> ExchangeAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Notification:FcmClientEmail / FcmPrivateKeyPem are not configured, so no FCM access token can be minted.");
        }

        var assertion = SignedAssertion(now);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        };

        var client = _clients.CreateClient(HttpClientName);

        using var response = await client.PostAsync(
            _options.GoogleTokenUrl, new FormUrlEncodedContent(form), cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Google refused the FCM service-account assertion with {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);

        var token = document.RootElement.TryGetProperty("access_token", out var accessToken)
            ? accessToken.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new HttpRequestException("Google's token response carried no access_token.");
        }

        var seconds = document.RootElement.TryGetProperty("expires_in", out var expiresIn)
                      && expiresIn.TryGetInt32(out var value)
            ? value
            : 3600;

        return (token, TimeSpan.FromSeconds(seconds));
    }

    private string SignedAssertion(DateTimeOffset now)
    {
        var issuedAt = now.ToUnixTimeSeconds();

        var header = JwtCodec.Segment(new { alg = "RS256", typ = "JWT" });
        var claims = JwtCodec.Segment(new
        {
            iss = _options.FcmClientEmail,
            scope = Scope,
            aud = _options.GoogleTokenUrl,
            iat = issuedAt,
            exp = issuedAt + 3600,
        });

        var signingInput = $"{header}.{claims}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_options.FcmPrivateKeyPem);

        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{JwtCodec.Base64Url(signature)}";
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// The two lines of JWT encoding both provider tokens need.
/// </summary>
/// <remarks>
/// Hand-rolled deliberately: this service signs two provider assertions and validates none, so
/// pulling in a token library would add a dependency to produce base64url and a JSON object. The
/// bearer this service *accepts* is the kernel's, which uses the real validator.
/// </remarks>
internal static class JwtCodec
{
    public static string Segment(object value) =>
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

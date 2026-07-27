using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MageRide.ApiGateway.Attestation;

/// <summary>
/// Mints an OAuth 2.0 access token for a Google service account with the JWT-bearer grant
/// (RFC 7523), so the gateway can call <c>decodeIntegrityToken</c>.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from <c>Google.Apis.Auth</c>: the grant is one signed JWT and one
/// form post, while the library drags in the whole Google API client stack for it. The signed
/// assertion never leaves this class and the resulting token is cached until shortly before expiry.
/// </remarks>
internal sealed class GoogleServiceAccountTokenSource(
    HttpClient httpClient, ServiceAccountKey key, TimeProvider timeProvider)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ServiceAccountKey _key = key ?? throw new ArgumentNullException(nameof(key));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Renew this long before the token actually expires, so an in-flight call cannot land on a dead token.</summary>
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(2);

    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async ValueTask<string> GetAccessTokenAsync(string scope, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_accessToken is not null && now < _expiresAt - RenewalMargin)
        {
            return _accessToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_accessToken is not null && now < _expiresAt - RenewalMargin)
            {
                return _accessToken;
            }

            var assertion = CreateAssertion(scope, now);

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", assertion),
            ]);

            using var response = await _httpClient.PostAsync(_key.TokenUri, content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content
                .ReadFromJsonAsync<TokenResponse>(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Google token endpoint returned an empty body.");

            if (string.IsNullOrEmpty(payload.AccessToken))
            {
                throw new InvalidOperationException("Google token endpoint returned no access_token.");
            }

            _accessToken = payload.AccessToken;
            _expiresAt = now.AddSeconds(payload.ExpiresInSeconds <= 0 ? 3600 : payload.ExpiresInSeconds);
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CreateAssertion(string scope, DateTimeOffset now)
    {
        var header = new Dictionary<string, object?>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["kid"] = _key.PrivateKeyId,
        };

        var claims = new Dictionary<string, object?>
        {
            ["iss"] = _key.ClientEmail,
            ["scope"] = scope,
            ["aud"] = _key.TokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(30).ToUnixTimeSeconds(),
        };

        var signingInput = string.Concat(
            Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header)),
            ".",
            Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims)));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_key.PrivateKeyPem);

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return string.Concat(signingInput, ".", Base64UrlEncode(signature));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresInSeconds { get; init; }
    }
}

/// <summary>The fields the gateway needs out of a Google service-account key file.</summary>
internal sealed record ServiceAccountKey(string ClientEmail, string PrivateKeyPem, string? PrivateKeyId, string TokenUri)
{
    public static ServiceAccountKey Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var clientEmail = ReadString(root, "client_email")
            ?? throw new InvalidOperationException("Service-account key has no client_email.");
        var privateKey = ReadString(root, "private_key")
            ?? throw new InvalidOperationException("Service-account key has no private_key.");

        return new ServiceAccountKey(
            clientEmail,
            privateKey,
            ReadString(root, "private_key_id"),
            ReadString(root, "token_uri") ?? "https://oauth2.googleapis.com/token");
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"ServiceAccountKey({ClientEmail})");
}

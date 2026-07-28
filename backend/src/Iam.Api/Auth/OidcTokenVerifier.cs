using System.Net.Http.Json;
using MageRide.Iam.Configuration;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Auth;

/// <summary>The external identity providers AL-07 puts on the portals.</summary>
public static class IdentityProviders
{
    /// <summary>Admin Portal and Fleet Portal.</summary>
    public const string Google = "google";

    /// <summary>Fleet Portal only.</summary>
    public const string Apple = "apple";

    public static bool IsKnown(string? provider) => provider is Google or Apple;
}

/// <summary>What a verified provider ID token asserts.</summary>
/// <param name="Provider"><see cref="IdentityProviders"/>.</param>
/// <param name="Subject">The provider's <c>sub</c> — the only identity it guarantees is stable.</param>
/// <param name="Email">The asserted address, lower-cased. May be absent (Apple private relay
/// aside, a Services-ID token can omit it on repeat sign-ins).</param>
/// <param name="EmailVerified">Whether the provider says it verified that address. An unverified
/// address must never match an existing MageRide account.</param>
public sealed record FederatedPrincipal(string Provider, string Subject, string? Email, bool EmailVerified);

/// <summary>Supplies a provider's current signing keys.</summary>
/// <remarks>
/// A seam rather than an implementation detail: it is what lets the sign-in tests drive the whole
/// verification path — signature, issuer, audience, expiry — against a locally minted key instead
/// of stubbing the verifier and testing nothing.
/// </remarks>
public interface IOidcKeySource
{
    Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(string provider, CancellationToken cancellationToken);
}

/// <summary>Verifies a Google or Apple ID token and reduces it to the identity it asserts.</summary>
public interface IOidcTokenVerifier
{
    Task<FederatedPrincipal> VerifyAsync(string provider, string? idToken, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOidcTokenVerifier"/>
/// <remarks>
/// <para>
/// Three checks carry the weight, and skipping any one of them turns "sign in with Google" into
/// "sign in": the <b>signature</b> against the provider's published keys, the <b>issuer</b>, and
/// the <b>audience</b>. The audience is the one most often left out — an ID token minted for
/// somebody else's OAuth client is a perfectly valid Google token, and accepting it would let any
/// app on the internet mint MageRide admin sessions. <see cref="GoogleOidcOptions.ClientIds"/>
/// therefore has no default and an empty list refuses every token rather than accepting all of
/// them.
/// </para>
/// <para>
/// Nothing here consults MageRide state. Mapping the verified identity onto an account — and
/// refusing to create one — is <c>PortalSignInService</c>'s job.
/// </para>
/// </remarks>
public sealed class OidcTokenVerifier(
    IOidcKeySource keys, IOptions<OidcOptions> options, ILogger<OidcTokenVerifier> logger) : IOidcTokenVerifier
{
    private static readonly JsonWebTokenHandler Handler = new();

    private readonly OidcOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FederatedPrincipal> VerifyAsync(
        string provider, string? idToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["idToken"] = ["idToken is required."],
            });
        }

        var (issuers, audiences) = provider switch
        {
            IdentityProviders.Google => (_options.Google.Issuers, _options.Google.ClientIds),
            IdentityProviders.Apple => (_options.Apple.Issuers, _options.Apple.ClientIds),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown identity provider."),
        };

        if (audiences.Count == 0)
        {
            // Configuration, not a caller error: the portal was never wired up. Refusing is the
            // only safe answer — validating with no audience accepts every token Google ever
            // minted, for any client.
            logger.LogError(
                "Oidc:{Provider}:ClientIds is empty, so no {Provider} ID token can be accepted (AL-07)", provider, provider);
            throw new MageRideException(
                MageRideErrors.Forbidden, $"{provider} sign-in is not configured on this deployment.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuers = issuers,
            ValidAudiences = audiences,
            IssuerSigningKeys = await keys.GetKeysAsync(provider, cancellationToken),
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        var result = await Handler.ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid)
        {
            logger.LogWarning(result.Exception, "Rejected a {Provider} ID token", provider);
            throw new MageRideException(MageRideErrors.Unauthorized, $"The {provider} ID token could not be verified.");
        }

        var claims = result.Claims;
        var subject = Claim(claims, JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new MageRideException(MageRideErrors.Unauthorized, $"The {provider} ID token carries no subject.");
        }

        var email = Claim(claims, JwtRegisteredClaimNames.Email);

        return new FederatedPrincipal(
            provider,
            subject,
            string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
            EmailVerified(claims));
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value as string : null;

    /// <summary>
    /// Google sends <c>email_verified</c> as a JSON boolean, Apple as the string <c>"true"</c>.
    /// Both spellings mean the same thing and neither is negotiable — an unverified address is
    /// an address the provider let somebody type in.
    /// </summary>
    private static bool EmailVerified(IDictionary<string, object> claims)
    {
        if (!claims.TryGetValue("email_verified", out var value))
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false,
        };
    }
}

/// <summary>
/// Fetches a provider's JWKS over HTTPS and caches it for the D-21 window.
/// </summary>
/// <remarks>
/// The same 15-minute cache the platform gives its own key set, for the same reason: Google
/// rotates keys on its own schedule and a fetch per sign-in would be both slow and rude. A fetch
/// that fails while a cached set is held serves the cached set — a provider blip must not lock
/// every admin out.
/// </remarks>
public sealed class HttpOidcKeySource(
    IHttpClientFactory httpClientFactory,
    IOptions<OidcOptions> options,
    ILogger<HttpOidcKeySource> logger,
    TimeProvider timeProvider) : IOidcKeySource
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "oidc-jwks";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private readonly OidcOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (IReadOnlyCollection<SecurityKey> Keys, DateTimeOffset FetchedAt)> _cache =
        new(StringComparer.Ordinal);

    public async Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(
        string provider, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var now = timeProvider.GetUtcNow();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(provider, out var cached) && now - cached.FetchedAt < CacheDuration)
            {
                return cached.Keys;
            }

            var url = provider switch
            {
                IdentityProviders.Google => _options.Google.JwksUrl,
                IdentityProviders.Apple => _options.Apple.JwksUrl,
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown identity provider."),
            };

            try
            {
                var client = httpClientFactory.CreateClient(HttpClientName);
                var json = await client.GetStringAsync(url, cancellationToken);
                IReadOnlyCollection<SecurityKey> keys = [.. JsonWebKeySet.Create(json).GetSigningKeys()];

                if (keys.Count == 0)
                {
                    throw new InvalidOperationException($"The JWKS at {url} contains no signing keys.");
                }

                _cache[provider] = (keys, timeProvider.GetUtcNow());
                return keys;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_cache.TryGetValue(provider, out var stale))
                {
                    logger.LogWarning(ex, "Could not refresh the {Provider} JWKS; continuing with the cached key set", provider);
                    return stale.Keys;
                }

                logger.LogError(ex, "Could not fetch the {Provider} JWKS from {Url}", provider, url);
                throw new MageRideException(
                    MageRideErrors.DependencyUnavailable, $"{provider} sign-in is temporarily unavailable.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Exchanges the Admin Portal's Google authorization code for an <c>id_token</c>
/// (Δ 2026-06-28 item 5).
/// </summary>
public interface IGoogleAuthCodeExchange
{
    Task<string> ExchangeAsync(string code, string? redirectUri, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGoogleAuthCodeExchange"/>
/// <remarks>
/// The Admin Portal redirect gives the browser a one-time code, not a token, so the code has to
/// be redeemed server-side with the client secret. That is the whole reason the admin arm takes a
/// code rather than an ID token: the secret never reaches the browser, and the code is spent by
/// the first party to redeem it.
/// </remarks>
public sealed class GoogleAuthCodeExchange(
    IHttpClientFactory httpClientFactory,
    IOptions<OidcOptions> options,
    ILogger<GoogleAuthCodeExchange> logger) : IGoogleAuthCodeExchange
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "google-token";

    private readonly GoogleOidcOptions _options =
        options?.Value.Google ?? throw new ArgumentNullException(nameof(options));

    public async Task<string> ExchangeAsync(string code, string? redirectUri, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (_options.ClientIds.Count == 0 || string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            logger.LogError("Oidc:Google:ClientIds/ClientSecret are not configured; the admin Google arm cannot run");
            throw new MageRideException(MageRideErrors.Forbidden, "Google sign-in is not configured on this deployment.");
        }

        var redirect = redirectUri ?? _options.RedirectUri;
        if (string.IsNullOrWhiteSpace(redirect))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["redirectUri"] = ["redirectUri is required when Oidc:Google:RedirectUri is not configured."],
            });
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["client_id"] = _options.ClientIds[0],
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        });

        GoogleTokenResponse? payload;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(_options.TokenEndpoint, form, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // A spent, expired or mismatched code is a caller problem, not an outage.
                logger.LogWarning("Google refused the authorization code with {Status}", (int)response.StatusCode);
                throw new MageRideException(MageRideErrors.Unauthorized, "The Google authorization code was rejected.");
            }

            payload = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Could not reach the Google token endpoint");
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "Google sign-in is temporarily unavailable.");
        }

        if (string.IsNullOrWhiteSpace(payload?.IdToken))
        {
            throw new MageRideException(MageRideErrors.Unauthorized, "Google returned no ID token for that code.");
        }

        return payload.IdToken;
    }

    private sealed record GoogleTokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("id_token")] string? IdToken);
}

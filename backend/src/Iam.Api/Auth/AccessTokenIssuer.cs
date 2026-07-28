using MageRide.Iam.Configuration;
using MageRide.Shared.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Auth;

/// <summary>What goes into one access token.</summary>
/// <param name="UserId"><c>sub</c>.</param>
/// <param name="Roles">Every canonical role held; effective permissions are their union (AL-06).</param>
/// <param name="DeviceKey">The client's <c>deviceId</c> — the <c>device_id</c> claim (AL-08).</param>
/// <param name="App"><c>passenger</c> | <c>driver</c> (AL-08).</param>
/// <param name="SessionId">The <c>iam.sessions.jti</c> this token belongs to, so a revoked
/// session can be correlated with the access tokens issued under it (ADD §12.1).</param>
public sealed record AccessTokenRequest(
    Guid UserId,
    IReadOnlyCollection<string> Roles,
    string DeviceKey,
    string App,
    Guid SessionId);

/// <summary>An issued access token and the instant it stops being valid.</summary>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

/// <summary>Mints the RS256 access tokens D-29 specifies.</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(AccessTokenRequest request);
}

/// <inheritdoc cref="IAccessTokenIssuer"/>
public sealed class AccessTokenIssuer(
    SigningKeyRing keys, IOptions<TokenOptions> options, TimeProvider timeProvider) : IAccessTokenIssuer
{
    private static readonly JsonWebTokenHandler Handler = new();

    private readonly TokenOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public AccessToken Issue(AccessTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Roles.Count == 0)
        {
            throw new ArgumentException("An access token must carry at least one role (AL-06).", nameof(request));
        }

        var issuedAt = timeProvider.GetUtcNow();
        var expiresAt = issuedAt + _options.AccessTokenLifetime;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            // A single-element array would serialise "role" as an array even for the common
            // one-role case; the claim is repeated only when the union actually has more.
            [MageRideClaims.Role] = request.Roles.Count == 1 ? request.Roles.First() : request.Roles.ToArray(),
            [MageRideClaims.DeviceId] = request.DeviceKey,
            [MageRideClaims.App] = request.App,
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audiences.Count > 0 ? _options.Audiences[0] : null,
            Claims = claims,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = keys.SigningCredentials,
        };

        // Set through AdditionalHeaderClaims/registered names rather than Claims so the handler
        // does not emit them twice.
        descriptor.Claims[JwtRegisteredClaimNames.Sub] = request.UserId.ToString();
        descriptor.Claims[JwtRegisteredClaimNames.Jti] = request.SessionId.ToString();

        return new AccessToken(
            Handler.CreateToken(descriptor),
            expiresAt,
            (int)_options.AccessTokenLifetime.TotalSeconds);
    }
}

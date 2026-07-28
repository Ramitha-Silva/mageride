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
/// <param name="App"><c>passenger</c> | <c>driver</c> | <c>admin</c> | <c>fleet</c> (AL-08, 0107).</param>
/// <param name="SessionId">The <c>iam.sessions.jti</c> this token belongs to, so a revoked
/// session can be correlated with the access tokens issued under it (ADD §12.1).</param>
/// <param name="FleetRole">Org-scoped sub-role, <c>owner</c> | <c>manager</c> | <c>viewer</c>
/// (AL-03). Present only for a member of a fleet.</param>
/// <param name="FleetId">The fleet <paramref name="FleetRole"/> applies to. A sub-role without
/// the org it is scoped to would be a permission over every fleet.</param>
public sealed record AccessTokenRequest(
    Guid UserId,
    IReadOnlyCollection<string> Roles,
    string DeviceKey,
    string App,
    Guid SessionId,
    string? FleetRole = null,
    Guid? FleetId = null);

/// <summary>An issued access token and the instant it stops being valid.</summary>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

/// <summary>Mints the RS256 access tokens D-29 specifies.</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(AccessTokenRequest request);
}

/// <summary>
/// <inheritdoc cref="IAccessTokenIssuer"/>
/// </summary>
/// <remarks>
/// One claim set for every surface (D3' §0 "Auth"): <c>sub</c>, <c>role</c>, <c>fleet_role?</c>,
/// <c>device_id</c>, <c>app</c>, plus the <c>jti</c> that ties the token to its session row. A
/// portal sign-in and a phone-OTP sign-in differ in the *values* — <c>app</c>, and whether a fleet
/// pair is present — never in the shape, because every consumer of these tokens (the gateway,
/// nine services, EMQX) reads one shape.
/// </remarks>
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

        // Both or neither (AL-03). `fleet_role` alone would read as a privilege over every fleet,
        // which is what ClaimsPrincipalExtensions.TryGetFleetScope refuses to return.
        if (request.FleetRole is { } fleetRole && request.FleetId is { } fleetId && fleetId != Guid.Empty)
        {
            claims[MageRideClaims.FleetRole] = fleetRole;
            claims[MageRideClaims.FleetId] = fleetId.ToString();
        }

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

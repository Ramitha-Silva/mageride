using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.FleetHealth.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// fleet-health-svc is a token consumer: it holds no signing key and, in production, resolves
/// iam-svc's public half over JWKS. Standing a whole iam-svc up to get a bearer would test C020 again
/// and make this suite fail for reasons that are not this component's.
/// </remarks>
internal sealed class TestTokenIssuer
{
    private const string Issuer = "https://iam.mageride.test";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SigningCredentials _credentials;

    public TestTokenIssuer()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        _credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        PublicKey = new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = "test-key" };
    }

    /// <summary>What the harness gives the bearer handler in place of a JWKS fetch.</summary>
    public SecurityKey PublicKey { get; }

    public string IssuerName => Issuer;

    /// <summary>
    /// A Fleet Portal sign-in (AL-03/AL-07): the <c>fleet_owner</c> role plus the org-scoped
    /// <c>fleet_id</c> and <c>fleet_role</c> claims the endpoint checks the path against.
    /// </summary>
    public string FleetUser(Guid userId, Guid fleetId, string fleetRole = FleetRoles.Owner) =>
        Issue(
            userId,
            [MageRideRoles.FleetOwner],
            MageRideApps.Fleet,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [MageRideClaims.FleetId] = fleetId.ToString(),
                [MageRideClaims.FleetRole] = fleetRole,
            });

    /// <summary>A fleet owner whose token carries no org scope — the AL-03 claim-less case.</summary>
    public string UnscopedFleetUser(Guid userId) =>
        Issue(userId, [MageRideRoles.FleetOwner], MageRideApps.Fleet, extra: null);

    /// <summary>A back-office admin. AL-06 gives the two platform roles blanket authority.</summary>
    public string Admin(Guid userId) => Issue(userId, [MageRideRoles.Admin], MageRideApps.Admin, extra: null);

    /// <summary>A driver. On this surface, refused by the role gate.</summary>
    public string Driver(Guid userId) => Issue(userId, [MageRideRoles.Driver], MageRideApps.Driver, extra: null);

    public string Issue(
        Guid userId,
        IReadOnlyCollection<string> roles,
        string app,
        IReadOnlyDictionary<string, object>? extra)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            [MageRideClaims.Role] = roles.Count == 1 ? roles.First() : roles.ToArray(),
            [MageRideClaims.App] = app,
            [MageRideClaims.DeviceId] = "test-device",
        };

        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                claims[key] = value;
            }
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = _credentials,
        };

        return Handler.CreateToken(descriptor);
    }
}

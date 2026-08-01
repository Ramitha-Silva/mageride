using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.FleetBilling.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// <para>
/// fleet-billing-svc is a token consumer: it holds no signing key and, in production, resolves
/// iam-svc's public half over JWKS. Standing a whole iam-svc up to get a bearer would test C020
/// again and make this suite fail for reasons that are not this component's.
/// </para>
/// <para>
/// <b>The <c>fleet_role</c> claim is deliberately settable to something the membership row does not
/// say.</b> That combination is the one the access filter exists for: iam-svc puts the caller's
/// <em>most privileged</em> membership in the token (C027), so an Owner of fleet A arrives at
/// fleet B's billing carrying <c>fleet_role=owner</c> — and must be refused on the membership row
/// rather than admitted on the claim.
/// </para>
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

    /// <summary>A Fleet Portal user, with whatever sub-role claim the caller wants to test.</summary>
    public string FleetUser(Guid userId, Guid? fleetId = null, string? fleetRole = null)
    {
        var extra = new Dictionary<string, object>(StringComparer.Ordinal);

        if (fleetId is { } fleet)
        {
            extra[MageRideClaims.FleetId] = fleet.ToString();
        }

        if (fleetRole is not null)
        {
            extra[MageRideClaims.FleetRole] = fleetRole;
        }

        return Issue(userId, [MageRideRoles.FleetOwner], MageRideApps.Fleet, extra);
    }

    /// <summary>A driver, for the tests that prove a bearer alone reaches nothing here.</summary>
    public string Driver(Guid userId) => Issue(userId, [MageRideRoles.Driver], MageRideApps.Driver);

    public string Issue(
        Guid userId,
        IReadOnlyCollection<string> roles,
        string app,
        IReadOnlyDictionary<string, object>? extra = null)
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

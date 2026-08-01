using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Payout.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// payout-svc is a token consumer: it holds no signing key and, in production, resolves iam-svc's
/// public half over JWKS. Standing a whole iam-svc up to get a bearer would test C020 again and make
/// this suite fail for reasons that are not this component's.
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

    public SecurityKey PublicKey { get; }

    public string IssuerName => Issuer;

    public string Driver(Guid userId) => Issue(userId, MageRideApps.Driver, MageRideRoles.Driver);

    public string FinanceOfficer(Guid userId) => Issue(userId, MageRideApps.Admin, MageRideRoles.FinanceOfficer);

    public string Admin(Guid userId) => Issue(userId, MageRideApps.Admin, MageRideRoles.Admin);

    public string Issue(Guid userId, string app, params string[] roles)
    {
        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            [MageRideClaims.Role] = roles,
            [MageRideClaims.App] = app,
            [MageRideClaims.DeviceId] = "test-device",
        };

        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = _credentials,
        });
    }
}

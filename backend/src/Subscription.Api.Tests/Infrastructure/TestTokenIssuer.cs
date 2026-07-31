using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Subscriptions.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// subscription-svc and wallet-svc are both token consumers: neither holds a signing key and both, in
/// production, resolve iam-svc's public half over JWKS. Standing a whole iam-svc up to get a bearer
/// would test C020 again and make this suite fail for reasons that are not this component's.
/// <para>
/// One issuer serves both services in this harness, which is also what production does — a bearer the
/// driver's app presents to subscription-svc is the same one the forwarded route carries to wallet-svc.
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

    /// <summary>A passenger on the passenger app.</summary>
    public string Passenger(Guid userId) =>
        Issue(userId, [MageRideRoles.Passenger], MageRideApps.Passenger);

    /// <summary>A driver on the driver app.</summary>
    public string Driver(Guid userId) => Issue(userId, [MageRideRoles.Driver], MageRideApps.Driver);

    /// <summary>A back-office admin (AL-06's blanket platform role).</summary>
    public string Admin(Guid userId) => Issue(userId, [MageRideRoles.Admin], MageRideApps.Admin);

    /// <summary>A Finance Officer — the third role the voucher-tier admin surface admits.</summary>
    public string FinanceOfficer(Guid userId) =>
        Issue(userId, [MageRideRoles.FinanceOfficer], MageRideApps.Admin);

    /// <summary>A Support CSR: an internal role with no money-writing cell in URD §2.3.</summary>
    public string SupportCsr(Guid userId) =>
        Issue(userId, [MageRideRoles.SupportCsr], MageRideApps.Admin);

    /// <summary>A fleet owner, who has a wallet too (AL-03's `owner_type = 'fleet'`).</summary>
    public string FleetOwner(Guid userId) =>
        Issue(userId, [MageRideRoles.FleetOwner], MageRideApps.Fleet);

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

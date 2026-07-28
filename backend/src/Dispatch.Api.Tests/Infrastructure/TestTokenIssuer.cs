using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Dispatch.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// dispatch-svc is a token consumer: it holds no signing key and, in production, resolves
/// iam-svc's public half over JWKS. Standing a whole iam-svc up to get a bearer would test C020
/// again and make this suite fail for reasons that are not dispatch's. One issuer serves both
/// harnessed services, which is also what makes a driver's bearer work against ride-svc and
/// dispatch-svc in the same test. C025 is where a real iam token crosses into a real dispatch-svc.
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

    public string Passenger(Guid userId) => Issue(userId, [MageRideRoles.Passenger], MageRideApps.Passenger);

    public string Driver(Guid userId) => Issue(userId, [MageRideRoles.Driver], MageRideApps.Driver);

    /// <summary>
    /// A passenger who opened the Driver App: <c>app=driver</c> but no driver role. C020 decision 4
    /// makes this a real, reachable principal rather than a contrived one.
    /// </summary>
    public string PassengerOnDriverApp(Guid userId) =>
        Issue(userId, [MageRideRoles.Passenger], MageRideApps.Driver);

    public string Issue(Guid userId, IReadOnlyCollection<string> roles, string app)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [MageRideClaims.Role] = roles.Count == 1 ? roles.First() : roles.ToArray(),
                [MageRideClaims.App] = app,
                [MageRideClaims.DeviceId] = "test-device",
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(30),
            SigningCredentials = _credentials,
        };

        return Handler.CreateToken(descriptor);
    }
}

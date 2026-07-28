using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// fanout-svc is a token consumer: it holds no signing key and, in production, resolves iam-svc's
/// public half over JWKS with D-21's 15-minute cache. Standing a whole iam-svc up to get a bearer
/// would test C020 again and make this suite fail for reasons that are not the hot path's; C025 is
/// where a real iam token crosses into a real fanout-svc.
/// <para>
/// This is <b>not</b> the MQTT session credential. That one is minted by
/// <c>MageRide.Shared.Mqtt.MqttSessionTokenIssuer</c> against EMQX's HMAC secret, lives four hours
/// and is bound to a vehicle (E-02). Keeping the two visibly separate here is the point — a hub
/// that accepted an MQTT token, or a broker that accepted an API token, would be the E-02 mistake.
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

    public string Passenger(Guid userId) => Issue(userId, MageRideRoles.Passenger, MageRideApps.Passenger);

    public string Driver(Guid userId) => Issue(userId, MageRideRoles.Driver, MageRideApps.Driver);

    public string Issue(Guid userId, string role, string app)
    {
        var now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [MageRideClaims.Role] = role,
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

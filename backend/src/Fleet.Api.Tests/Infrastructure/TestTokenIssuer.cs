using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Fleet.Tests.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29, AL-07), signed by a key this test run owns.
/// </summary>
/// <remarks>
/// fleet-svc is a token consumer: it holds no signing key and, in production, resolves iam-svc's
/// public half over JWKS. Standing a whole iam-svc up to get a bearer would test C026/C027 again
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

    /// <summary>A Fleet Owner with no organisation yet — the state <c>POST /v1/fleets</c> starts in.</summary>
    public string FleetOwner(Guid userId) => Issue(userId, MageRideRoles.FleetOwner, null, null);

    /// <summary>
    /// A member of an organisation, with the claim pair iam-svc mints (AL-03).
    /// </summary>
    /// <remarks>
    /// The claim is minted here so the suite can put a <em>wrong</em> one in it — a person whose
    /// token says they are an Owner of fleet A arriving at fleet B's path. That is the case the
    /// filter exists for, and it cannot be tested with a token that is always right.
    /// </remarks>
    public string FleetMember(Guid userId, Guid fleetId, string fleetRole) =>
        Issue(userId, MageRideRoles.FleetOwner, fleetId, fleetRole);

    /// <summary>Somebody with no fleet role at all — a passenger who found the portal.</summary>
    public string Passenger(Guid userId) => Issue(userId, MageRideRoles.Passenger, null, null);

    public string Issue(Guid userId, string role, Guid? fleetId, string? fleetRole)
    {
        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            [MageRideClaims.Role] = role,
            // AL-07: the Fleet Portal is a browser surface, so the session's `app` is `fleet`.
            [MageRideClaims.App] = MageRideApps.Fleet,
            [MageRideClaims.DeviceId] = "test-browser",
        };

        // Both or neither, as AccessTokenIssuer does (C026): `fleet_role` alone would read as a
        // privilege over every fleet.
        if (fleetId is { } id && fleetRole is not null)
        {
            claims[MageRideClaims.FleetId] = id.ToString();
            claims[MageRideClaims.FleetRole] = fleetRole;
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

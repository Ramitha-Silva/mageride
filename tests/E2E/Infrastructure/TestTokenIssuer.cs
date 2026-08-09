using System.Security.Cryptography;
using MageRide.Shared.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// Mints the RS256 access tokens iam-svc would (D-29), signed by a key this run owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>One issuer for the whole fleet</b>, which is the point of it: the passenger's bearer is
/// accepted by ride-svc, by fare-svc and by fanout-svc's <c>access_token</c> hook without any of
/// them being told about the others, exactly as they would accept an iam-svc token in production.
/// </para>
/// <para>
/// iam-svc itself is in neither fleet. C120's subject is the Mode C ride and C121's is the Mode A/B
/// journey; standing up the login flow to obtain a bearer would make every scenario in this
/// assembly able to fail for C020's reasons. The one place a real iam token crosses into a real
/// service is <c>e2e/walking-skeleton</c> (C025), which is a different kind of run and keeps that
/// coverage.
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

    /// <summary>What each service's bearer handler is given in place of a JWKS fetch.</summary>
    public SecurityKey PublicKey { get; }

    public string IssuerName => Issuer;

    public string Passenger(Guid userId) => Issue(userId, MageRideRoles.Passenger, MageRideApps.Passenger);

    public string Driver(Guid userId) => Issue(userId, MageRideRoles.Driver, MageRideApps.Driver);

    /// <summary>A Fleet Owner with no organisation yet — the state <c>POST /v1/fleets</c> starts in.</summary>
    public string FleetOwner(Guid userId) => Issue(userId, MageRideRoles.FleetOwner, MageRideApps.Fleet);

    /// <summary>
    /// A member of an organisation, with the claim pair iam-svc mints (AL-03, C027).
    /// </summary>
    /// <remarks>
    /// Minted here rather than derived, so a scenario can present a <em>wrong</em> pair — an Owner
    /// of fleet A arriving at fleet B's path. That is the case <c>FleetAccessFilter</c> exists for
    /// and it cannot be exercised with a token that is always right. Both claims or neither, as
    /// <c>AccessTokenIssuer</c> does: <c>fleet_role</c> alone reads as a privilege over every fleet.
    /// </remarks>
    public string FleetMember(Guid userId, Guid fleetId, string fleetRole) =>
        Issue(
            userId,
            MageRideRoles.FleetOwner,
            MageRideApps.Fleet,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [MageRideClaims.FleetId] = fleetId.ToString(),
                [MageRideClaims.FleetRole] = fleetRole,
            });

    public string Issue(Guid userId, string role, string app, IDictionary<string, object>? extraClaims = null)
    {
        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            [MageRideClaims.Role] = role,
            [MageRideClaims.App] = app,
            [MageRideClaims.DeviceId] = "e2e-device",
        };

        foreach (var (key, value) in extraClaims ?? new Dictionary<string, object>(StringComparer.Ordinal))
        {
            claims[key] = value;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,

            // Long enough that a hundred-run concurrency scenario does not expire its own actors
            // half way through. Token lifetime is C020's subject, not this suite's.
            Expires = now.AddHours(2),
            SigningCredentials = _credentials,
        };

        return Handler.CreateToken(descriptor);
    }
}

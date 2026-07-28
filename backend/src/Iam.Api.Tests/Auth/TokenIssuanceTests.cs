using System.Security.Cryptography;
using MageRide.Iam.Auth;
using MageRide.Iam.Configuration;
using MageRide.Shared.Auth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Tests.Auth;

/// <summary>
/// D-29's access token: RS256, 30 minutes, verifiable against the JWKS this service publishes.
/// </summary>
public sealed class TokenIssuanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    private static (SigningKeyRing Keys, AccessTokenIssuer Issuer, TokenOptions Options) Create(string? pem = null)
    {
        var options = new TokenOptions
        {
            SigningKeyPem = pem,
            Issuer = "https://iam.mageride.lk",
        };

        var keys = new SigningKeyRing(
            Options.Create(options), TestEnvironment.Development, NullLogger<SigningKeyRing>.Instance);

        return (keys, new AccessTokenIssuer(keys, Options.Create(options), new FakeTimeProvider(Now)), options);
    }

    private static AccessTokenRequest Request(params string[] roles) => new(
        UserId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Roles: roles.Length == 0 ? [MageRideRoles.Passenger] : roles,
        DeviceKey: "device-abc",
        App: MageRideApps.Passenger,
        SessionId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void The_access_token_is_rs256_and_lives_for_thirty_minutes()
    {
        var (_, issuer, _) = Create();

        var token = issuer.Issue(Request());
        var parsed = new JsonWebToken(token.Value);

        Assert.Equal(SecurityAlgorithms.RsaSha256, parsed.Alg);
        Assert.Equal(1800, token.ExpiresInSeconds);
        Assert.Equal(TimeSpan.FromMinutes(30), parsed.ValidTo - parsed.ValidFrom);
        Assert.Equal(Now.AddMinutes(30), token.ExpiresAt);
    }

    [Fact]
    public void It_carries_the_claims_d3_names()
    {
        var (_, issuer, _) = Create();

        var parsed = new JsonWebToken(issuer.Issue(Request()).Value);

        Assert.Equal("11111111-1111-1111-1111-111111111111", parsed.GetClaim(MageRideClaims.Subject).Value);
        Assert.Equal("22222222-2222-2222-2222-222222222222", parsed.GetClaim(JwtRegisteredClaimNames.Jti).Value);
        Assert.Equal(MageRideRoles.Passenger, parsed.GetClaim(MageRideClaims.Role).Value);
        Assert.Equal("device-abc", parsed.GetClaim(MageRideClaims.DeviceId).Value);
        Assert.Equal(MageRideApps.Passenger, parsed.GetClaim(MageRideClaims.App).Value);
        Assert.Equal("https://iam.mageride.lk", parsed.Issuer);
    }

    [Fact]
    public void Several_roles_travel_as_a_repeated_claim_so_the_union_survives()
    {
        var (_, issuer, _) = Create();

        var parsed = new JsonWebToken(issuer.Issue(Request(MageRideRoles.Passenger, MageRideRoles.Driver)).Value);
        var roles = parsed.Claims.Where(c => c.Type == MageRideClaims.Role).Select(c => c.Value).ToArray();

        Assert.Equal([MageRideRoles.Passenger, MageRideRoles.Driver], roles);
    }

    [Fact]
    public void A_token_with_no_role_is_refused_rather_than_issued()
    {
        var (_, issuer, _) = Create();

        // Deny-by-default (AL-06) is only deny-by-default if a role-less token cannot exist.
        Assert.Throws<ArgumentException>(() => issuer.Issue(Request() with { Roles = [] }));
    }

    [Fact]
    public async Task The_token_validates_against_the_published_jwks()
    {
        var (keys, issuer, options) = Create();
        var token = issuer.Issue(Request());

        // Exactly the bytes GET /.well-known/jwks.json serves, parsed the way a consuming service
        // parses them (MageRide.Shared's JwksConfigurationManager calls JsonWebKeySet.Create too).
        var published = JsonWebKeySet.Create(JsonWebKeySetDocument.From(keys).ToJson());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = published.GetSigningKeys(),
            ValidIssuer = options.Issuer,
            ValidateAudience = false,
            ValidateLifetime = false,
        });

        Assert.True(result.IsValid, result.Exception?.ToString());
    }

    [Fact]
    public async Task A_token_signed_by_a_different_key_does_not_validate_against_ours()
    {
        var (ours, _, options) = Create();
        var (_, otherIssuer, _) = Create(NewPrivateKeyPem());

        var forged = otherIssuer.Issue(Request());
        var published = JsonWebKeySet.Create(JsonWebKeySetDocument.From(ours).ToJson());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(forged.Value, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeys = published.GetSigningKeys(),
            ValidIssuer = options.Issuer,
            ValidateAudience = false,
            ValidateLifetime = false,
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void The_kid_is_the_rfc_7638_thumbprint_and_is_stable_for_one_key()
    {
        var pem = NewPrivateKeyPem();

        var first = Create(pem).Keys;
        var second = Create(pem).Keys;

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.NotEqual(first.KeyId, Create(NewPrivateKeyPem()).Keys.KeyId);
        Assert.Equal(first.KeyId, JsonWebKeySetDocument.From(first).Keys.Single().Kid);
    }

    [Fact]
    public void A_configured_kid_wins_so_a_rotation_can_be_named()
    {
        var keys = new SigningKeyRing(
            Options.Create(new TokenOptions { SigningKeyPem = NewPrivateKeyPem(), SigningKeyId = "2026-q3" }),
            TestEnvironment.Development,
            NullLogger<SigningKeyRing>.Instance);

        Assert.Equal("2026-q3", keys.KeyId);
    }

    [Fact]
    public void Outside_development_a_signing_key_is_mandatory()
    {
        // An ephemeral key would invalidate every issued token on restart and give each replica
        // its own JWKS — a 401 storm that looks like an outage rather than a misconfiguration.
        var exception = Assert.Throws<InvalidOperationException>(() => new SigningKeyRing(
            Options.Create(new TokenOptions()), TestEnvironment.Staging, NullLogger<SigningKeyRing>.Instance));

        Assert.Contains("Jwt:SigningKeyPem", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_published_jwks_carries_only_the_public_half()
    {
        var keys = Create(NewPrivateKeyPem()).Keys;

        var json = JsonWebKeySetDocument.From(keys).ToJson();

        Assert.Contains("\"kty\":\"RSA\"", json, StringComparison.Ordinal);
        Assert.Contains("\"alg\":\"RS256\"", json, StringComparison.Ordinal);
        // "d", "p", "q" and friends are the private RSA parameters; publishing any of them would
        // let anybody mint a MageRide token.
        Assert.DoesNotContain("\"d\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"p\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"q\":", json, StringComparison.Ordinal);
    }

    private static string NewPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}

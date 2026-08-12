using System.Security.Cryptography;
using System.Text;
using MageRide.Contract.Tests.Runtime;
using MageRide.Shared.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Security.Tests.Tokens;

/// <summary>
/// How every service in the fleet validates a bearer, read off its own composed
/// <see cref="JwtBearerOptions"/> — ASVS V3 (session management) and V7 (cryptography), D-29, D-21.
///
/// <para>
/// <b>Per service, not once for the kernel.</b> <c>AddMageRideAuth</c> configures this in one place,
/// which is exactly why the assertion has to be made in twenty-four: a service that took a
/// <c>Configure&lt;JwtBearerOptions&gt;</c> of its own after the kernel's — to add an audience, to
/// widen a skew — silently replaces a decision nobody would look for again, and the options monitor
/// applies every registered configurator in order. Reading the final value per service is the only
/// way to see what the middleware will actually use.
/// </para>
/// </summary>
public sealed class BearerValidationTests
{
    /// <summary>
    /// The services that register a bearer handler at all.
    /// </summary>
    /// <remarks>
    /// Two do not, and both are argued in their own composition roots: <c>public-bff</c>, where
    /// AL-44 makes the share token the only credential and a JWT handler would be a second way in;
    /// and <c>ocr</c>, which has no user-facing surface. Naming them means the theory's denominator
    /// cannot shrink by accident.
    /// </remarks>
    public static readonly IReadOnlySet<string> NoBearerHandler = new HashSet<string>(StringComparer.Ordinal)
    {
        "public-bff",
        "ocr",
    };

    public static TheoryData<string> BearerServices()
    {
        var data = new TheoryData<string>();

        foreach (var service in ServiceCatalog.All
                     .Select(static service => service.Document)
                     .Where(static service => !NoBearerHandler.Contains(service))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(service);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BearerServices))]
    public void The_service_accepts_RS256_and_nothing_else(string service)
    {
        var parameters = ValidationParametersOf(service);

        // The classic algorithm-confusion attack: iam-svc's JWKS is public, so if a service would
        // accept HS256 an attacker can sign a token of their own choosing using the published
        // modulus as the HMAC secret. Leaving `ValidAlgorithms` unset accepts every algorithm the
        // key type supports, which is why this is asserted as an exact set rather than a `Contains`.
        Assert.NotNull(parameters.ValidAlgorithms);
        Assert.Equal([SecurityAlgorithms.RsaSha256], parameters.ValidAlgorithms.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BearerServices))]
    public void The_service_validates_the_signature_the_lifetime_and_the_issuer(string service)
    {
        var parameters = ValidationParametersOf(service);

        Assert.True(parameters.ValidateIssuerSigningKey, $"{service} does not validate the signing key.");
        Assert.True(parameters.ValidateLifetime, $"{service} accepts an expired access token (D-29: 30 min).");
        Assert.True(
            parameters.ValidateIssuer,
            $"{service} does not validate the issuer. Jwt:Issuer is set on every deployment (see "
            + "infra/env/.env.common.example); an unvalidated issuer accepts a token minted by any "
            + "authority whose key happens to be in the resolved key set.");

        // 5 minutes is the framework default and it is a long time for a 30-minute token: it extends
        // every revocation window by the skew. The kernel sets its own; this is the ceiling.
        Assert.True(
            parameters.ClockSkew <= TimeSpan.FromMinutes(2),
            $"{service} allows {parameters.ClockSkew.TotalMinutes:0.#} min of clock skew on a 30-minute "
            + "access token, which extends every expiry and every force-logout by the same amount.");
    }

    [Theory]
    [MemberData(nameof(BearerServices))]
    public void The_service_reads_roles_from_the_MageRide_claim_and_does_not_remap_inbound_names(string service)
    {
        var options = OptionsOf(service);
        var parameters = options.TokenValidationParameters;

        // `MapInboundClaims` is true by default and rewrites `sub` to the long WS-Federation URI.
        // Everything on this platform reads `sub` and `role` by their short names — AL-06's role
        // union arrives as repeated `role` claims — so a service that let the mapper run would see
        // no roles at all and deny everything, or worse, read a claim somebody else could set.
        Assert.False(
            options.MapInboundClaims,
            $"{service} remaps inbound claim names, so `sub` and `role` are not where MageRideClaims looks.");

        Assert.Equal(MageRideClaims.Role, parameters.RoleClaimType);
        Assert.Equal(MageRideClaims.Subject, parameters.NameClaimType);
    }

    [Fact]
    public async Task A_token_signed_with_the_public_key_as_an_HMAC_secret_is_refused()
    {
        // The attack the exact-set assertion above prevents, executed rather than argued. The
        // parameters are a real service's, not a synthetic set, so this fails the moment that
        // service's configuration changes — which is the whole point of driving it.
        var parameters = ValidationParametersOf("wallet");

        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "c127-forgery" };
        var publicModulus = rsa.ExportParameters(includePrivateParameters: false).Modulus!;

        // The forger knows only what the JWKS publishes, and that is enough for this attack: the
        // modulus, base64url-encoded exactly as `n` carries it, used as an HMAC key.
        var hmacKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(Base64UrlEncoder.Encode(publicModulus)));

        var handler = new JsonWebTokenHandler();
        var forged = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ServiceComposition.Issuer,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [MageRideClaims.Subject] = Guid.NewGuid().ToString(),
                [MageRideClaims.Role] = MageRideRoles.SuperAdmin,
            },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(hmacKey, SecurityAlgorithms.HmacSha256),
        });

        var attempt = parameters.Clone();
        attempt.IssuerSigningKeys = [key, hmacKey];
        attempt.ValidIssuer = ServiceComposition.Issuer;
        attempt.ValidateAudience = false;
        attempt.ConfigurationManager = null;

        var result = await handler.ValidateTokenAsync(forged, attempt);

        Assert.False(
            result.IsValid,
            "A super_admin token forged with the published RSA modulus as an HMAC secret was ACCEPTED. "
            + "This is algorithm confusion and it is a full authentication bypass — the JWKS is public.");

        // And the same claim set signed properly is accepted, so the refusal above is the algorithm
        // and not some unrelated defect in the token.
        var honest = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ServiceComposition.Issuer,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [MageRideClaims.Subject] = Guid.NewGuid().ToString(),
                [MageRideClaims.Role] = MageRideRoles.SuperAdmin,
            },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

        Assert.True((await handler.ValidateTokenAsync(honest, attempt)).IsValid);
    }

    [Fact]
    public async Task An_unsigned_token_is_refused()
    {
        // `alg: none`. Every library has refused it for a decade and every library has had a
        // release that did not; it costs one assertion to know which one this deployment has.
        var parameters = ValidationParametersOf("wallet");

        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "c127-unsigned" };

        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""{"sub":"{{Guid.NewGuid()}}","role":"super_admin","iss":"{{ServiceComposition.Issuer}}","exp":{{DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()}}}""");

        var attempt = parameters.Clone();
        attempt.IssuerSigningKeys = [key];
        attempt.ValidIssuer = ServiceComposition.Issuer;
        attempt.ValidateAudience = false;
        attempt.ConfigurationManager = null;

        var result = await new JsonWebTokenHandler().ValidateTokenAsync($"{header}.{payload}.", attempt);

        Assert.False(result.IsValid, "An unsigned `alg: none` super_admin token was ACCEPTED.");
    }

    private static JwtBearerOptions OptionsOf(string service)
    {
        var definition = ServiceCatalog.All.Single(entry => entry.Document == service);
        var application = ServiceComposition.Compose(definition);

        return application.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static TokenValidationParameters ValidationParametersOf(string service) =>
        OptionsOf(service).TokenValidationParameters;
}

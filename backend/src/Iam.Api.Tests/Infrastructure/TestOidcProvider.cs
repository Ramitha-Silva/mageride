using System.Security.Cryptography;
using MageRide.Iam.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Tests.Infrastructure;

/// <summary>
/// Stands in for Google and Apple: holds a key pair, publishes it as the provider's key set, and
/// mints ID tokens with it.
/// </summary>
/// <remarks>
/// Only the <em>key source</em> is faked. <c>OidcTokenVerifier</c> — the thing under test — is the
/// real one, so a test that mints a token for the wrong audience, from the wrong issuer, with an
/// expired lifetime or under a different key gets refused by the same code production runs. Faking
/// <c>IOidcTokenVerifier</c> instead would leave every one of those checks untested.
/// </remarks>
internal sealed class TestOidcProvider : IOidcKeySource, IDisposable
{
    public const string GoogleClientId = "c026-portal.apps.googleusercontent.com";
    public const string GoogleIssuer = "https://accounts.google.com";

    public const string AppleClientId = "lk.mageride.fleet";
    public const string AppleIssuer = "https://appleid.apple.com";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public TestOidcProvider()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "c026-test-provider" };
        Credentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256);
    }

    private SigningCredentials Credentials { get; }

    /// <summary>A Google ID token for a subject, valid unless a test asks otherwise.</summary>
    public string GoogleIdToken(
        string subject,
        string? email = null,
        bool emailVerified = true,
        string? audience = null,
        string? issuer = null,
        TimeSpan? lifetime = null) =>
        IdToken(subject, email, emailVerified, audience ?? GoogleClientId, issuer ?? GoogleIssuer, lifetime);

    /// <summary>An Apple ID token. Apple sends <c>email_verified</c> as a string, and so does this.</summary>
    public string AppleIdToken(
        string subject,
        string? email = null,
        bool emailVerified = true,
        string? audience = null,
        string? issuer = null,
        TimeSpan? lifetime = null) =>
        IdToken(subject, email, emailVerified, audience ?? AppleClientId, issuer ?? AppleIssuer, lifetime, verifiedAsString: true);

    /// <summary>A token signed by a key the provider never published.</summary>
    public static string ForgedIdToken(string subject, string email)
    {
        using var rogue = RSA.Create(2048);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = GoogleIssuer,
            Audience = GoogleClientId,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = subject,
                [JwtRegisteredClaimNames.Email] = email,
                ["email_verified"] = true,
            },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rogue) { KeyId = "rogue" }, SecurityAlgorithms.RsaSha256),
        };

        return Handler.CreateToken(descriptor);
    }

    public Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(string provider, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<SecurityKey>>([_key]);

    public void Dispose() => _rsa.Dispose();

    private string IdToken(
        string subject,
        string? email,
        bool emailVerified,
        string audience,
        string issuer,
        TimeSpan? lifetime,
        bool verifiedAsString = false)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = subject,
        };

        if (email is not null)
        {
            claims[JwtRegisteredClaimNames.Email] = email;
            claims["email_verified"] = verifiedAsString
                ? emailVerified.ToString().ToLowerInvariant()
                : emailVerified;
        }

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            // Expiry is relative to *now* even when negative, so a test can mint one that the
            // verifier's two-minute clock skew will not rescue.
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = Credentials,
        };

        return Handler.CreateToken(descriptor);
    }
}

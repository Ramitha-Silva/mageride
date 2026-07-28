using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Auth;

/// <summary>
/// The RS256 key iam-svc signs access tokens with, and the JWKS every other service verifies
/// them against (D-29, D-21).
/// </summary>
/// <remarks>
/// <para>
/// One key, held for the process lifetime. A 90-day rotation (D7' §13) is a redeploy with a new
/// <c>Jwt__SigningKeyPem</c>; consumers pick it up because an unknown <c>kid</c> makes their
/// <c>JwksConfigurationManager</c> refetch inside the 15-minute cache window.
/// </para>
/// <para>
/// Development with no key configured mints an ephemeral one so the skeleton runs from a clean
/// checkout. Every other environment must supply one — an ephemeral key would invalidate every
/// live token on restart and give each replica a different JWKS.
/// </para>
/// </remarks>
public sealed class SigningKeyRing : IDisposable
{
    private readonly RSA _rsa;

    public SigningKeyRing(IOptions<TokenOptions> options, IHostEnvironment environment, ILogger<SigningKeyRing> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;
        _rsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(settings.SigningKeyPem))
        {
            // ImportFromPem handles both "PRIVATE KEY" (PKCS#8) and "RSA PRIVATE KEY" (PKCS#1).
            _rsa.ImportFromPem(settings.SigningKeyPem);
        }
        else if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "Jwt:SigningKeyPem is not configured; minted an ephemeral RS256 key. Tokens issued by this " +
                "process die with it and a second replica would publish a different JWKS. Development only.");
        }
        else
        {
            throw new InvalidOperationException(
                "Jwt:SigningKeyPem is required outside Development (D7' §4.2). An ephemeral key would " +
                "invalidate every issued token on restart and give each replica its own JWKS.");
        }

        var publicParameters = _rsa.ExportParameters(includePrivateParameters: false);
        Modulus = publicParameters.Modulus ?? throw new InvalidOperationException("The signing key has no modulus.");
        Exponent = publicParameters.Exponent ?? throw new InvalidOperationException("The signing key has no exponent.");

        KeyId = string.IsNullOrWhiteSpace(settings.SigningKeyId)
            ? ComputeThumbprint(Modulus, Exponent)
            : settings.SigningKeyId;

        SigningKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>The <c>kid</c> on every issued token and on the published JWK.</summary>
    public string KeyId { get; }

    public RsaSecurityKey SigningKey { get; }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>Public modulus, big-endian and unpadded — the JWK <c>n</c> member before encoding.</summary>
    public byte[] Modulus { get; }

    /// <summary>Public exponent — the JWK <c>e</c> member before encoding.</summary>
    public byte[] Exponent { get; }

    /// <summary>
    /// Resolves the key a token's <c>kid</c> names. iam-svc validates the tokens it issued
    /// itself, so this never reaches the network — pointing the handler at this service's own
    /// JWKS URL would make it depend on itself to answer a request.
    /// </summary>
    public IEnumerable<SecurityKey> Resolve(string? keyId) =>
        keyId is null || string.Equals(keyId, KeyId, StringComparison.Ordinal) ? [SigningKey] : [];

    /// <summary>
    /// Bytes for the HMAC that binds an opaque refresh token to its session. Derived from the
    /// private key when <see cref="TokenOptions.RefreshTokenKey"/> is unset, so the skeleton needs
    /// no second secret; deployments should set one so a signing-key rotation does not log
    /// everybody out.
    /// </summary>
    public byte[] DeriveRefreshTokenKey(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Encoding.UTF8.GetBytes(configured);
        }

        return SHA512.HashData(_rsa.ExportPkcs8PrivateKey());
    }

    public void Dispose() => _rsa.Dispose();

    /// <summary>
    /// RFC 7638 JWK thumbprint: SHA-256 over the required members in lexicographic order, with
    /// no whitespace. Deterministic for a given key and different for any other.
    /// </summary>
    private static string ComputeThumbprint(byte[] modulus, byte[] exponent)
    {
        var canonical =
            $$"""{"e":"{{Base64UrlEncoder.Encode(exponent)}}","kty":"RSA","n":"{{Base64UrlEncoder.Encode(modulus)}}"}""";

        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>One entry of the published JWKS (RFC 7517), as iam-svc serves it.</summary>
/// <remarks>
/// Hand-rolled rather than serialised from <see cref="JsonWebKey"/>: that type emits every
/// unset member as an empty array, and a JWKS is read by EMQX and by three non-.NET clients.
/// </remarks>
public sealed record JsonWebKeyDocument(string Kty, string Use, string Alg, string Kid, string N, string E);

/// <summary>The <c>/.well-known/jwks.json</c> body.</summary>
public sealed record JsonWebKeySetDocument(IReadOnlyList<JsonWebKeyDocument> Keys)
{
    public static JsonWebKeySetDocument From(SigningKeyRing keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return new JsonWebKeySetDocument(
        [
            new JsonWebKeyDocument(
                Kty: "RSA",
                Use: "sig",
                Alg: SecurityAlgorithms.RsaSha256,
                Kid: keys.KeyId,
                N: Base64UrlEncoder.Encode(keys.Modulus),
                E: Base64UrlEncoder.Encode(keys.Exponent)),
        ]);
    }

    /// <summary>The document as the JSON text <c>JsonWebKeySet.Create</c> consumes.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, MageRide.Shared.Http.MageRideJson.Options);
}

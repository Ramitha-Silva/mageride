using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MageRide.Iam.Auth;

/// <summary>One RSA key in the ring: the private half, its <c>kid</c> and its public members.</summary>
internal sealed class RingKey : IDisposable
{
    private readonly RSA _rsa;

    private RingKey(RSA rsa, string keyId)
    {
        _rsa = rsa;

        var publicParameters = rsa.ExportParameters(includePrivateParameters: false);
        Modulus = publicParameters.Modulus ?? throw new InvalidOperationException("The signing key has no modulus.");
        Exponent = publicParameters.Exponent ?? throw new InvalidOperationException("The signing key has no exponent.");

        KeyId = string.IsNullOrWhiteSpace(keyId) ? ComputeThumbprint(Modulus, Exponent) : keyId;
        SecurityKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        SigningCredentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.RsaSha256);
    }

    public string KeyId { get; }

    public RsaSecurityKey SecurityKey { get; }

    public SigningCredentials SigningCredentials { get; }

    public byte[] Modulus { get; }

    public byte[] Exponent { get; }

    /// <summary>Imports a PEM. Handles "PRIVATE KEY" (PKCS#8) and "RSA PRIVATE KEY" (PKCS#1).</summary>
    public static RingKey FromPem(string pem, string? keyId = null)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return new RingKey(rsa, keyId ?? string.Empty);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>A throwaway key, for Development only.</summary>
    public static RingKey Ephemeral() => new(RSA.Create(2048), string.Empty);

    /// <summary>PKCS#8 bytes of the private half — the seed for the refresh-token HMAC key.</summary>
    public byte[] ExportPrivate() => _rsa.ExportPkcs8PrivateKey();

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

/// <summary>
/// The RS256 keys iam-svc signs access tokens with, and the JWKS every other service verifies
/// them against (D-29, D-21).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rotation is an overlap, not a switch.</b> One key signs — <c>Jwt:SigningKeyPem</c> — and any
/// number of retired keys stay published and accepted (<c>Jwt:RetiredSigningKeyPems</c>). D7' §13's
/// 90-day rotation is therefore two deploys: the incoming key becomes the signer and the outgoing
/// one moves to the retired list, where it stays until every token it signed has expired (30
/// minutes) and every consumer's D-21 cache has turned over (15 minutes). Only then is it deleted.
/// Promoting a new key with no overlap would 401 every session issued in the previous half hour.
/// </para>
/// <para>
/// The new key needs no warm-up in the other direction: an unknown <c>kid</c> makes a consumer's
/// <see cref="MageRide.Shared.Auth.JwksConfigurationManager"/> refetch inside its cache window, so
/// it can be signed with from the first request.
/// </para>
/// <para>
/// Development with no key configured mints an ephemeral one so the skeleton runs from a clean
/// checkout. Every other environment must supply one — an ephemeral key would invalidate every
/// live token on restart and give each replica a different JWKS.
/// </para>
/// </remarks>
public sealed class SigningKeyRing : IDisposable
{
    private readonly RingKey _active;
    private readonly IReadOnlyList<RingKey> _ring;

    public SigningKeyRing(IOptions<TokenOptions> options, IHostEnvironment environment, ILogger<SigningKeyRing> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;

        if (!string.IsNullOrWhiteSpace(settings.SigningKeyPem))
        {
            _active = RingKey.FromPem(settings.SigningKeyPem, settings.SigningKeyId);
        }
        else if (environment.IsDevelopment())
        {
            _active = RingKey.Ephemeral();
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

        var ring = new List<RingKey> { _active };

        foreach (var pem in settings.RetiredSigningKeyPems)
        {
            if (string.IsNullOrWhiteSpace(pem))
            {
                continue;
            }

            var retired = RingKey.FromPem(pem);

            // A retired entry that is still the active key is a half-finished rotation, and
            // publishing one kid twice makes a consumer's key set ambiguous.
            if (ring.Exists(existing => string.Equals(existing.KeyId, retired.KeyId, StringComparison.Ordinal)))
            {
                logger.LogWarning(
                    "Jwt:RetiredSigningKeyPems repeats a key already in the ring ({KeyId}); ignoring the duplicate",
                    retired.KeyId);
                retired.Dispose();
                continue;
            }

            ring.Add(retired);
        }

        _ring = ring;

        if (_ring.Count > 1)
        {
            logger.LogInformation(
                "Signing key {KeyId} is active; {RetiredCount} retired key(s) stay published for the rotation overlap (D7' §13)",
                _active.KeyId,
                _ring.Count - 1);
        }
    }

    /// <summary>The <c>kid</c> stamped on every token this process issues.</summary>
    public string KeyId => _active.KeyId;

    public RsaSecurityKey SigningKey => _active.SecurityKey;

    public SigningCredentials SigningCredentials => _active.SigningCredentials;

    /// <summary>Public modulus of the active key, big-endian and unpadded (the JWK <c>n</c>).</summary>
    public byte[] Modulus => _active.Modulus;

    /// <summary>Public exponent of the active key (the JWK <c>e</c>).</summary>
    public byte[] Exponent => _active.Exponent;

    /// <summary>Every published <c>kid</c>, the active one first.</summary>
    public IReadOnlyList<string> KeyIds => [.. _ring.Select(static key => key.KeyId)];

    /// <summary>
    /// Resolves the key a token's <c>kid</c> names, across the whole ring. iam-svc validates the
    /// tokens it issued itself, so this never reaches the network — pointing the handler at this
    /// service's own JWKS URL would make it depend on itself to answer a request.
    /// </summary>
    /// <remarks>
    /// A token with no <c>kid</c> is offered every key rather than only the active one: mid
    /// rotation, refusing it would be refusing a token this service itself signed.
    /// </remarks>
    public IEnumerable<SecurityKey> Resolve(string? keyId)
    {
        if (keyId is null)
        {
            return [.. _ring.Select(static key => key.SecurityKey)];
        }

        var match = _ring.FirstOrDefault(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
        return match is null ? [] : [match.SecurityKey];
    }

    /// <summary>
    /// Bytes for the HMAC that binds an opaque refresh token to its session. Derived from the
    /// active private key when <see cref="TokenOptions.RefreshTokenKey"/> is unset, so the
    /// skeleton needs no second secret; deployments should set one, because otherwise the 90-day
    /// rotation this class exists to support would invalidate every live refresh token.
    /// </summary>
    public byte[] DeriveRefreshTokenKey(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Encoding.UTF8.GetBytes(configured);
        }

        return SHA512.HashData(_active.ExportPrivate());
    }

    /// <summary>The published key set — the active key first, then the rotation overlap.</summary>
    internal IEnumerable<(string KeyId, byte[] Modulus, byte[] Exponent)> PublishedKeys() =>
        _ring.Select(static key => (key.KeyId, key.Modulus, key.Exponent));

    public void Dispose()
    {
        foreach (var key in _ring)
        {
            key.Dispose();
        }
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
            .. keys.PublishedKeys().Select(static key => new JsonWebKeyDocument(
                Kty: "RSA",
                Use: "sig",
                Alg: SecurityAlgorithms.RsaSha256,
                Kid: key.KeyId,
                N: Base64UrlEncoder.Encode(key.Modulus),
                E: Base64UrlEncoder.Encode(key.Exponent))),
        ]);
    }

    /// <summary>The document as the JSON text <c>JsonWebKeySet.Create</c> consumes.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, MageRide.Shared.Http.MageRideJson.Options);
}

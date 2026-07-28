using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Auth;

/// <summary>
/// Encodes and verifies the opaque refresh token (D-29).
/// </summary>
/// <remarks>
/// <para>
/// The token is <c>mr1.{jti}.{hmac}</c>: the session id plus an HMAC-SHA256 over the first two
/// segments. Opaque to the client — it carries no claims and nothing can be read out of it — but
/// self-describing to the server, which is what lets <c>iam.sessions</c> be the single record of
/// a session. The specs give the table no token column, and inventing one would put a bearer
/// secret at rest for no gain: forging a token needs the key, and a token that verifies is still
/// worthless unless its row is unrevoked and unexpired.
/// </para>
/// <para>
/// Rotation therefore means "revoke this jti and issue a new one" (D-29): a spent token still
/// verifies, finds a revoked row, and is treated as replay — which is exactly the signal the
/// contract asks us to act on by revoking the whole session family.
/// </para>
/// </remarks>
public sealed class RefreshTokenCodec
{
    private const string Prefix = "mr1";

    private readonly byte[] _key;

    public RefreshTokenCodec(SigningKeyRing keys, IOptions<TokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(options);

        _key = keys.DeriveRefreshTokenKey(options.Value.RefreshTokenKey);
    }

    /// <summary>The opaque token for a session.</summary>
    public string Issue(Guid sessionId)
    {
        var payload = $"{Prefix}.{Encode(sessionId.ToByteArray())}";
        return $"{payload}.{Encode(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload)))}";
    }

    /// <summary>
    /// Recovers the session id from a token, or fails. A forged or truncated token never reaches
    /// the database.
    /// </summary>
    public bool TryRead(string? token, out Guid sessionId)
    {
        sessionId = default;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var lastDot = token.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return false;
        }

        var payload = token[..lastDot];
        if (!payload.StartsWith(Prefix + ".", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryDecode(token[(lastDot + 1)..], out var presented) ||
            !TryDecode(payload[(Prefix.Length + 1)..], out var idBytes) ||
            idBytes.Length != 16)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            return false;
        }

        sessionId = new Guid(idBytes);
        return true;
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool TryDecode(string value, [NotNullWhen(true)] out byte[]? decoded)
    {
        decoded = null;

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => null!,
        };

        if (padded is null)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[((padded.Length / 4) * 3) + 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }

        decoded = buffer[..written].ToArray();
        return true;
    }
}

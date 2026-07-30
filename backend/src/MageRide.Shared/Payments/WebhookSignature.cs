using System.Security.Cryptography;
using System.Text;

namespace MageRide.Shared.Payments;

/// <summary>
/// Verifies the <c>X-Signature</c> header on a payment-provider callback — HMAC-SHA256 over the raw
/// request body, keyed with the provider's webhook secret (D6' §7.1/§7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>In the kernel because there are six of these callbacks, not one.</b>
/// <c>backend/contracts/_shared.yaml</c> declares the <c>hmacSignature</c> scheme once for all of
/// them — wallet-svc's two top-up callbacks (C046), fare-svc's payment callbacks (C049/C050) and
/// subscription-svc's Mode B ones (C048) — and D6' §7.1 resolves NY's "<c>[UNVERIFIED]</c> Juspay
/// webhook signature" to "explicit HMAC verification". Four copies of a signature check is four
/// chances for one of them to compare with <c>==</c>, which leaks the key a byte at a time to a
/// caller who can time it.
/// </para>
/// <para>
/// <b>The body must be the raw bytes, before any parsing.</b> Deserialising and re-serialising a
/// payload changes whitespace and key order, so the digest of a round-tripped body is not the digest
/// the provider signed. `_shared.yaml` says "verified before any body parsing" for that reason.
/// </para>
/// <para>
/// <b>Both encodings are accepted, and that is not laxity.</b> OnePay's documentation and the
/// Commercial Bank IPG differ on whether the digest is hex or base64, and a deployment that guesses
/// wrong rejects every genuine callback — which looks exactly like a provider outage. Accepting
/// either costs nothing: both are 32 bytes compared in fixed time against the same key.
/// </para>
/// </remarks>
public static class WebhookSignature
{
    /// <summary>The header every provider callback carries (<c>_shared.yaml</c>, <c>hmacSignature</c>).</summary>
    public const string HeaderName = "X-Signature";

    /// <summary>
    /// Computes the canonical signature of <paramref name="body"/>, lower-case hex.
    /// </summary>
    public static string Compute(ReadOnlySpan<byte> body, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is a valid signature over <paramref name="body"/>.
    /// </summary>
    /// <param name="presented">
    /// The header value, hex or base64, optionally prefixed <c>sha256=</c> as some providers send it.
    /// </param>
    /// <param name="secret">The provider's webhook secret. An empty secret always fails.</param>
    /// <remarks>
    /// Fixed-time comparison over the decoded bytes. A malformed header is a failure rather than an
    /// exception — a callback endpoint is reachable by anyone who finds the URL, so an unparseable
    /// header is an ordinary event and must cost the same as a wrong one.
    /// </remarks>
    public static bool IsValid(ReadOnlySpan<byte> body, string? presented, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        var candidate = Decode(presented.Trim());

        return candidate is not null && CryptographicOperations.FixedTimeEquals(candidate, expected);
    }

    private static byte[]? Decode(string presented)
    {
        var value = presented.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? presented["sha256=".Length..]
            : presented;

        if (value.Length == 64)
        {
            try
            {
                return Convert.FromHexString(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return Convert.TryFromBase64String(value, new byte[32], out var written) && written == 32
            ? Convert.FromBase64String(value)
            : null;
    }
}

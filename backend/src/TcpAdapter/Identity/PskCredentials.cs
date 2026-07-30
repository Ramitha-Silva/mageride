using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.TcpAdapter.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.TcpAdapter.Identity;

/// <summary>
/// Verifies the signed PSK credential provisioning-svc mints for legacy TCP devices (D6' §4.2,
/// ADD §7.7.3).
/// </summary>
/// <remarks>
/// <para>
/// The token is <c>mrp1.{serial}.{expiryUnix}.{secret}.{signature}</c>, the signature an
/// HMAC-SHA256 over <c>mrp1|serial|imei|expiry|secret</c> keyed with
/// <c>secrets/psk_signing_key</c>. <b>The IMEI is inside the signature</b>, which is what makes a
/// token stolen from one device useless on another — and what makes this check worth doing at all,
/// because <c>validate</c> answers "has this credential been revoked" and cannot answer "was this
/// credential ever issued to the device presenting it".
/// </para>
/// <para>
/// <b>The format is spelled here and implemented in provisioning-svc.</b> This project must not
/// reference that one — the fence is that protocol decoding is the adapter's and credential minting
/// is provisioning-svc's — so the two implementations of one format sit either side of a wire, in the
/// same situation as position-processor's copy of dispatch-svc's <c>AVAILABLE</c>. The test suite
/// mints a token with the real <c>EmbeddedStepCa</c> and verifies it here, so a divergence fails a
/// build rather than every hardware connect in production.
/// </para>
/// <para>
/// <b>Unconfigured means unverified, not rejected.</b> With no key the adapter still resolves the
/// device through <c>validate</c> — which is the T-12 question and the one that actually protects the
/// platform — and says so once at start-up. Refusing every PSK-bearing device because a key file was
/// not mounted would take a whole fleet off the air over a missing setting.
/// </para>
/// </remarks>
public sealed class PskCredentials : IDisposable
{
    /// <summary>Version tag. A format change is detectable rather than silent.</summary>
    public const string TokenPrefix = "mrp1";

    /// <summary>Where the key lives under <c>Adapter:PskKeyDirectory</c> — step-ca's own layout.</summary>
    public const string SigningKeyFile = "secrets/psk_signing_key";

    private readonly byte[]? _key;

    public PskCredentials(IOptions<AdapterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var directory = options.Value.PskKeyDirectory;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var path = Path.Combine(Path.GetFullPath(directory), SigningKeyFile);

        if (!File.Exists(path))
        {
            // Not a throw. The directory is a shared volume written by provisioning-svc's CA, and a
            // pod that starts before it has been populated must come up and serve the devices whose
            // protocol carries no credential at all.
            return;
        }

        _key = DecodeBase64Url(File.ReadAllText(path).Trim());
    }

    /// <summary>Whether a signing key was loaded.</summary>
    public bool CanVerify => _key is not null;

    /// <summary>Whether a string is shaped like one of these tokens at all.</summary>
    /// <remarks>
    /// Used to tell a credential apart from the other things a protocol's authentication field
    /// carries — JT/T 808's <c>0x0102</c> body is whatever the device was registered with, which for
    /// most firmware is its own id echoed back.
    /// </remarks>
    public static bool LooksLikeToken(string? candidate) =>
        candidate is not null
        && candidate.StartsWith(TokenPrefix + '.', StringComparison.Ordinal)
        && candidate.Count(character => character == '.') == 4;

    /// <summary>
    /// Verifies a token against the IMEI presenting it and reports the credential serial it names.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> for a forged, expired, malformed or wrong-device token — and for any
    /// token at all when no key is loaded, in which case <paramref name="serial"/> is still filled
    /// from the token so the serial can reach <c>validate</c>'s anti-clone evidence.
    /// </returns>
    public bool TryRead(string? token, string imei, DateTimeOffset now, out string serial)
    {
        serial = string.Empty;

        if (!LooksLikeToken(token))
        {
            return false;
        }

        var parts = token!.Split('.');

        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        // The serial is readable without the key and is evidence rather than authority, so it is
        // reported even when the signature cannot be checked.
        serial = parts[1];

        if (_key is null)
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= now)
        {
            return false;
        }

        byte[] presented;

        try
        {
            presented = DecodeBase64Url(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var payload = Encoding.UTF8.GetBytes(string.Join(
            '|', TokenPrefix, parts[1], imei, expiresUnix.ToString(CultureInfo.InvariantCulture), parts[3]));

        // Fixed-time: the signature is the only thing between a guessed token and a device identity,
        // and an early-exit comparison leaks it a byte at a time.
        return CryptographicOperations.FixedTimeEquals(presented, HMACSHA256.HashData(_key, payload));
    }

    public void Dispose()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
        }
    }

    /// <summary>Base64url without padding — the encoding provisioning-svc writes the key and the signature in.</summary>
    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Not base64url."),
        };

        return Convert.FromBase64String(padded);
    }
}

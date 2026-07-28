using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Domain;

/// <summary>
/// Reduces an E.164 number to the keyed digest <c>iam.phone_lookups.phone_hash</c> stores (P-03).
/// </summary>
/// <remarks>
/// <para>
/// A plain SHA-256 of a phone number is not a hiding place: Sri Lankan mobiles are
/// <c>+947XXXXXXXX</c>, which is 10<sup>8</sup> candidates — seconds of work to enumerate.
/// The HMAC key (<c>Auth:PhoneHashKey</c>) is what makes a leaked table useless without it, and
/// is why the key is required outside Development rather than defaulted.
/// </para>
/// <para>
/// Deliberately <b>not</b> the OTP pepper. <c>Otp:PepperKey</c> guards a code that lives for five
/// minutes and can be rotated the moment it leaks; this one keys rows that outlive the accounts
/// they may name, so rotating it silently orphans every existing digest. Two lifetimes, two keys.
/// </para>
/// </remarks>
public sealed class PhoneHasher
{
    private readonly byte[] _key;

    public PhoneHasher(IOptions<AuthPolicyOptions> options, IHostEnvironment environment, ILogger<PhoneHasher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var key = options.Value.PhoneHashKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            _key = Encoding.UTF8.GetBytes(key);
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Auth:PhoneHashKey is required outside Development. Without it iam.phone_lookups.phone_hash " +
                "is an unkeyed digest of a 10^8 number space, which is not a hash of a phone number so much " +
                "as a slow spelling of one (P-03).");
        }

        _key = RandomNumberGenerator.GetBytes(32);
        logger.LogWarning(
            "Auth:PhoneHashKey is not configured; using an ephemeral key. Lookup digests will not correlate " +
            "across a restart. Development only.");
    }

    /// <summary>The digest for a normalised E.164 number.</summary>
    public byte[] Hash(string phoneE164)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneE164);
        return HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(phoneE164));
    }
}

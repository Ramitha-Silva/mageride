using System.Security.Cryptography;
using System.Text;
using MageRide.Ride.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>
/// Reduces an E.164 number to the keyed digest <c>rides.rides.rider_phone_hash</c> and
/// <c>rides.location_requests.rider_phone_hash</c> store (P-03).
/// </summary>
/// <remarks>
/// <para>
/// A plain SHA-256 of a Sri Lankan mobile is not a hiding place: <c>+947XXXXXXXX</c> is 10⁸
/// candidates, seconds of work to enumerate. The HMAC key is what makes a leaked table useless
/// without it, which is why <c>Ride:PhoneHashKey</c> is required outside Development rather than
/// defaulted — the same argument iam-svc's <c>PhoneHasher</c> makes about
/// <c>iam.phone_lookups</c> (C027).
/// </para>
/// <para>
/// <b>Deliberately not iam-svc's key and deliberately not shared with it.</b> A digest that
/// correlated across the two services would let a leak of either table identify the subjects of the
/// other, and neither service ever needs to compare its rows with the other's — ride-svc asks
/// iam-svc about a number, never about a digest. Two bounded contexts, two key spaces.
/// </para>
/// <para>
/// <b>Deliberately not the OTP pepper</b> (<see cref="PackageOtpCodec"/>). That one keys a code
/// that lives for one delivery and can be rotated the moment it leaks; this one keys rows that
/// outlive the rides they belong to, so rotating it partitions the table rather than re-keying it.
/// </para>
/// </remarks>
public sealed class RiderPhoneHasher
{
    private readonly byte[] _key;

    public RiderPhoneHasher(
        IOptions<RideOptions> options, IHostEnvironment environment, ILogger<RiderPhoneHasher> logger)
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
                "Ride:PhoneHashKey is required outside Development. Without it rider_phone_hash is an " +
                "unkeyed digest of a 10^8 number space, which is not a hash of a phone number so much as a " +
                "slow spelling of one (P-03).");
        }

        _key = RandomNumberGenerator.GetBytes(32);

        logger.LogWarning(
            "Ride:PhoneHashKey is not configured; using an ephemeral key. Rider digests will not correlate " +
            "across a restart, so the P-12 audit cannot group a booker's requests by subject. Development only.");
    }

    /// <summary>The digest for a number already normalised by <c>RiderPhone.TryNormalise</c>.</summary>
    public byte[] Hash(string phoneE164)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneE164);

        return HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(phoneE164));
    }
}

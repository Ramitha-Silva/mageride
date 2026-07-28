using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Otp;

/// <summary>
/// Mints six-digit OTPs and turns them into the <c>iam.otp_attempts.otp_hash</c> the schema
/// stores ("never the OTP itself").
/// </summary>
/// <remarks>
/// <para>
/// The hash is HMAC-SHA256 over <c>{authId}:{code}</c>, keyed with <c>Otp:PepperKey</c>. The
/// <c>authId</c> is a per-attempt salt, so two users with the same code hash differently; the
/// pepper means a leaked table is not a 10^6 offline search per row.
/// </para>
/// <para>
/// Six digits is the contract's <c>^\d{6}$</c>, not a setting. <see cref="RandomNumberGenerator"/>
/// rather than <see cref="Random"/> — a predictable OTP is a login.
/// </para>
/// </remarks>
public sealed class OtpCodes
{
    /// <summary>Digits in a code. Pinned by the contract's <c>otp</c> pattern.</summary>
    public const int CodeLength = 6;

    private readonly byte[] _pepper;

    public OtpCodes(IOptions<OtpOptions> options, IHostEnvironment environment, ILogger<OtpCodes> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var pepper = options.Value.PepperKey;
        if (!string.IsNullOrWhiteSpace(pepper))
        {
            _pepper = Encoding.UTF8.GetBytes(pepper);
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Otp:PepperKey is required outside Development. Without it a leaked iam.otp_attempts row " +
                "reduces an OTP to a 10^6 offline search.");
        }

        _pepper = RandomNumberGenerator.GetBytes(32);
        logger.LogWarning(
            "Otp:PepperKey is not configured; using an ephemeral pepper. OTPs minted before a restart " +
            "cannot be verified after it. Development only.");
    }

    /// <summary>A uniformly distributed six-digit code, leading zeros preserved.</summary>
    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>The value stored in <c>otp_hash</c>.</summary>
    public byte[] Hash(Guid authId, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes($"{authId:D}:{code}"));
    }

    /// <summary>Constant-time comparison of a presented code against a stored hash.</summary>
    public bool Matches(Guid authId, string presented, byte[] storedHash)
    {
        ArgumentNullException.ThrowIfNull(storedHash);

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Hash(authId, presented), storedHash);
    }
}

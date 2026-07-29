using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>
/// The two 4-digit package OTPs: minted at booking, hashed at rest, verified at the handoffs
/// (P-07, ADD §11.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>The plaintext leaves the server exactly once</b> — the pickup code in the booking response to
/// the sender, the delivery code to the recipient through notification-svc. Nothing reads either
/// back: <c>rides.rides</c> stores <c>HMAC-SHA256</c> digests under a per-environment pepper
/// (<c>Ride:OtpPepper</c>), and verification re-derives the digest from the code the driver typed.
/// </para>
/// <para>
/// <b>Four digits is 10⁴, and the pepper is not what makes that safe.</b> The attempt budget is —
/// five tries per OTP and then <c>423 otp-locked</c> (P-07), which caps an online guess at 0.05%.
/// The pepper's job is different: it makes the stored digests useless to somebody who has the table
/// and not the key, which an unkeyed 4-digit hash would not be for even a moment.
/// </para>
/// <para>
/// <b>The message is bound to the ride and to the gate.</b> <c>(passengerId, clientRequestId)</c> is
/// R-18's key, unique in <c>rides.rides</c> and known before the row is inserted — so two rides that
/// happen to mint the same four digits store different digests, and a rainbow table over 10⁴ codes
/// buys nothing. The purpose is in the message for the same reason at a smaller scale: a driver who
/// was read the pickup code cannot spend it at the door.
/// </para>
/// </remarks>
public sealed class PackageOtpCodec
{
    /// <summary>P-07 / D5' §11: "two **4-digit** OTPs".</summary>
    public const int Digits = 4;

    private const int UpperBound = 10_000;

    private readonly byte[] _pepper;

    public PackageOtpCodec(IOptions<RideOptions> options, IHostEnvironment environment, ILogger<PackageOtpCodec> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var pepper = options.Value.OtpPepper;

        if (!string.IsNullOrWhiteSpace(pepper))
        {
            _pepper = Encoding.UTF8.GetBytes(pepper);
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Ride:OtpPepper is required outside Development. Without it the package OTP digests in " +
                "rides.rides are an unkeyed hash of four digits, which anybody holding the table can invert " +
                "by counting to ten thousand (P-07).");
        }

        _pepper = RandomNumberGenerator.GetBytes(32);

        logger.LogWarning(
            "Ride:OtpPepper is not configured; using an ephemeral pepper. Every package booked before a " +
            "restart becomes undeliverable, because its stored digests no longer match any code. Development only.");
    }

    /// <summary>A fresh code, uniformly distributed over the 10 000 (P-07).</summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator"/> rather than <c>Random</c>: the code is a credential, and
    /// a predictable one would let a driver who has seen a few of them guess the next.
    /// </remarks>
    public string Generate() =>
        RandomNumberGenerator.GetInt32(UpperBound).ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');

    /// <summary>
    /// The digest <c>rides.rides.pickup_otp_hash</c> / <c>delivery_otp_hash</c> holds for
    /// <paramref name="otp"/> on this ride's <paramref name="purpose"/> gate.
    /// </summary>
    public byte[] Hash(Guid passengerId, Guid clientRequestId, PackageOtpPurpose purpose, string otp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(otp);

        var message = string.Create(
            CultureInfo.InvariantCulture, $"{passengerId:D}:{clientRequestId:D}:{purpose}:{otp}");

        return HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(message));
    }

    /// <summary>Whether <paramref name="otp"/> is four digits — the shape the contract publishes.</summary>
    /// <remarks>
    /// A malformed code is refused before it is hashed and therefore <b>without spending an
    /// attempt</b>: the five-try budget exists to bound guessing at the code, and a client that sent
    /// three characters has not guessed anything. It is also what keeps a fat-fingered driver out of
    /// the admin queue.
    /// </remarks>
    public static bool IsWellFormed(string? otp) =>
        otp is { Length: Digits } && otp.All(char.IsAsciiDigit);
}

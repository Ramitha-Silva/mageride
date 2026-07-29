using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Rides;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Tests.Domain;

/// <summary>
/// P-07's two 4-digit codes: their shape, and the four things their digest is bound to.
/// </summary>
public sealed class PackageOtpCodecTests
{
    private static PackageOtpCodec Codec(string pepper = "c037-test-pepper") =>
        new(
            Options.Create(new RideOptions { OtpPepper = pepper }),
            new StubEnvironment(Environments.Production),
            NullLogger<PackageOtpCodec>.Instance);

    [Fact]
    public void A_generated_code_is_always_four_digits()
    {
        var codec = Codec();

        // 10 000 draws over a 10 000-wide space: enough that a generator which forgot to pad, or
        // which reached past the bound, would have to be lucky ten thousand times running.
        for (var i = 0; i < 10_000; i++)
        {
            var otp = codec.Generate();

            Assert.Equal(4, otp.Length);
            Assert.True(PackageOtpCodec.IsWellFormed(otp));
        }
    }

    [Fact]
    public void The_generator_reaches_both_ends_of_the_range()
    {
        var codec = Codec();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 20_000; i++)
        {
            seen.Add(codec.Generate());
        }

        // 20 000 uniform draws over 10 000 codes reach 10 000·(1 − e⁻²) ≈ 8 647 of them; a
        // generator that never emitted, say, the top decile would land visibly under that.
        Assert.True(seen.Count > 8_000, $"Only {seen.Count} distinct codes in 20 000 draws.");
        Assert.Contains(seen, code => code[0] == '0');
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("12a4")]
    [InlineData(" 123")]
    public void A_malformed_code_is_not_well_formed(string? otp) =>
        Assert.False(PackageOtpCodec.IsWellFormed(otp));

    /// <summary>
    /// The message is <c>(passengerId, clientRequestId, purpose, otp)</c>, so the same four digits
    /// hash differently on two rides and at the two ends of one delivery.
    /// </summary>
    [Fact]
    public void The_digest_is_bound_to_the_ride_and_to_the_gate()
    {
        var codec = Codec();
        var passenger = Guid.NewGuid();
        var request = Guid.NewGuid();

        var pickup = codec.Hash(passenger, request, PackageOtpPurpose.Pickup, "4829");

        Assert.NotEqual(pickup, codec.Hash(passenger, request, PackageOtpPurpose.Delivery, "4829"));
        Assert.NotEqual(pickup, codec.Hash(Guid.NewGuid(), request, PackageOtpPurpose.Pickup, "4829"));
        Assert.NotEqual(pickup, codec.Hash(passenger, Guid.NewGuid(), PackageOtpPurpose.Pickup, "4829"));
        Assert.NotEqual(pickup, codec.Hash(passenger, request, PackageOtpPurpose.Pickup, "4830"));

        // …and the same four inputs always produce the same digest, or no verification could work.
        Assert.Equal(pickup, codec.Hash(passenger, request, PackageOtpPurpose.Pickup, "4829"));
    }

    [Fact]
    public void A_different_pepper_produces_a_different_digest()
    {
        var passenger = Guid.NewGuid();
        var request = Guid.NewGuid();

        Assert.NotEqual(
            Codec("pepper-one").Hash(passenger, request, PackageOtpPurpose.Pickup, "4829"),
            Codec("pepper-two").Hash(passenger, request, PackageOtpPurpose.Pickup, "4829"));
    }

    /// <summary>
    /// An unkeyed digest of four digits is a table of ten thousand rows, so a deployment without a
    /// pepper is refused rather than allowed to hash badly.
    /// </summary>
    [Fact]
    public void A_missing_pepper_outside_development_is_a_failed_start_up()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new PackageOtpCodec(
            Options.Create(new RideOptions()),
            new StubEnvironment(Environments.Production),
            NullLogger<PackageOtpCodec>.Instance));

        Assert.Contains("Ride:OtpPepper", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_falls_back_to_an_ephemeral_pepper()
    {
        var codec = new PackageOtpCodec(
            Options.Create(new RideOptions()),
            new StubEnvironment(Environments.Development),
            NullLogger<PackageOtpCodec>.Instance);

        Assert.Equal(4, codec.Generate().Length);
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "ride-svc-tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

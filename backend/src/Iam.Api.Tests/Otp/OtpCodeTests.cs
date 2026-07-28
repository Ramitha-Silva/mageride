using System.Text.RegularExpressions;
using MageRide.Iam.Configuration;
using MageRide.Iam.Otp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Tests.Otp;

/// <summary>
/// The code itself: six digits (the contract's <c>^\d{6}$</c>), and never stored in the clear —
/// <c>iam.otp_attempts.otp_hash</c>'s comment says as much.
/// </summary>
public sealed partial class OtpCodeTests
{
    private static OtpCodes Create(string? pepper = "pepper", TestEnvironment? environment = null) =>
        new(Options.Create(new OtpOptions { PepperKey = pepper }),
            environment ?? TestEnvironment.Development,
            NullLogger<OtpCodes>.Instance);

    [Fact]
    public void Every_code_is_six_digits_including_the_ones_that_start_with_zero()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.Matches(SixDigits(), OtpCodes.NewCode());
        }
    }

    [Fact]
    public void Codes_are_not_predictable_enough_to_guess()
    {
        // Not a statistical test — a sanity check that the generator is not a constant or a
        // counter. A predictable OTP is a login.
        var codes = Enumerable.Range(0, 200).Select(_ => OtpCodes.NewCode()).ToHashSet(StringComparer.Ordinal);

        Assert.True(codes.Count > 150, $"only {codes.Count} distinct codes in 200 draws");
    }

    [Fact]
    public void A_hash_matches_only_its_own_code_and_its_own_attempt()
    {
        var codes = Create();
        var authId = Guid.NewGuid();
        var hash = codes.Hash(authId, "123456");

        Assert.True(codes.Matches(authId, "123456", hash));
        Assert.False(codes.Matches(authId, "123457", hash));

        // The authId is the per-attempt salt: the same code under another attempt is a different
        // hash, so one leaked pair does not unlock a second sign-in.
        Assert.False(codes.Matches(Guid.NewGuid(), "123456", hash));
    }

    [Fact]
    public void An_empty_entry_is_a_miss_not_a_match()
    {
        var codes = Create();
        var authId = Guid.NewGuid();

        Assert.False(codes.Matches(authId, string.Empty, codes.Hash(authId, "123456")));
    }

    [Fact]
    public void The_pepper_is_what_makes_the_stored_hash_worthless_on_its_own()
    {
        var authId = Guid.NewGuid();

        Assert.False(Create(pepper: "other").Matches(authId, "123456", Create(pepper: "pepper").Hash(authId, "123456")));
    }

    [Fact]
    public void Outside_development_a_pepper_is_mandatory()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Create(pepper: null, TestEnvironment.Production));

        Assert.Contains("Otp:PepperKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_falls_back_to_an_ephemeral_pepper_so_a_clean_checkout_runs()
    {
        var codes = Create(pepper: null);
        var authId = Guid.NewGuid();

        Assert.True(codes.Matches(authId, "123456", codes.Hash(authId, "123456")));
    }

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex SixDigits();
}

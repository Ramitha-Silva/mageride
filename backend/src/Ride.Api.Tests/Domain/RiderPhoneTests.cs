using MageRide.Ride.Domain;

namespace MageRide.Ride.Tests.Domain;

/// <summary>
/// <c>_shared.yaml#PhoneE164</c>'s <c>^\+947\d{8}$</c>, and the spellings a human types instead.
/// </summary>
/// <remarks>
/// Normalisation is load-bearing rather than cosmetic: <c>rider_phone_hash</c> is a digest, so two
/// spellings of one number would hash to two subjects and the P-12 audit would show two people
/// where there is one.
/// </remarks>
public sealed class RiderPhoneTests
{
    [Theory]
    [InlineData("+94771234567")]
    [InlineData("0094771234567")]
    [InlineData("0771234567")]
    [InlineData("771234567")]
    [InlineData("077 123 4567")]
    [InlineData("+94 77-123-4567")]
    [InlineData("(077) 1234567")]
    public void Every_spelling_of_one_number_normalises_to_the_same_subject(string spelling)
    {
        Assert.True(RiderPhone.TryNormalise(spelling, out var normalised));
        Assert.Equal("+94771234567", normalised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+94112345678")]   // Colombo landline — not a mobile, so nothing can reach it by SMS or FCM
    [InlineData("+447700900123")]  // a UK mobile: valid E.164, not a number this platform serves
    [InlineData("07712345")]       // too short
    [InlineData("07712345678")]    // too long
    [InlineData("not-a-number")]
    public void Anything_that_is_not_a_sri_lankan_mobile_is_refused(string? value)
    {
        Assert.False(RiderPhone.TryNormalise(value, out var normalised));
        Assert.Equal(string.Empty, normalised);
    }

    /// <summary>
    /// The length bound is a guard, not a parse rule: a caller that sent a megabyte of digits must
    /// not get a megabyte of stack.
    /// </summary>
    [Fact]
    public void An_absurdly_long_value_is_refused_without_being_scanned()
    {
        Assert.False(RiderPhone.TryNormalise(new string('7', 4096), out _));
    }
}

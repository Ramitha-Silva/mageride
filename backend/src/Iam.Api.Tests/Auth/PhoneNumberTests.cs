using MageRide.Iam.Domain;

namespace MageRide.Iam.Tests.Auth;

/// <summary>
/// <c>_shared.yaml#/schemas/PhoneE164</c> is <c>^\+947\d{8}$</c> and D3' answers anything else
/// <c>400 invalid-phone</c>. These are the spellings a Sri Lankan user actually types.
/// </summary>
public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+94771234567")]
    [InlineData("0771234567")]
    [InlineData("94771234567")]
    [InlineData("+94 77 123 4567")]
    [InlineData("+94-77-123-4567")]
    [InlineData("077 123 4567")]
    [InlineData("+94 (77) 123.4567")]
    public void Every_spelling_of_one_number_normalises_to_the_same_e164(string input)
    {
        Assert.True(PhoneNumbers.TryNormalise(input, out var normalised));
        Assert.Equal("+94771234567", normalised);
    }

    [Theory]
    [InlineData("+94112345678")]   // Colombo landline: the 7 after +94 is the mobile prefix
    [InlineData("+919876543210")]  // India
    [InlineData("+9477123456")]    // too short
    [InlineData("+947712345678")]  // too long
    [InlineData("077123456a")]
    [InlineData("not a phone")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_a_sri_lankan_mobile_is_refused(string? input)
    {
        Assert.False(PhoneNumbers.TryNormalise(input, out var normalised));
        Assert.Equal(string.Empty, normalised);
    }

    [Fact]
    public void A_leading_zero_is_national_notation_only_when_there_is_no_country_code()
    {
        // +940771234567 is a country code followed by a national trunk prefix — a real typo, and
        // one we must not "fix" by dropping the zero, because the result would be someone else.
        Assert.False(PhoneNumbers.TryNormalise("+940771234567", out _));
    }
}

using MageRide.Registry.Domain;

namespace MageRide.Registry.Tests.Domain;

/// <summary>
/// D-37 makes a registration unique across the live set, and 0303 enforces it with a unique
/// index over the stored text. Canonicalisation is what stops the rule being bypassed by
/// retyping the same plate differently.
/// </summary>
public sealed class RegistrationNumberTests
{
    [Theory]
    [InlineData("WP-QA-1234", "WP-QA-1234")]
    [InlineData("wp-qa-1234", "WP-QA-1234")]
    [InlineData("WP QA 1234", "WP-QA-1234")]
    [InlineData("  wp   qa-1234  ", "WP-QA-1234")]
    [InlineData("WP_QA_1234", "WP-QA-1234")]
    [InlineData("WP - QA - 1234", "WP-QA-1234")]
    [InlineData("QA1234", "QA1234")]
    [InlineData("-QA-1234-", "QA-1234")]
    // Whitespace of any kind is a separator, not a character: a tab or a stray newline is
    // copy-paste noise, and refusing it would fail a plate the driver typed correctly.
    [InlineData("WP-QA-1234\n", "WP-QA-1234")]
    [InlineData("WP\tQA 1234", "WP-QA-1234")]
    public void The_same_plate_written_differently_canonicalises_to_one_value(string input, string expected)
    {
        Assert.True(RegistrationNumbers.TryNormalise(input, out var normalised));
        Assert.Equal(expected, normalised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("- -")]
    public void An_empty_or_separator_only_registration_is_refused(string? input) =>
        Assert.False(RegistrationNumbers.TryNormalise(input, out _));

    [Theory]
    [InlineData("WP/QA/1234")]
    [InlineData("WP.QA.1234")]
    [InlineData("WP-QA-1234!")]
    [InlineData("WP-QA-1234\0")]
    [InlineData("ශ්‍රී-1234")]
    public void A_character_a_plate_cannot_contain_is_refused_rather_than_stripped(string input)
    {
        // Deleting it would let two genuinely different plates canonicalise to the same value,
        // which turns D-37 from a uniqueness rule into a collision.
        Assert.False(RegistrationNumbers.TryNormalise(input, out _));
    }

    [Fact]
    public void A_registration_longer_than_the_contract_allows_is_refused()
    {
        Assert.False(RegistrationNumbers.TryNormalise(new string('A', RegistrationNumbers.MaxLength + 1), out _));
        Assert.True(RegistrationNumbers.TryNormalise(new string('A', RegistrationNumbers.MaxLength), out _));
    }

    [Fact]
    public void Canonicalisation_is_idempotent()
    {
        Assert.True(RegistrationNumbers.TryNormalise("  wp  qa -1234 ", out var once));
        Assert.True(RegistrationNumbers.TryNormalise(once, out var twice));
        Assert.Equal(once, twice);
    }
}

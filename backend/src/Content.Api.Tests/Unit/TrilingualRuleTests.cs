using MageRide.Content.Domain;
using MageRide.Shared.Errors;

namespace MageRide.Content.Tests.Unit;

/// <summary>
/// The trilingual rule and the language fallback, as pure functions — no container, because neither
/// depends on one.
/// </summary>
public sealed class TrilingualRuleTests
{
    [Fact]
    public void All_three_languages_make_a_value()
    {
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["si"] = " සිංහල ",
            ["ta"] = "தமிழ்",
            ["en"] = "English",
        };

        Assert.True(TrilingualText.TryCreate(raw, out var text, out var missing));
        Assert.Empty(missing);

        // Trimmed on the way in, so a copy-pasted string with a trailing newline does not become a
        // banner with a trailing newline.
        Assert.Equal("සිංහල", text!.Sinhala);
        Assert.Equal("தமிழ்", text["ta"]);
        Assert.Equal("English", text.English);
    }

    [Theory]
    [InlineData("si")]
    [InlineData("ta")]
    [InlineData("en")]
    public void One_absent_language_names_that_language(string absent)
    {
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["si"] = "සිංහල",
            ["ta"] = "தமிழ்",
            ["en"] = "English",
        };

        raw.Remove(absent);

        Assert.False(TrilingualText.TryCreate(raw, out var text, out var missing));
        Assert.Null(text);
        Assert.Equal([absent], missing);
    }

    /// <summary>
    /// Whitespace is absence. A body of <c>" "</c> passes a NOT NULL and every shape check there is,
    /// and produces a message with no text in exactly one language.
    /// </summary>
    [Fact]
    public void Whitespace_is_absence()
    {
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["si"] = "  ",
            ["ta"] = "\t",
            ["en"] = "English",
        };

        Assert.False(TrilingualText.TryCreate(raw, out _, out var missing));
        Assert.Equal(["si", "ta"], missing);
    }

    [Fact]
    public void An_empty_request_names_all_three()
    {
        Assert.False(TrilingualText.TryCreate(null, out _, out var missing));
        Assert.Equal(["si", "ta", "en"], missing);
    }

    /// <summary>
    /// The rejection is a field-level <c>validation-failed</c>, which is what "rejected with a clear
    /// error" means for a form the author has to fix.
    /// </summary>
    [Fact]
    public void Require_throws_a_field_level_validation_failure()
    {
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal) { ["en"] = "English" };

        var exception = Assert.Throws<MageRideValidationException>(
            () => TrilingualText.Require(raw, "bodyByLang"));

        Assert.Equal(MageRideErrors.ValidationFailed, exception.Error);
        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains("Sinhala (si)", exception.Errors["bodyByLang.si"][0]);
        Assert.Contains("Tamil (ta)", exception.Errors["bodyByLang.ta"][0]);
    }

    /// <summary>
    /// A stored value that is not trilingual throws rather than serving two of three languages: such a
    /// row means a constraint was dropped or a writer bypassed this service.
    /// </summary>
    [Fact]
    public void A_stored_value_that_is_not_trilingual_throws()
    {
        var stored = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["en"] = "English",
            ["si"] = "සිංහල",
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => TrilingualText.FromStored(stored, "broadcast 1 message"));

        Assert.Contains("2 of 3", exception.Message);
        Assert.Contains("ta", exception.Message);
    }

    [Theory]
    [InlineData("si", "si")]
    [InlineData("SI", "si")]
    [InlineData(" ta ", "ta")]
    [InlineData("si-LK", "si")]
    [InlineData("ta_IN", "ta")]
    [InlineData("en-GB", "en")]
    public void A_device_locale_resolves_to_its_primary_subtag(string requested, string expected) =>
        Assert.Equal(expected, Languages.TryNormalise(requested));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("fr")]
    [InlineData("sinhala")]
    [InlineData("s")]
    public void Anything_else_names_no_language(string? requested) =>
        Assert.Null(Languages.TryNormalise(requested));

    /// <summary>
    /// Resolution falls back to English, which is the language every operator, admin and developer on
    /// the platform reads.
    /// </summary>
    [Theory]
    [InlineData(null, "en")]
    [InlineData("fr", "en")]
    [InlineData("si", "si")]
    public void Resolution_falls_back_to_english(string? requested, string expected) =>
        Assert.Equal(expected, Languages.Resolve(requested));

    /// <summary>
    /// Presentation order is Sinhala first (AL-26); fallback order is English first. Two different
    /// questions, and a single shared order would answer one of them wrongly.
    /// </summary>
    [Fact]
    public void The_two_orders_are_deliberately_different()
    {
        Assert.Equal(["si", "ta", "en"], Languages.All);
        Assert.Equal(["en", "si", "ta"], Languages.FallbackOrder);
    }
}

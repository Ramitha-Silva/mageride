using MageRide.Content.Domain;
using MageRide.Shared.Errors;

namespace MageRide.Content.Tests.Unit;

/// <summary>
/// The <c>{{name}}</c> placeholder syntax and the rule that the three languages of one template
/// interpolate the same variables.
/// </summary>
/// <remarks>
/// No spec defines the syntax beyond the seed's own examples (server_db_schema.md §20's
/// <c>{{pickup}} → {{dropoff}}</c> and D6' I-29.2's <c>{{link}}</c>), so these tests are where it is
/// pinned. Recorded in the C045 handoff.
/// </remarks>
public sealed class TemplatePlaceholderTests
{
    [Fact]
    public void The_seeded_syntax_is_extracted_in_first_seen_order()
    {
        Assert.Equal(
            ["pickup", "dropoff"],
            TemplatePlaceholders.Extract("New ride request: {{pickup}} → {{dropoff}}"));

        Assert.Equal(
            ["link"],
            TemplatePlaceholders.Extract("Confirm your pickup location: {{link}} — expires in 5 minutes."));
    }

    /// <summary>
    /// Repeats collapse: the set is what a caller has to supply, and the same variable used twice is
    /// one value.
    /// </summary>
    [Fact]
    public void A_repeated_placeholder_appears_once() =>
        Assert.Equal(["name"], TemplatePlaceholders.Extract("{{name}}, hello {{name}}"));

    /// <summary>Inner whitespace is what a hand-edited template acquires, and it is tolerated.</summary>
    [Fact]
    public void Inner_whitespace_is_tolerated() =>
        Assert.Equal(["pickup"], TemplatePlaceholders.Extract("From {{ pickup }}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No placeholders here.")]
    [InlineData("Not a placeholder: {single} or {{ }} or {{1st}}")]
    public void Nothing_that_is_not_a_placeholder_is_extracted(string? text) =>
        Assert.Empty(TemplatePlaceholders.Extract(text));

    [Fact]
    public void Three_agreeing_languages_pass()
    {
        var text = Build(
            "ඔබේ පාර්සලය: {{link}}",
            "உங்கள் பொதி: {{link}}",
            "Your package: {{link}}");

        TemplatePlaceholders.RequireConsistent(text, "bodyByLang");
    }

    /// <summary>
    /// A translation that dropped <c>{{link}}</c> is an SMS with no link, sent to the one recipient who
    /// has no app to find another way in.
    /// </summary>
    [Fact]
    public void A_language_missing_a_placeholder_is_rejected_naming_it()
    {
        var text = Build("ඔබේ පාර්සලය මාර්ගයේ ය.", "உங்கள் பொதி: {{link}}", "Your package: {{link}}");

        var exception = Assert.Throws<MageRideValidationException>(
            () => TemplatePlaceholders.RequireConsistent(text, "bodyByLang"));

        Assert.Equal(MageRideErrors.ValidationFailed, exception.Error);

        var message = Assert.Single(exception.Errors["bodyByLang.si"]);

        Assert.Contains("{{link}}", message);
        Assert.Contains("missing", message);
        Assert.DoesNotContain("bodyByLang.ta", exception.Errors.Keys);
    }

    [Fact]
    public void A_language_with_an_extra_placeholder_is_rejected_naming_it()
    {
        var text = Build("සිංහල {{driverName}}", "தமிழ்", "English");

        var exception = Assert.Throws<MageRideValidationException>(
            () => TemplatePlaceholders.RequireConsistent(text, "bodyByLang"));

        var message = Assert.Single(exception.Errors["bodyByLang.si"]);

        Assert.Contains("{{driverName}}", message);
        Assert.Contains("delivered literally", message);
    }

    /// <summary>
    /// Both directions in one language are reported together, so an author sees the whole diff rather
    /// than fixing one and resubmitting.
    /// </summary>
    [Fact]
    public void A_renamed_placeholder_reports_both_halves()
    {
        var text = Build("සිංහල {{from}}", "தமிழ் {{pickup}}", "English {{pickup}}");

        var exception = Assert.Throws<MageRideValidationException>(
            () => TemplatePlaceholders.RequireConsistent(text, "bodyByLang"));

        Assert.Equal(2, exception.Errors["bodyByLang.si"].Length);
        Assert.Contains("{{pickup}}", exception.Errors["bodyByLang.si"][0]);
        Assert.Contains("{{from}}", exception.Errors["bodyByLang.si"][1]);
    }

    /// <summary>
    /// English is the reference because it is the language every spec example is written in, so the
    /// error names what the author changed rather than what they left alone.
    /// </summary>
    [Fact]
    public void English_is_the_reference()
    {
        var text = Build("සිංහල", "தமிழ்", "English {{pickup}}");

        var exception = Assert.Throws<MageRideValidationException>(
            () => TemplatePlaceholders.RequireConsistent(text, "bodyByLang"));

        Assert.Equal(2, exception.Errors.Count);
        Assert.Contains("bodyByLang.si", exception.Errors.Keys);
        Assert.Contains("bodyByLang.ta", exception.Errors.Keys);
    }

    private static TrilingualText Build(string si, string ta, string en)
    {
        var raw = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["si"] = si,
            ["ta"] = ta,
            ["en"] = en,
        };

        Assert.True(TrilingualText.TryCreate(raw, out var text, out _));

        return text!;
    }
}

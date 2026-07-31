using MageRide.Notification.Domain;
using MageRide.Notification.Templates;

namespace MageRide.Notification.Tests.Unit;

/// <summary>
/// The <c>{{placeholder}}</c> substitution, and the failure it exists to prevent.
/// </summary>
/// <remarks>
/// The component's fourth definition of done is "every notification body is rendered in the
/// recipient's language with no hardcoded strings". The language half is an integration claim
/// (<c>LanguageRenderingTests</c>); this is the other half — that a template with a value missing
/// does not ship a sentence with a hole in it.
/// </remarks>
public sealed class TemplateRenderingTests
{
    private static ResolvedTemplate Template(string body, string? title = null, string language = "en") =>
        new("test_key", language, 1, title, body, TemplateRenderer.PlaceholdersOf(body));

    [Fact]
    public void Values_are_substituted_in_body_and_title()
    {
        var rendered = TemplateRenderer.Render(
            Template("Your payment of Rs {{amount}} has been received.", "Payment received"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["amount"] = "1,240.00" });

        Assert.Equal("Your payment of Rs 1,240.00 has been received.", rendered.Body);
        Assert.Equal("Payment received", rendered.Title);
    }

    /// <summary>
    /// D6' I-29.2's three SMS templates all carry <c>{{link}}</c>, and their recipients are the
    /// people with no app to find another way in. "Track it here: " is worse than no message at all,
    /// and it would go out silently.
    /// </summary>
    [Fact]
    public void A_missing_value_refuses_to_render_rather_than_leaving_a_hole()
    {
        var exception = Assert.Throws<TemplateRenderException>(() => TemplateRenderer.Render(
            Template("Your package is on the way. Track it here: {{link}}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["rideId"] = "7" }));

        Assert.Contains("link", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty string is a missing value, not a value — same reason.</summary>
    [Fact]
    public void An_empty_value_is_a_missing_value()
    {
        Assert.Throws<TemplateRenderException>(() => TemplateRenderer.Render(
            Template("Confirm your pickup location: {{link}}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["link"] = string.Empty }));
    }

    /// <summary>
    /// Most of the payload is for the app — deep links, ride ids, the kind switch — not for the
    /// sentence. An unused value is not an error.
    /// </summary>
    [Fact]
    public void Values_the_template_does_not_use_are_ignored()
    {
        var rendered = TemplateRenderer.Render(
            Template("A driver has accepted your ride."),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["deeplink"] = "mageride://ride/7",
                ["kind"] = "DRIVER_ASSIGNED",
            });

        Assert.Equal("A driver has accepted your ride.", rendered.Body);
    }

    /// <summary>
    /// A translator who typed an unmatched brace pair has made a typo, not a broken template. It is
    /// emitted verbatim; throwing would take a whole notification type down in one language.
    /// </summary>
    [Fact]
    public void An_unclosed_brace_pair_is_text()
    {
        var rendered = TemplateRenderer.Render(
            Template("Rs {{amount}} — see {{ for details"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["amount"] = "50.00" });

        Assert.Equal("Rs 50.00 — see {{ for details", rendered.Body);
    }

    [Fact]
    public void The_same_placeholder_may_appear_more_than_once()
    {
        var rendered = TemplateRenderer.Render(
            Template("{{name}} raised an SOS. Call {{name}} back."),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "Nimal" });

        Assert.Equal("Nimal raised an SOS. Call Nimal back.", rendered.Body);
    }

    [Fact]
    public void Placeholders_are_reported_once_each_in_order()
    {
        Assert.Equal(
            ["fare", "distance"],
            TemplateRenderer.PlaceholdersOf("Rs {{fare}}, pickup {{distance}} km away ({{fare}})"));
    }

    /// <summary>
    /// The three languages of one key have to interpolate the same set — content-svc refuses to
    /// publish otherwise — so a Sinhala body that lost <c>{{link}}</c> in translation is caught
    /// there. This asserts the scanner both sides rely on agrees across scripts.
    /// </summary>
    [Fact]
    public void Placeholders_are_found_in_every_script()
    {
        Assert.Equal(["link"], TemplateRenderer.PlaceholdersOf("ඔබේ පාර්සලය මාර්ගයේ ය: {{link}}"));
        Assert.Equal(["link"], TemplateRenderer.PlaceholdersOf("உங்கள் பொதி வழியில் உள்ளது: {{link}}"));
    }
}

/// <summary>Money, and the language tag, at the two boundaries where they become text.</summary>
public sealed class PayloadValueTests
{
    /// <summary>
    /// Every amount on the platform is an integer of minor units (CLAUDE.md), and this is the one
    /// place it becomes a decimal — at the boundary where a human reads it.
    /// </summary>
    [Theory]
    [InlineData(0L, "0.00")]
    [InlineData(5L, "0.05")]
    [InlineData(50L, "0.50")]
    [InlineData(100L, "1.00")]
    [InlineData(12_345L, "123.45")]
    [InlineData(1_000_000L, "10,000.00")]
    [InlineData(-15_000L, "-150.00")]
    public void Minor_units_become_rupees(long minor, string expected) =>
        Assert.Equal(expected, PayloadValues.Rupees(minor));

    [Fact]
    public void A_payload_round_trips_as_strings()
    {
        var written = PayloadValues.Write(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = "RIDE_OFFER",
            ["fare"] = "1,240.00",
        });

        var read = PayloadValues.Parse(written);

        Assert.Equal("RIDE_OFFER", read["kind"]);
        Assert.Equal("1,240.00", read["fare"]);
    }

    /// <summary>
    /// FCM refuses anything but strings in <c>data</c>, so a number or a nested object that reached
    /// the payload is flattened rather than dropped — the app still gets it, as text.
    /// </summary>
    [Fact]
    public void Non_string_members_are_flattened_rather_than_lost()
    {
        var read = PayloadValues.Parse("""{"ttl":300,"silent":true,"geo":{"lat":6.9,"lng":79.8},"nothing":null}""");

        Assert.Equal("300", read["ttl"]);
        Assert.Equal("true", read["silent"]);
        Assert.Contains("6.9", read["geo"], StringComparison.Ordinal);
        Assert.Equal(string.Empty, read["nothing"]);
    }

    [Fact]
    public void An_unreadable_payload_is_empty_rather_than_a_throw() =>
        Assert.Empty(PayloadValues.Parse("not json"));

    [Theory]
    [InlineData("si", "si")]
    [InlineData("si-LK", "si")]
    [InlineData("SI", "si")]
    [InlineData("ta_IN", "ta")]
    [InlineData("en-GB", "en")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public void A_locale_resolves_to_one_of_the_three(string? value, string expected) =>
        Assert.Equal(expected, Languages.Normalise(value));
}

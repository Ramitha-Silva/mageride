using System.Text.RegularExpressions;
using MageRide.Shared.Errors;

namespace MageRide.Content.Domain;

/// <summary>
/// The <c>{{name}}</c> variables a notification template expects, and the rule that the three
/// languages of one template expect the same ones.
/// </summary>
/// <remarks>
/// <para>
/// The placeholder syntax is the seed's: <c>'New ride request: {{pickup}} → {{dropoff}}'</c>
/// (server_db_schema.md §20) and <c>'{{link}}'</c> (D6' I-29.2). <b>No spec defines it</b> beyond
/// those examples — recorded in the C045 handoff — so this is the one place it is written down, and
/// substitution itself is notification-svc's (C051): a GET with no body cannot carry values, and
/// the contract's response is the template, not a rendered message.
/// </para>
/// <para>
/// <b>Why the cross-language check is a publish-time rejection rather than a warning.</b> D6'
/// I-29.2's SMS templates carry <c>{{link}}</c> — the package-tracking link, the proxy-ride link,
/// the 5-minute pickup-confirm link. A Sinhala body that lost that placeholder in translation is
/// not a slightly worse message: it is an SMS with no link, sent to the one recipient who cannot
/// use the app to find another way in. The three bodies of a template are three spellings of one
/// message and they interpolate one set of values, so an author who drops or renames a variable in
/// one language has made a mistake that no test downstream can catch — by then the message has been
/// sent.
/// </para>
/// </remarks>
internal static partial class TemplatePlaceholders
{
    /// <summary>
    /// Every distinct placeholder in <paramref name="text"/>, in first-seen order.
    /// </summary>
    /// <remarks>
    /// Order is stable so the contract's <c>placeholders</c> array is diffable between versions and
    /// a caller comparing two of them sees a real change rather than a reshuffle.
    /// </remarks>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in Pattern().EnumerateMatches(text))
        {
            var token = text.AsSpan(match.Index, match.Length);
            var name = Name(token);

            if (seen.Add(name))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>
    /// Rejects a trilingual field whose three languages do not interpolate the same variables.
    /// </summary>
    public static void RequireConsistent(TrilingualText text, string field)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        var reference = Extract(text[Languages.English]);
        var referenceSet = new HashSet<string>(reference, StringComparer.Ordinal);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var language in Languages.All)
        {
            if (language == Languages.English)
            {
                continue;
            }

            var candidate = new HashSet<string>(Extract(text[language]), StringComparer.Ordinal);

            var missing = referenceSet.Except(candidate, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var extra = candidate.Except(referenceSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

            if (missing.Length == 0 && extra.Length == 0)
            {
                continue;
            }

            var messages = new List<string>(2);

            if (missing.Length > 0)
            {
                messages.Add(
                    $"is missing {Describe(missing)}, which the English text interpolates. A message that "
                    + "loses a placeholder in one language loses the value it carried — most of the D6' "
                    + "I-29.2 templates interpolate a tracking link.");
            }

            if (extra.Length > 0)
            {
                messages.Add(
                    $"interpolates {Describe(extra)}, which the English text does not. Nothing supplies a "
                    + "value for it, so it would be delivered literally.");
            }

            errors[$"{field}.{language}"] = [.. messages];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(
                errors,
                $"The three languages of {field} do not interpolate the same placeholders. Expected "
                + $"{(reference.Count == 0 ? "none" : Describe(reference))}, from the English text.");
        }
    }

    private static string Describe(IEnumerable<string> names) =>
        string.Join(", ", names.Select(static name => $"{{{{{name}}}}}"));

    private static string Name(ReadOnlySpan<char> token) =>
        token[2..^2].Trim().ToString();

    // `{{ name }}` with optional inner whitespace, which is what a hand-edited template acquires;
    // the name itself is the identifier set the seeded templates use.
    [GeneratedRegex(@"\{\{\s*[A-Za-z_][A-Za-z0-9_]*\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

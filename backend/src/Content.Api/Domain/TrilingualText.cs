using System.Diagnostics.CodeAnalysis;
using MageRide.Shared.Errors;

namespace MageRide.Content.Domain;

/// <summary>
/// A string that exists in Sinhala, Tamil and English — the only kind of user-facing string this
/// platform has (D-26).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no way to construct a partial one.</b> That is the point: this component's first
/// fence is "every user-facing string in every template exists in all three languages or the
/// template is invalid", and a type that can hold two languages would let the rule be a check
/// somebody forgets to call. The publish paths take this type; the only door in is
/// <see cref="TryCreate"/>, which reports what is missing rather than filling it in.
/// </para>
/// <para>
/// The database says the same thing twice more — <c>ck_broadcasts_trilingual</c> (C005) and
/// migration 1307's deferred constraint trigger on <c>content.notification_templates</c> — because
/// this service is not the only thing that can write those tables.
/// </para>
/// </remarks>
internal sealed class TrilingualText
{
    private readonly Dictionary<string, string> _values;

    private TrilingualText(Dictionary<string, string> values) => _values = values;

    /// <summary>The three strings, keyed by language code.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public string Sinhala => _values[Languages.Sinhala];

    public string Tamil => _values[Languages.Tamil];

    public string English => _values[Languages.English];

    /// <summary>The string in one of the three languages.</summary>
    public string this[string language] => _values[language];

    /// <summary>
    /// Builds one from an untrusted map, or reports the languages that are missing or blank.
    /// </summary>
    /// <remarks>
    /// Whitespace-only counts as missing. A body of <c>" "</c> passes a <c>NOT NULL</c> and every
    /// JSON-shape check there is, and produces a push notification with no text in exactly one
    /// language — the failure mode this whole type exists to prevent, arriving through the front
    /// door.
    /// </remarks>
    public static bool TryCreate(
        IReadOnlyDictionary<string, string?>? raw,
        [NotNullWhen(true)] out TrilingualText? text,
        out IReadOnlyList<string> missing)
    {
        var absent = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var language in Languages.All)
        {
            if (raw is not null
                && raw.TryGetValue(language, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                values[language] = value.Trim();
            }
            else
            {
                absent.Add(language);
            }
        }

        missing = absent;

        if (absent.Count > 0)
        {
            text = null;
            return false;
        }

        text = new TrilingualText(values);
        return true;
    }

    /// <summary>
    /// Builds one from a map that is known to be complete — the read path, over rows the write
    /// path or a migration produced.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The stored value is not trilingual. Thrown rather than papered over: a row like that means a
    /// constraint was dropped or a writer bypassed this service, and serving two of three languages
    /// while pretending otherwise is how a missing translation reaches a user silently.
    /// </exception>
    public static TrilingualText FromStored(IReadOnlyDictionary<string, string?>? stored, string what)
    {
        if (TryCreate(stored, out var text, out var missing))
        {
            return text;
        }

        throw new InvalidOperationException(
            $"{what} is stored in {3 - missing.Count} of 3 languages (missing {string.Join(", ", missing)}). "
            + "Every user-facing string exists in si, ta and en (D-26); check migration 1307's trigger.");
    }

    /// <summary>
    /// Validates a required trilingual field, throwing <c>400 validation-failed</c> naming the
    /// languages that are absent.
    /// </summary>
    /// <remarks>
    /// The field keys are the wire names (<c>bodyByLang.si</c>), so the Admin Portal can put the
    /// message under the input the author has to fix — the C045 definition of done is "publishing a
    /// template missing a language is rejected with a <i>clear</i> error", and "validation failed"
    /// with no field is not clear.
    /// </remarks>
    public static TrilingualText Require(IReadOnlyDictionary<string, string?>? raw, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (TryCreate(raw, out var text, out var missing))
        {
            return text;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var language in missing)
        {
            errors[$"{field}.{language}"] =
                [$"{LanguageNames[language]} is required. Every user-facing string exists in si, ta and en (D-26)."];
        }

        throw new MageRideValidationException(
            errors,
            $"{field} is missing {string.Join(", ", missing)}. A template, broadcast or slide that would ship "
            + "in fewer than three languages is not publishable.");
    }

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.Ordinal)
    {
        [Languages.Sinhala] = "Sinhala (si)",
        [Languages.Tamil] = "Tamil (ta)",
        [Languages.English] = "English (en)",
    };
}

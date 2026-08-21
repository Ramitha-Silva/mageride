namespace MageRide.Query.Geo;

/// <summary>
/// The three languages a geocode may be asked for, and how a <c>?lang=</c> value becomes one.
/// </summary>
/// <remarks>
/// <para>
/// D-26 and the root CLAUDE.md make every user-facing string trilingual, and a place name a
/// passenger reads on SCR-PA-008 is one. The set is the <c>LanguageCode</c> enum in
/// <c>backend/contracts/_shared.yaml</c>; adding a fourth is a contract change and a translation
/// project, never a config value.
/// </para>
/// <para>
/// <b>This is the fourth copy of that vocabulary</b> — <c>Content.Api/Domain/Languages.cs</c>,
/// <c>Notification.Api/Domain/NotificationRecords.cs</c> and
/// <c>Support.Api/Domain/SupportVocabulary.cs</c> each carry their own, because
/// <c>MageRide.Shared</c> has none and a service does not reference another service's assembly.
/// Consolidating the four into <c>MageRide.Shared</c> is worth doing and is not this change;
/// recorded here so the debt is visible rather than silently added to.
/// </para>
/// <para>
/// <b>Absent is not English.</b> <see cref="TryNormalise"/> answers <see langword="null"/> for a
/// missing or unrecognised value and the caller then sends Nominatim no <c>accept-language</c> at
/// all, which is what every client did before this parameter existed. Resolving absent to
/// <c>en</c> instead would change the answer given to every caller that has not been updated yet —
/// the iOS apps among them — from "whatever OSM's <c>name</c> says" to "the English tag, or
/// nothing".
/// </para>
/// </remarks>
internal static class GeoLanguages
{
    public const string Sinhala = "si";
    public const string Tamil = "ta";
    public const string English = "en";

    /// <summary>Presentation order: Sinhala first and default (AL-26).</summary>
    private static readonly string[] All = [Sinhala, Tamil, English];

    /// <summary>
    /// The language to ask Nominatim for, or <see langword="null"/> to ask for none.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, and it accepts the BCP 47 forms a mobile platform hands an app without
    /// being asked — <c>si-LK</c>, <c>ta-IN</c>, <c>en_US</c>. Deliberately not a general BCP 47
    /// parser: the primary subtag is matched against the three and nothing else. Same rule as
    /// <c>Content.Api</c>'s <c>Languages.TryNormalise</c>, so a client that sends its device
    /// locale verbatim is understood identically by both services.
    /// </remarks>
    public static string? TryNormalise(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var span = language.AsSpan().Trim();
        var separator = span.IndexOfAny('-', '_');
        var primary = separator < 0 ? span : span[..separator];

        foreach (var candidate in All)
        {
            if (primary.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}

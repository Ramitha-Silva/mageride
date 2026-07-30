using System.Diagnostics.CodeAnalysis;

namespace MageRide.Content.Domain;

/// <summary>
/// The three languages every user-facing string on this platform exists in (D-26, CLAUDE.md
/// "Trilingual resources"), and the order a request falls back through.
/// </summary>
/// <remarks>
/// <para>
/// The set is closed and it is closed in three places that have to agree: this vocabulary,
/// <c>ck_notification_templates_language</c> / <c>ck_faq_articles_language</c> (migration 1304) and
/// the <c>LanguageCode</c> enum in <c>backend/contracts/_shared.yaml</c>. Adding a fourth language
/// is a schema change, a contract change and a translation project — never a config value.
/// </para>
/// <para>
/// <b>Sinhala is first in <see cref="All"/> and English is first in <see cref="FallbackOrder"/>,
/// and both are deliberate.</b> AL-26 makes Sinhala the default the picker opens on, so that is the
/// order the carousel and the city list are rendered in. Fallback is the opposite question — what
/// to serve when the asked-for language is genuinely absent — and English is the one every
/// operator, admin and developer on the platform reads.
/// </para>
/// </remarks>
internal static class Languages
{
    public const string Sinhala = "si";
    public const string Tamil = "ta";
    public const string English = "en";

    /// <summary>Presentation order: Sinhala first and default (AL-26, D2' SCR-DA/DI-002).</summary>
    public static readonly string[] All = [Sinhala, Tamil, English];

    /// <summary>
    /// What to try when the requested language has no row. English first — see the type remarks.
    /// </summary>
    public static readonly string[] FallbackOrder = [English, Sinhala, Tamil];

    /// <summary>Whether <paramref name="language"/> is one of the three.</summary>
    public static bool IsKnown([NotNullWhen(true)] string? language) =>
        language is not null && Array.IndexOf(All, language) >= 0;

    /// <summary>
    /// Normalises a <c>?lang=</c> value, or returns <see langword="null"/> when it names nothing.
    /// </summary>
    /// <remarks>
    /// Case- and culture-insensitive, and it accepts the BCP 47 forms a mobile platform hands an
    /// app without being asked — <c>si-LK</c>, <c>ta-IN</c>, <c>en_US</c>. A client that sent its
    /// device locale verbatim gets its language rather than English, which is the difference
    /// between a working picker and one that silently does nothing. <b>Not</b> a general BCP 47
    /// parser: the primary subtag is matched against the three and nothing else.
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

    /// <summary>
    /// The language to serve for a <c>?lang=</c> value: the requested one, or
    /// <see cref="English"/>.
    /// </summary>
    /// <remarks>
    /// <c>_shared.yaml</c>'s <c>Lang</c> parameter documents the order as "requested → the caller's
    /// profile language → <c>en</c>", and the middle step is deliberately not implemented here:
    /// <c>iam.users.language</c> belongs to iam-svc, and reading another bounded context's table to
    /// resolve a banner would put an availability dependency on the read for no gain — the apps
    /// send the profile language as <c>?lang=</c>, having stored it at onboarding (AL-26). Recorded
    /// in the C045 handoff.
    /// </remarks>
    public static string Resolve(string? requested) => TryNormalise(requested) ?? English;
}

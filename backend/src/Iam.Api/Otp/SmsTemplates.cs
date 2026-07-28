using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MageRide.Iam.Otp;

/// <summary>
/// The trilingual SMS bodies, read from the embedded <c>Resources/sms-templates.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The platform rule is that no user-facing string is hardcoded and every one exists in Sinhala,
/// Tamil and English (CLAUDE.md, D-26). The OTP message is the first user-facing string the
/// backend sends and the only one iam-svc owns, so it lives in a resource file even though there
/// is exactly one of it — the alternative is a string literal in a sender that the next component
/// copies.
/// </para>
/// <para>
/// The resource is embedded, not a content file: a container image that shipped without it would
/// fail at the first sign-in rather than at build time.
/// </para>
/// </remarks>
public sealed class SmsTemplates
{
    /// <summary>The OTP body's key in the resource.</summary>
    public const string Otp = "otp";

    private const string ResourceName = "MageRide.Iam.Resources.sms-templates.json";
    private const string Fallback = "en";

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _templates;

    public SmsTemplates()
        : this(ReadEmbedded())
    {
    }

    internal SmsTemplates(string json)
    {
        using var document = JsonDocument.Parse(json);

        var templates = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var template in document.RootElement.GetProperty("templates").EnumerateObject())
        {
            var byLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in template.Value.EnumerateObject())
            {
                byLanguage[language.Name] = language.Value.GetString() ?? string.Empty;
            }

            if (!byLanguage.ContainsKey(Fallback))
            {
                throw new InvalidOperationException(
                    $"SMS template '{template.Name}' has no '{Fallback}' body, so a user whose language is " +
                    "neither Sinhala nor Tamil could not be sent one.");
            }

            templates[template.Name] = byLanguage;
        }

        _templates = templates;
    }

    /// <summary>
    /// The body for a template and language, with <c>{code}</c> and <c>{minutes}</c> filled in.
    /// </summary>
    /// <param name="language"><c>si</c> | <c>ta</c> | <c>en</c>. Anything else falls back to English
    /// rather than failing — a code that arrives in the wrong language beats one that never
    /// arrives.</param>
    public string Render(string template, string? language, string code, int minutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        if (!_templates.TryGetValue(template, out var byLanguage))
        {
            throw new KeyNotFoundException($"No SMS template named '{template}'.");
        }

        if (string.IsNullOrWhiteSpace(language) || !byLanguage.TryGetValue(language, out var body))
        {
            body = byLanguage[Fallback];
        }

        return body
            .Replace("{code}", code, StringComparison.Ordinal)
            .Replace("{minutes}", minutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{ResourceName}' is missing. Check the EmbeddedResource item in Iam.Api.csproj.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

using System.Text;
using MageRide.Notification.Domain;

namespace MageRide.Notification.Templates;

/// <summary>A template that could not be turned into a message.</summary>
/// <remarks>
/// Distinct from a transport failure so the delivery worker can tell them apart: a gateway that
/// refused is worth another attempt, and a template missing a value will be missing it again in
/// five seconds. The second is <c>Failed</c> immediately.
/// </remarks>
public sealed class TemplateRenderException : Exception
{
    public TemplateRenderException(string message)
        : base(message)
    {
    }

    public TemplateRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TemplateRenderException()
    {
    }
}

/// <summary>A template resolved into one language, ready to render.</summary>
/// <param name="Language">
/// What was actually served, which is not always what was asked for — content-svc falls back and
/// says so, and the answer is stored on the row so a support question about "why was this in
/// English" has an answer.
/// </param>
public sealed record ResolvedTemplate(
    string Key, string Language, int Version, string? Title, string Body, IReadOnlyList<string> Placeholders);

/// <summary>A rendered message.</summary>
public sealed record RenderedMessage(string? Title, string Body, string Language, int Version);

/// <summary>
/// Substitutes <c>{{name}}</c> placeholders (D-26; the style <c>content.notification_templates</c>
/// declares in its own column comment).
/// </summary>
/// <remarks>
/// <para>
/// <b>A missing value is a failure, not a blank.</b> Most of D6' I-29.2's SMS templates carry
/// <c>{{link}}</c> — the package-tracking link, the proxy-ride link, the five-minute pickup-confirm
/// link — and the recipient of every one of them is somebody with no app to find another way in. A
/// renderer that shipped "Track it here: " would send a message that is worse than none, and it
/// would do it silently. content-svc already refuses to *publish* a set of languages whose
/// placeholders disagree; this is the same rule one step later, against the values.
/// </para>
/// <para>
/// <b>An unknown value is ignored, not appended.</b> The payload is also the FCM <c>data</c> map
/// (deep links, request ids, ride ids), so most of it is for the app rather than for the sentence.
/// </para>
/// <para>
/// The scan is a single pass over the body. No regular expression: this runs once per recipient per
/// ride offer, which is the hottest cold path the platform has (content-svc's own note), and an
/// unmatched <c>{{</c> in a translation must be emitted verbatim rather than throwing.
/// </para>
/// </remarks>
public static class TemplateRenderer
{
    private const string Open = "{{";
    private const string Close = "}}";

    public static RenderedMessage Render(ResolvedTemplate template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var title = template.Title is null ? null : Substitute(template.Key, template.Title, values);
        var body = Substitute(template.Key, template.Body, values);

        return new RenderedMessage(title, body, template.Language, template.Version);
    }

    /// <summary>The placeholder names a body interpolates, in order of first appearance.</summary>
    public static IReadOnlyList<string> PlaceholdersOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var names = new List<string>();

        foreach (var name in Scan(text))
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string Substitute(string key, string text, IReadOnlyDictionary<string, string> values)
    {
        if (!text.Contains(Open, StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 32);
        var index = 0;

        while (index < text.Length)
        {
            var start = text.IndexOf(Open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                // An unclosed brace pair is part of the text, not a broken placeholder. Emitting it
                // verbatim is what a translator who typed one would expect; throwing would take a
                // whole notification type down over a typo in one language.
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);

            var name = text[(start + Open.Length)..end].Trim();

            if (!values.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
            {
                throw new TemplateRenderException(
                    $"Template '{key}' interpolates {{{{{name}}}}} and the payload carries no value for it. " +
                    "The message is not sent — a body with a hole in it is worse than none (D-26).");
            }

            builder.Append(value);
            index = end + Close.Length;
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Scan(string text)
    {
        var index = 0;

        while (index < text.Length)
        {
            var start = text.IndexOf(Open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var end = text.IndexOf(Close, start + Open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            yield return text[(start + Open.Length)..end].Trim();
            index = end + Close.Length;
        }
    }
}

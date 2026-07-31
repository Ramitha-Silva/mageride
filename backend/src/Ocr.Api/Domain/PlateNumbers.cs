using System.Text;
using System.Text.RegularExpressions;

namespace MageRide.Ocr.Domain;

/// <summary>
/// Reads a registration number off a plate, and decides whether it is the one the driver entered
/// (D5' §14.1a's photos row, <c>reg_no_match</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The comparison lives here on purpose.</b> C029's seam hands this service the expected plate
/// rather than asking it to return only what it read, because the verdict is one decision and
/// splitting it across two services would let them disagree about whether <c>wp qa 1234</c> is
/// <c>WP-QA-1234</c>.
/// </para>
/// <para>
/// <b>Comparison is on the alphanumerics alone; presentation keeps the hyphens.</b> Separators are
/// a writing convention — the same plate is painted <c>WP QA-1234</c>, written <c>WP-QA-1234</c>
/// and read by an OCR engine as <c>WPQA1234</c> when the gap falls below its word threshold. None
/// of that is identity, so none of it may cause a mismatch. What is <em>not</em> forgiven is a
/// different character: no <c>O</c>↔<c>0</c> or <c>I</c>↔<c>1</c> folding, because a photograph of
/// a different vehicle's plate is the one thing step 4/4 exists to rule out, and a fuzzy match here
/// would approve exactly that. Over-strictness costs an officer one glance; under-strictness puts
/// an unverified vehicle on the road.
/// </para>
/// </remarks>
public static class PlateNumbers
{
    /// <summary>Same ceiling as <c>registry.yaml#/components/schemas/VehicleRegistration</c>.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// A Sri Lankan plate in canonical form: an optional province prefix, a letter or digit group,
    /// and the four-digit serial.
    /// </summary>
    private static readonly Regex Candidate = new(
        @"^(?:[A-Z]{2,3})?(?:[A-Z]{2,3}|[0-9]{2,3})[0-9]{4}$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250));

    /// <summary>How many OCR words a plate may be split across (<c>WP</c> · <c>QA</c> · <c>1234</c>).</summary>
    private const int MaxTokens = 3;

    /// <summary>Longest run of letters or of digits a single plate token can be.</summary>
    /// <remarks>
    /// This is what stops the recogniser reading <c>EXPIRY 2029</c> as a plate: split into groups it
    /// is <c>EXP</c>·<c>IRY</c>·<c>2029</c>, which is exactly the canonical shape above. A plate's
    /// letter groups are separated on the plate itself, so a <em>single</em> token of six letters is
    /// a word, not a registration — and a matcher that could not tell them apart would put
    /// <c>plate_text: EXPIRY-2029</c> in front of a Verification Officer.
    /// </remarks>
    private const int MaxLettersPerToken = 3;

    private const int MaxDigitsPerToken = 4;

    /// <summary>The identity of a plate: its alphanumerics, upper-cased. Never shown to anybody.</summary>
    public static string Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether two plates name the same registration.</summary>
    /// <remarks>
    /// Two empty plates are <b>not</b> a match. An unreadable photograph and an unregistered vehicle
    /// would otherwise agree with each other and verify the step.
    /// </remarks>
    public static bool Match(string? read, string? expected)
    {
        var left = Canonical(read);
        var right = Canonical(expected);

        return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    /// Picks the plate out of a page of OCR text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tokens, not a regex over the raw text.</b> A plate is written with separators — and an OCR
    /// engine reproduces them as anything from a space to a doubled em dash (<c>WP—-QA-—1234</c> is
    /// a real read of the fixture in this repository). Splitting on everything that is not
    /// alphanumeric makes the separator irrelevant, and keeping the token boundaries is what lets
    /// <see cref="MaxLettersPerToken"/> tell a registration from a word of the same shape.
    /// </para>
    /// <para>
    /// A vehicle photograph carries the plate and not much else, but it also carries the
    /// manufacturer's badge, a tax disc and whatever is behind the vehicle. Candidates are scored
    /// rather than taken first-found: the longest plate-shaped run wins, and ties go to the earliest,
    /// which on a cropped rear photograph is the plate.
    /// </para>
    /// </remarks>
    public static string? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? best = null;

        foreach (var line in text.ToUpperInvariant().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = Tokenise(line);

            for (var start = 0; start < tokens.Length; start++)
            {
                for (var length = 1; length <= MaxTokens && start + length <= tokens.Length; length++)
                {
                    var run = tokens.AsSpan(start, length);

                    if (!IsPlateShaped(run))
                    {
                        continue;
                    }

                    var candidate = string.Join("-", run.ToArray());

                    if (best is null || Canonical(candidate).Length > Canonical(best).Length)
                    {
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    private static bool IsPlateShaped(ReadOnlySpan<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (!IsPlateToken(token))
            {
                return false;
            }
        }

        return Candidate.IsMatch(string.Concat(tokens.ToArray()));
    }

    /// <summary>
    /// Whether one OCR word can be part of a plate.
    /// </summary>
    /// <remarks>
    /// Three shapes, and the first is the one that matters: a token of nothing but letters is a
    /// plate <em>group</em> and is never longer than three, which is what rules out
    /// <c>EXPIRY 2029</c> — six letters and four digits, exactly the canonical plate shape once the
    /// token boundaries are thrown away. A mixed token is a plate the engine failed to split
    /// (<c>WPQA1234</c>) and may carry the full four-to-six letters, because the digits following
    /// them are what make it a registration rather than a word.
    /// </remarks>
    private static bool IsPlateToken(string token)
    {
        var letters = token.TakeWhile(char.IsAsciiLetter).Count();

        if (letters == token.Length)
        {
            return letters <= MaxLettersPerToken;
        }

        if (letters == 0)
        {
            return token.Length <= MaxDigitsPerToken && token.All(char.IsAsciiDigit);
        }

        return letters is >= 2 and <= MaxLettersPerToken * 2
            && token.Length - letters is >= 2 and <= MaxDigitsPerToken
            && token.Skip(letters).All(char.IsAsciiDigit);
    }

    /// <summary>Splits a line into runs of letters and digits, whatever separated them.</summary>
    private static string[] Tokenise(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var character in line)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    /// <summary>
    /// The readable form — upper case, separators collapsed to single hyphens. This is what goes on
    /// the wire as <c>plate_text</c> and in front of a Verification Officer, so it matches how
    /// registry-svc canonicalises what the driver typed and the two can be read side by side.
    /// </summary>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSeparator = false;

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingSeparator = false;
                builder.Append(char.ToUpperInvariant(character));
                continue;
            }

            // Unlike registry-svc's normaliser, an unexpected character is a separator rather than a
            // refusal. That one is validating what a person typed; this one is cleaning up what a
            // camera read, where a speck of dirt between two groups is routine.
            pendingSeparator = true;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}

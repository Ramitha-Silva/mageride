using System.Text.RegularExpressions;

namespace MageRide.Ocr.Domain;

/// <summary>
/// The identity numbers D-36 masks out of an image before it leaves the perimeter, and the shapes
/// that recognise them.
/// </summary>
/// <remarks>
/// <para>
/// ADD §12.5 is specific about the mechanism: <em>"Tesseract is run to obtain bounding boxes for the
/// <b>regex-detected</b> ID number on the document (NIC / driving licence number). The pixels in
/// those boxes are blacked out."</em> These are those regexes.
/// </para>
/// <para>
/// <b>They are deliberately over-eager.</b> A false positive costs a blacked-out rectangle on a
/// document the model reads around; a false negative puts somebody's NIC in a third-party model's
/// request log. The two are not comparable, so anything NIC-shaped or licence-shaped is masked —
/// including the value in a field the extraction then has to supply from Gemini's structured answer
/// rather than from the pixels (which is exactly what I-25.1 says happens: "the NIC number is still
/// masked before the image leaves the perimeter; the value is captured from the structured
/// response").
/// </para>
/// </remarks>
public static class IdentifierPatterns
{
    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Sri Lankan NIC — the pre-2016 nine digits with a <c>V</c> or <c>X</c> suffix, and the current
    /// twelve digits.
    /// </summary>
    /// <remarks>
    /// The twelve-digit form is anchored on word boundaries and nothing else, so a long serial on an
    /// insurance certificate can match it. That is the intended trade: see the class remarks.
    /// </remarks>
    public static readonly Regex NationalIdentityCard =
        new(@"\b(?:\d{9}\s?[VX]|\d{12})\b", Options, Budget);

    /// <summary>
    /// Sri Lankan driving-licence number — a letter and seven digits (<c>B1234567</c>), or the eight
    /// digits it is sometimes printed as.
    /// </summary>
    public static readonly Regex DrivingLicence =
        new(@"\b(?:[A-Z]\s?\d{7}|\d{8})\b", Options, Budget);

    /// <summary>Every pattern the redaction pass masks, in the order it applies them.</summary>
    public static readonly IReadOnlyList<Regex> Masked = [NationalIdentityCard, DrivingLicence];

    /// <summary>Whether a word this service is about to send is an identity number.</summary>
    public static bool IsIdentifier(string? word) =>
        !string.IsNullOrWhiteSpace(word) && Masked.Any(pattern => pattern.IsMatch(word));

    /// <summary>The first NIC in a block of text, normalised to upper case with no inner space.</summary>
    public static string? FindNic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = NationalIdentityCard.Match(text);

        return match.Success ? match.Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() : null;
    }

    /// <summary>The first licence number in a block of text, normalised the same way.</summary>
    public static string? FindLicenceNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var match in DrivingLicence.Matches(text).Cast<Match>())
        {
            var value = match.Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

            // A bare eight-digit run is also the front half of a twelve-digit NIC, and the licence
            // number is not the NIC. Preferring the lettered form keeps the two apart on a document
            // that carries both, which a Sri Lankan licence always does.
            if (char.IsAsciiLetter(value[0]))
            {
                return value;
            }
        }

        var fallback = DrivingLicence.Match(text);

        return fallback.Success
            ? fallback.Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant()
            : null;
    }
}

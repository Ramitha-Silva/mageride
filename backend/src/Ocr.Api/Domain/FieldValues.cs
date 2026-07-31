using System.Globalization;
using System.Text.RegularExpressions;

namespace MageRide.Ocr.Domain;

/// <summary>
/// Normalises what an engine read into the value <c>registry.document_fields</c> stores.
/// </summary>
/// <remarks>
/// One place, because both engines feed it and E-03 reads the result: an expiry that came back as
/// <c>30/04/2029</c> from one path and <c>2029-04-30</c> from the other would give the nightly
/// document sweep two formats to parse, and the one it cannot parse is a certificate that never
/// expires.
/// </remarks>
public static class FieldValues
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>The formats a Sri Lankan document prints an expiry in, most specific first.</summary>
    /// <remarks>
    /// <b>Day-first, not month-first.</b> Sri Lanka writes <c>30.04.2029</c>; reading it as a month
    /// would turn a valid certificate into an invalid date, and — worse, on the days where both
    /// parse — silently move an expiry by up to eleven months. There is no month-first entry at all
    /// rather than one placed after, because "whichever parses" is how <c>03/04/2029</c> becomes
    /// either April or March depending on nothing.
    /// </remarks>
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd",
        "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy",
        "d-M-yyyy", "d/M/yyyy", "d.M.yyyy",
        "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy",
        "MMM dd, yyyy", "MMMM dd, yyyy",
    ];

    private static readonly Regex DateLike = new(
        @"\b(\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.\s]\d{1,2}[-/.\s]\d{4}|\d{1,2}\s+[A-Za-z]{3,9}\.?\s+\d{4})\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        Budget);

    /// <summary>
    /// Parses a date out of <paramref name="text"/> and returns it as ISO <c>yyyy-MM-dd</c>, or
    /// <see langword="null"/> when there is no date in it.
    /// </summary>
    public static string? NormaliseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidate = text.Trim();

        if (TryParse(candidate, out var parsed))
        {
            return parsed;
        }

        // The engine may have handed back a whole line ("Date of Expiry : 30.04.2029"). Pull the
        // date-shaped run out of it rather than refusing the field.
        var match = DateLike.Match(candidate);

        return match.Success && TryParse(match.Value, out var found) ? found : null;
    }

    private static bool TryParse(string candidate, out string? iso)
    {
        var cleaned = candidate.Replace('–', '-').Replace('—', '-').Trim(' ', ':', ',', '.');

        if (DateTime.TryParseExact(
                cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            iso = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        iso = null;
        return false;
    }

    /// <summary>
    /// The vehicle classes a licence permits, as the comma-separated list AL-29 stores.
    /// </summary>
    /// <remarks>
    /// <b>Sri Lankan licence classes are not MageRide vehicle types</b> — the reverse of a licence
    /// carries <c>A1</c>, <c>B</c>, <c>C1</c>, <c>G1</c> and the rest, and <c>registry.vehicles</c>
    /// speaks <c>three_wheeler</c>/<c>sedan</c>. Mapping between them is a rule no spec in this
    /// build states, so the classes are stored verbatim and the mapping is left to whoever writes
    /// the screen that needs it (raised in the C054 handoff). What normalisation does here is
    /// spacing and separators, so <c>A1 , B ,C1</c> and <c>A1/B/C1</c> compare equal.
    /// </remarks>
    public static string? NormaliseVehicleClasses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var classes = text
            .Split([',', '/', ';', '|', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim('.').ToUpperInvariant())
            .Where(part => part.Length is > 0 and <= 4 && part.All(char.IsAsciiLetterOrDigit))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return classes.Length == 0 ? null : string.Join(",", classes);
    }

    /// <summary>Trims and collapses whitespace on a free-text value, or returns null for an empty one.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>
    /// Applies the normalisation a field key implies. The one place a value is shaped, whichever
    /// engine produced it.
    /// </summary>
    public static string? Normalise(string key, string? value) => key switch
    {
        _ when DocumentFieldKeys.IsDate(key) => NormaliseDate(value),
        DocumentFieldKeys.AllowedVehicleTypes => NormaliseVehicleClasses(value),
        DocumentFieldKeys.NicNo => IdentifierPatterns.FindNic(value) ?? Clean(value),
        DocumentFieldKeys.LicenceNo => IdentifierPatterns.FindLicenceNumber(value) ?? Clean(value),
        DocumentFieldKeys.PlateText => PlateNumbers.Normalise(value),
        _ => Clean(value),
    };
}

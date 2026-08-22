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
    /// The classes a Sri Lankan driving licence prints, and the only tokens
    /// <see cref="NormaliseVehicleClasses"/> keeps (Δ MCS-17).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same list the reverse-side prompt shows the model</b>, shared rather than written
    /// twice on purpose: two copies of an alphabet drift, and the copy that drifts is whichever one
    /// nothing reads back.
    /// </para>
    /// <para>
    /// An ORDERED array rather than a set, because it is interpolated into that prompt and a hash
    /// set's enumeration order is not contractual. A prompt whose wording varies between processes
    /// is a model that can answer differently for the same document, which is the one thing a
    /// zero-temperature transcription is configured to avoid.
    /// </para>
    /// <para>
    /// <b>Sri Lankan licence classes are not MageRide vehicle types.</b> The reverse of a licence
    /// carries <c>A1</c>, <c>B</c>, <c>C1</c>, <c>G1</c> and the rest; <c>registry.vehicles</c>
    /// speaks <c>three_wheeler</c>/<c>sedan</c>. No spec in this build maps one to the other, so the
    /// classes are stored verbatim — inventing a mapping would put an unstated rule between a
    /// driver's licence and what they may drive (C054 handoff).
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> LicenceClasses =
    [
        "A1", "A", "B1", "B", "C1", "C", "CE", "D1", "D", "DE", "G1", "G", "J",
    ];

    /// <summary>
    /// The licence classes in <paramref name="text"/> as the comma-separated list AL-29 stores, or
    /// <see langword="null"/> when it holds none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Normalisation is spacing and separators, so <c>A1 , B ,C1</c> and <c>A1/B/C1</c> compare
    /// equal.
    /// </para>
    /// <para>
    /// <b>Δ MCS-17 — filtered against <see cref="LicenceClasses"/>, not against a shape.</b> It used
    /// to keep any ASCII-alphanumeric token of four characters or fewer, which describes a licence
    /// class and also most short English words. A model answering <c>"Class B and G1 only"</c> — a
    /// reasonable thing for it to say — normalised to <c>B,AND,G1,ONLY</c> and was written to
    /// <c>docs.extractions</c> as an auto-verified reading. registry-svc's MCS-11 clause keeps that
    /// out of the driver's own column, but the officer is still shown prose to confirm.
    /// </para>
    /// <para>
    /// A token that is not a class is DROPPED rather than failing the field: a licence prints its
    /// classes in a table with headings, and refusing the whole value because a heading came along
    /// would lose the classes that were read correctly.
    /// </para>
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
            .Where(part => LicenceClasses.Contains(part))
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

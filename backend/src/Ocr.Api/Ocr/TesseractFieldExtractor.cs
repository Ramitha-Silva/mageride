using MageRide.Ocr.Configuration;
using MageRide.Ocr.Domain;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Ocr;

/// <summary>
/// D6' §7.5's fallback path and §8.3's "OCR down → Tesseract": fields read on-prem, from the page
/// the redaction pass already produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field this produces is capped below the auto-verify threshold.</b> Not because Tesseract
/// is untrustworthy — its per-word confidences are honest — but because this path has no
/// document-layout model behind it: it finds a date near a label, and "near a label" is a heuristic
/// that cannot tell an insurance certificate's expiry from the date it was issued on a form that
/// prints them adjacently. AL-27 auto-approves a vehicle with no human involvement at all on the
/// strength of these fields, and that is not a decision to make on a keyword match. The result is
/// D6' §7.5's own sentence — "below threshold → manual admin review" — expressed as a ceiling
/// rather than as a hope that the numbers come out low.
/// </para>
/// <para>
/// <b>It reads the raw page, not the redacted one</b>, and that is within D-36: the pre-pass governs
/// what leaves the perimeter, and this engine is the on-prem one. It is also the only reason the
/// NIC can be returned at all on this path — the redacted image has a black rectangle where it was.
/// </para>
/// </remarks>
public sealed class TesseractFieldExtractor
{
    private readonly OcrOptions _options;

    public TesseractFieldExtractor(IOptions<OcrOptions> options) =>
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Reads every field <paramref name="kind"/> and <paramref name="side"/> can carry.</summary>
    public IReadOnlyList<ExtractedField> Extract(OcrPage page, string kind, string? side)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!page.Succeeded || page.Words.Count == 0)
        {
            return [];
        }

        var lines = Lines(page);
        var text = page.Text;
        var fields = new List<ExtractedField>();

        foreach (var key in DocumentFieldKeys.AcceptedFor(kind, DocumentSides.Normalise(side)))
        {
            // The comparison is the platform's, not an engine's (D5' §14.1a).
            if (key == DocumentFieldKeys.RegNoMatch)
            {
                continue;
            }

            var (value, confidence) = Read(key, lines, text);

            if (value is not null)
            {
                fields.Add(new ExtractedField(key, value, Cap(confidence)));
            }
        }

        return fields;
    }

    private (string? Value, decimal Confidence) Read(
        string key, IReadOnlyList<OcrLine> lines, string text) => key switch
    {
        DocumentFieldKeys.LicenceNo =>
            Found(IdentifierPatterns.FindLicenceNumber(text), lines, DocumentFieldKeys.LicenceNo),
        DocumentFieldKeys.NicNo =>
            Found(IdentifierPatterns.FindNic(text), lines, DocumentFieldKeys.NicNo),
        DocumentFieldKeys.PlateText =>
            Found(PlateNumbers.Read(text), lines, DocumentFieldKeys.PlateText),

        DocumentFieldKeys.LicenceExpiry => Date(lines, ExpiryLabels),
        DocumentFieldKeys.InsuranceExpiry => Date(lines, ExpiryLabels),
        DocumentFieldKeys.RevenueExpiry => Date(lines, ExpiryLabels),
        DocumentFieldKeys.PermitExpiry => Date(lines, ExpiryLabels),

        DocumentFieldKeys.InsurancePolicyNo => Labelled(lines, ["policy", "cover note", "certificate no"]),
        DocumentFieldKeys.Insurer => Labelled(lines, ["insurer", "insurance company", "underwritten"]),
        DocumentFieldKeys.RevenueNo => Labelled(lines, ["licence no", "license no", "revenue licence", "serial"]),
        DocumentFieldKeys.PermitNo => Labelled(lines, ["permit no", "permit number", "permit"]),
        DocumentFieldKeys.PermitRoute => Labelled(lines, ["route", "route no"]),

        DocumentFieldKeys.AllowedVehicleTypes => Classes(lines),

        _ => (null, 0m),
    };

    private static readonly string[] ExpiryLabels =
        ["expiry", "expires", "expire", "valid to", "valid till", "valid until", "date of expiry", "4b"];

    /// <summary>
    /// The date nearest an expiry label, or — failing that — the latest date on the page.
    /// </summary>
    /// <remarks>
    /// The fallback is deliberate and is why the ceiling above exists. A document that prints an
    /// issue date and an expiry date and labels neither in a way this recognises still yields the
    /// later of the two, which is right far more often than it is wrong; a Verification Officer
    /// confirms it either way, and returning nothing would have made the same officer type it.
    /// </remarks>
    private static (string? Value, decimal Confidence) Date(IReadOnlyList<OcrLine> lines, string[] labels)
    {
        foreach (var line in lines)
        {
            if (!labels.Any(label => line.Lowered.Contains(label, StringComparison.Ordinal)))
            {
                continue;
            }

            if (FieldValues.NormaliseDate(line.Text) is { } onTheLine)
            {
                return (onTheLine, line.Confidence);
            }

            // Forms routinely print the label on one line and the value on the next.
            var next = lines.SkipWhile(candidate => candidate != line).Skip(1).FirstOrDefault();

            if (next is not null && FieldValues.NormaliseDate(next.Text) is { } below)
            {
                return (below, Math.Min(line.Confidence, next.Confidence));
            }
        }

        var latest = lines
            .Select(line => (Line: line, Date: FieldValues.NormaliseDate(line.Text)))
            .Where(candidate => candidate.Date is not null)
            .OrderByDescending(candidate => candidate.Date, StringComparer.Ordinal)
            .FirstOrDefault();

        return latest.Date is null ? (null, 0m) : (latest.Date, latest.Line.Confidence);
    }

    /// <summary>The text following a label on its own line, or on the line under it.</summary>
    /// <remarks>
    /// <b>The continuation line has its own label stripped too.</b> A revenue licence prints
    /// "REVENUE LICENCE" as a heading and "LICENCE NO RL8891234" underneath; the heading matches
    /// first, leaves nothing after it, and a naive fall-through would return the whole next line —
    /// <c>revenue_no = "LICENCE NO RL8891234"</c>, which is what a Verification Officer then has to
    /// retype. Found by <c>An_expiry_is_read_off_its_label_and_normalised</c>.
    /// </remarks>
    private static (string? Value, decimal Confidence) Labelled(IReadOnlyList<OcrLine> lines, string[] labels)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (!labels.Any(candidate => line.Lowered.Contains(candidate, StringComparison.Ordinal)))
            {
                continue;
            }

            if (AfterLabel(line, labels) is { Length: > 1 } tail)
            {
                return (tail, line.Confidence);
            }

            if (index + 1 < lines.Count
                && AfterLabel(lines[index + 1], labels) is { Length: > 1 } below)
            {
                return (below, Math.Min(line.Confidence, lines[index + 1].Confidence));
            }
        }

        return (null, 0m);
    }

    /// <summary>What is left of a line once the longest label on it has been removed.</summary>
    /// <remarks>
    /// Longest, not first: "licence no" and "revenue licence" both match "REVENUE LICENCE NO 88", and
    /// stripping the shorter one leaves "REVENUE" in front of the value.
    /// </remarks>
    private static string? AfterLabel(OcrLine line, string[] labels)
    {
        var label = labels
            .Where(candidate => line.Lowered.Contains(candidate, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();

        if (label is null)
        {
            return FieldValues.Clean(line.Text);
        }

        var after = line.Lowered.IndexOf(label, StringComparison.Ordinal) + label.Length;

        return FieldValues.Clean(line.Text[after..].TrimStart(' ', ':', '.', '-', '#'));
    }

    /// <summary>The licence classes on the reverse of a licence (AL-29).</summary>
    /// <remarks>
    /// The reverse is a table of class codes beside dates, so the classes are the short
    /// alphanumeric tokens; <see cref="FieldValues.NormaliseVehicleClasses"/> is what filters the
    /// column headings and the dates back out.
    /// </remarks>
    private static (string? Value, decimal Confidence) Classes(IReadOnlyList<OcrLine> lines)
    {
        var candidates = lines
            .SelectMany(line => line.Words.Select(word => (line, word)))
            .Where(pair => pair.word.Text.Length is > 0 and <= 3
                && pair.word.Text.All(char.IsAsciiLetterOrDigit)
                && char.IsAsciiLetterUpper(pair.word.Text[0]))
            .ToArray();

        if (candidates.Length == 0)
        {
            return (null, 0m);
        }

        var classes = FieldValues.NormaliseVehicleClasses(
            string.Join(",", candidates.Select(pair => pair.word.Text)));

        return classes is null
            ? (null, 0m)
            : (classes, candidates.Min(pair => pair.word.Confidence));
    }

    /// <summary>
    /// Attaches a confidence to a value found by regex over the whole page — the lowest confidence
    /// of the words the value's characters came from.
    /// </summary>
    private static (string? Value, decimal Confidence) Found(
        string? value, IReadOnlyList<OcrLine> lines, string key)
    {
        if (value is null)
        {
            return (null, 0m);
        }

        var normalised = FieldValues.Normalise(key, value);

        if (normalised is null)
        {
            return (null, 0m);
        }

        var comparable = Compact(normalised);

        var contributing = lines
            .SelectMany(line => line.Words)
            .Where(word => comparable.Contains(Compact(word.Text), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return (normalised, contributing.Length == 0 ? 0.5m : contributing.Min(word => word.Confidence));
    }

    private static string Compact(string value) =>
        new([.. value.Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>The ceiling that makes every field on this path land in the officer queue.</summary>
    private decimal Cap(decimal confidence) =>
        Math.Clamp(Math.Min(confidence, _options.TesseractConfidenceCeiling), 0m, 1m);

    /// <summary>One line of the page — its words, its text and the confidence of its worst word.</summary>
    private sealed record OcrLine(IReadOnlyList<OcrWord> Words)
    {
        public string Text { get; } = string.Join(' ', Words.Select(word => word.Text));

        public string Lowered => Text.ToLowerInvariant();

        public decimal Confidence { get; } = Words.Min(word => word.Confidence);
    }

    /// <summary>
    /// Groups words into lines by vertical overlap.
    /// </summary>
    /// <remarks>
    /// Not by <c>top / height</c> as <see cref="OcrPage.Text"/> does — that is good enough for a
    /// regex over the whole page, but a label and its value routinely differ in font size, and a
    /// bucketed line puts them in different lines exactly when reading them together matters.
    /// </remarks>
    private static IReadOnlyList<OcrLine> Lines(OcrPage page)
    {
        var lines = new List<List<OcrWord>>();

        foreach (var word in page.Words.OrderBy(word => word.Top).ThenBy(word => word.Left))
        {
            var line = lines.FirstOrDefault(candidate =>
            {
                var first = candidate[0];
                var overlap = Math.Min(first.Bottom, word.Bottom) - Math.Max(first.Top, word.Top);

                return overlap * 2 > Math.Min(first.Height, word.Height);
            });

            if (line is null)
            {
                lines.Add([word]);
            }
            else
            {
                line.Add(word);
            }
        }

        return [.. lines.Select(line => new OcrLine([.. line.OrderBy(word => word.Left)]))];
    }
}

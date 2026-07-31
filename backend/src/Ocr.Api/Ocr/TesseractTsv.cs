using System.Globalization;

namespace MageRide.Ocr.Ocr;

/// <summary>
/// Parses <c>tesseract … tsv</c> output into words with boxes.
/// </summary>
/// <remarks>
/// <para>
/// The TSV writer is Tesseract's own stable interface and the reason this service shells out to the
/// binary rather than binding its C API: the columns below have not changed across 3.x, 4.x and
/// 5.x, whereas every managed wrapper needs the same <c>libtesseract</c>/<c>liblept</c> pair
/// installed <em>and</em> a matching build of itself.
/// </para>
/// <para>
/// Format: a header row, then one row per node at five levels — page, block, paragraph, line and
/// word. <b>Only level 5 rows carry text</b>; the rest exist to describe the layout and carry
/// <c>conf = -1</c>. A parser that skipped the level check would emit four empty "words" per real
/// one, each with the enclosing box, and the redaction pass would black out whole paragraphs.
/// </para>
/// </remarks>
internal static class TesseractTsv
{
    private const int LevelColumn = 0;
    private const int LeftColumn = 6;
    private const int TopColumn = 7;
    private const int WidthColumn = 8;
    private const int HeightColumn = 9;
    private const int ConfidenceColumn = 10;
    private const int TextColumn = 11;

    /// <summary>The level Tesseract gives a word. Everything above it is layout.</summary>
    private const int WordLevel = 5;

    private const int Columns = 12;

    /// <summary>Parses the whole document. Malformed rows are skipped, not thrown on.</summary>
    /// <remarks>
    /// A row this cannot read is one word missing from a page of them; throwing would lose the page
    /// — and with it the redaction boxes — because one line came back odd.
    /// </remarks>
    public static IReadOnlyList<OcrWord> Parse(string? tsv)
    {
        if (string.IsNullOrWhiteSpace(tsv))
        {
            return [];
        }

        var words = new List<OcrWord>();

        foreach (var line in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var row = line.TrimEnd('\r').Split('\t');

            if (row.Length < Columns
                || !int.TryParse(row[LevelColumn], CultureInfo.InvariantCulture, out var level)
                || level != WordLevel)
            {
                continue;
            }

            var text = row[TextColumn].Trim();

            if (text.Length == 0
                || !int.TryParse(row[LeftColumn], CultureInfo.InvariantCulture, out var left)
                || !int.TryParse(row[TopColumn], CultureInfo.InvariantCulture, out var top)
                || !int.TryParse(row[WidthColumn], CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(row[HeightColumn], CultureInfo.InvariantCulture, out var height)
                || !decimal.TryParse(
                    row[ConfidenceColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
            {
                continue;
            }

            if (width <= 0 || height <= 0 || confidence < 0)
            {
                continue;
            }

            // Tesseract reports 0–100; everything else on this platform speaks 0–1.
            words.Add(new OcrWord(text, left, top, width, height, Math.Clamp(confidence / 100m, 0m, 1m)));
        }

        return words;
    }
}

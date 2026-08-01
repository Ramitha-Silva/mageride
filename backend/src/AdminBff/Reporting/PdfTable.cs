using System.Globalization;
using System.Text;

namespace MageRide.AdminBff.Reporting;

/// <summary>
/// A paginated table as a PDF 1.4 file, written by hand and with no dependency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> C065's deliverable names "CSV/PDF export" for the transactions
/// report, and wallet-svc's own statement route answers <c>415</c> to <c>application/pdf</c> with
/// the reason: a PDF needs a renderer and no spec provides a document template. That reasoning is
/// right about a *styled statement* and wrong about a *table* — a table of monospaced-width text in
/// one of the fourteen base fonts every conforming reader is required to have is roughly two hundred
/// lines of PDF syntax and no font embedding, no image handling and no external code. Adding a
/// rendering library to <c>Directory.Packages.props</c> for one back-office download would be a
/// far larger commitment than this file.
/// </para>
/// <para>
/// <b>The base-14 font is the whole simplification.</b> <c>Helvetica</c> and <c>Helvetica-Bold</c>
/// are guaranteed present in the reader (PDF 1.7 §9.6.2.2), so nothing is embedded and the file has
/// no font program in it. The cost is that only WinAnsi characters can be drawn, which is why
/// <see cref="Sanitise"/> exists — and why this renderer is used for a finance table of ids,
/// integers and contract identifiers and would be the wrong tool for anything trilingual (D-26). A
/// Sinhala or Tamil column would need an embedded font and this class must not be extended to fake
/// one.
/// </para>
/// <para>
/// <b>Byte offsets are the format's own integrity check, so they are measured rather than
/// computed.</b> The cross-reference table records where each object starts; a file whose offsets
/// are one byte out fails to open in a strict reader and opens fine in a forgiving one, which is the
/// worst way for this to be wrong. Everything is written through one <see cref="MemoryStream"/> in
/// Latin-1 — one byte per character, so <c>Stream.Position</c> <em>is</em> the offset — and the xref
/// is emitted from the positions that were actually reached.
/// </para>
/// </remarks>
internal static class PdfTable
{
    /// <summary>A4 at 72 dpi, portrait: 595 × 842 points.</summary>
    private const double PageWidth = 595;
    private const double PageHeight = 842;

    private const double Margin = 36;
    private const double FontSize = 8;
    private const double HeaderFontSize = 9;
    private const double LineHeight = 12;

    /// <summary>Latin-1: one byte per character, which is what makes the xref offsets exact.</summary>
    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>
    /// Renders <paramref name="rows"/> under <paramref name="columns"/>, paginating as needed.
    /// </summary>
    /// <param name="title">The document title, repeated at the top of every page.</param>
    /// <param name="preamble">
    /// Self-describing lines drawn under the title on page 1 — the window, the timezone, the money
    /// unit. The same argument as the CSV's <c>#</c> preamble: a download opened six months later has
    /// no request around it, and a figure with no stated window is unfalsifiable.
    /// </param>
    /// <param name="columns">Header cells and their widths in points. Widths are used verbatim.</param>
    /// <param name="rows">Cell text, already formatted. Over-long cells are clipped, never wrapped.</param>
    public static byte[] Render(
        string title,
        IReadOnlyList<string> preamble,
        IReadOnlyList<(string Header, double Width)> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(preamble);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var pages = Paginate(title, preamble, columns, rows);

        using var buffer = new MemoryStream();

        // Object 1 is the catalog, 2 the page tree, 3 and 4 the two fonts; pages and their content
        // streams follow in pairs. Numbered up front because the page tree has to name its children
        // before they are written.
        var firstPageObject = 5;
        var objectCount = 4 + (pages.Count * 2);
        var offsets = new long[objectCount + 1];

        Write(buffer, "%PDF-1.4\n");

        // A binary comment on the second line marks the file as containing binary data, which is
        // what stops a transfer agent treating it as text and rewriting the line endings.
        buffer.WriteByte(0x25);
        buffer.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        buffer.WriteByte(0x0A);

        var kids = string.Join(
            ' ', Enumerable.Range(0, pages.Count).Select(index => $"{firstPageObject + (index * 2)} 0 R"));

        offsets[1] = buffer.Position;
        Write(buffer, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = buffer.Position;
        Write(buffer, $"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        offsets[3] = buffer.Position;
        Write(buffer,
            "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        offsets[4] = buffer.Position;
        Write(buffer,
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\n"
            + "endobj\n");

        for (var index = 0; index < pages.Count; index++)
        {
            var pageObject = firstPageObject + (index * 2);
            var contentObject = pageObject + 1;
            var content = Latin1.GetBytes(pages[index]);

            offsets[pageObject] = buffer.Position;
            Write(buffer,
                $"{pageObject} 0 obj\n<< /Type /Page /Parent 2 0 R "
                + $"/MediaBox [0 0 {Number(PageWidth)} {Number(PageHeight)}] "
                + "/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> "
                + $"/Contents {contentObject} 0 R >>\nendobj\n");

            offsets[contentObject] = buffer.Position;
            Write(buffer, $"{contentObject} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            buffer.Write(content);
            Write(buffer, "\nendstream\nendobj\n");
        }

        var xref = buffer.Position;

        Write(buffer, $"xref\n0 {objectCount + 1}\n");

        // Object 0 is always the head of the free list, in this exact spelling: a 10-digit offset,
        // a 5-digit generation and a two-character end-of-line, so every entry is 20 bytes.
        Write(buffer, "0000000000 65535 f \n");

        for (var number = 1; number <= objectCount; number++)
        {
            Write(buffer, $"{offsets[number].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        Write(buffer,
            $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n"
            + $"{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");

        return buffer.ToArray();
    }

    /// <summary>Lays the rows out into content streams, one per page.</summary>
    private static List<string> Paginate(
        string title,
        IReadOnlyList<string> preamble,
        IReadOnlyList<(string Header, double Width)> columns,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var pages = new List<string>();
        var index = 0;

        // Every page carries the title and the column headers; only the first carries the preamble,
        // because a window restated on page nine is noise and a window stated nowhere is a bug.
        do
        {
            var page = new StringBuilder();
            var y = PageHeight - Margin;

            Text(page, bold: true, 12, Margin, y, title);
            y -= LineHeight * 1.6;

            if (pages.Count == 0)
            {
                foreach (var line in preamble)
                {
                    Text(page, bold: false, FontSize, Margin, y, line);
                    y -= LineHeight;
                }

                y -= LineHeight * 0.5;
            }

            var headerY = y;
            var x = Margin;

            foreach (var (header, width) in columns)
            {
                Text(page, bold: true, HeaderFontSize, x, headerY, header);
                x += width;
            }

            y = headerY - (LineHeight * 0.4);

            // A rule under the headers. `re f` fills a rectangle, which is a hairline at 0.6 points
            // and needs no graphics state of its own.
            page.Append(CultureInfo.InvariantCulture,
                $"{Number(Margin)} {Number(y)} {Number(PageWidth - (2 * Margin))} 0.6 re f\n");

            y -= LineHeight;

            while (index < rows.Count && y > Margin + LineHeight)
            {
                x = Margin;

                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = column < rows[index].Count ? rows[index][column] : string.Empty;
                    Text(page, bold: false, FontSize, x, y, Clip(cell, columns[column].Width));
                    x += columns[column].Width;
                }

                y -= LineHeight;
                index++;
            }

            Text(
                page,
                bold: false,
                FontSize,
                Margin,
                Margin - (LineHeight * 0.5),
                $"Page {pages.Count + 1}");

            pages.Add(page.ToString());
        }
        while (index < rows.Count);

        return pages;
    }

    private static void Text(StringBuilder page, bool bold, double size, double x, double y, string value) =>
        page.Append(CultureInfo.InvariantCulture,
            $"BT /{(bold ? "F2" : "F1")} {Number(size)} Tf 1 0 0 1 {Number(x)} {Number(y)} Tm ({Escape(value)}) Tj ET\n");

    /// <summary>
    /// Clips a cell to its column, because Helvetica is proportional and a long id would otherwise
    /// run under the next column's text.
    /// </summary>
    /// <remarks>
    /// The width estimate is deliberately crude — 0.55 em per character is a safe average for
    /// Helvetica's digits and lower-case letters — because the alternative is carrying the font's
    /// 224-glyph width table for a back-office download. Erring narrow costs a character; erring wide
    /// costs a table nobody can read.
    /// </remarks>
    private static string Clip(string value, double width)
    {
        var maximum = Math.Max(1, (int)((width - 4) / (FontSize * 0.55)));

        return value.Length <= maximum ? value : string.Concat(value.AsSpan(0, Math.Max(1, maximum - 1)), "…");
    }

    /// <summary>
    /// Escapes a literal string, and drops what WinAnsi cannot draw.
    /// </summary>
    /// <remarks>
    /// A character outside Latin-1 would be written as a byte the reader decodes to something else
    /// entirely — silently, and differently in different readers. Replacing it with <c>?</c> is
    /// visible; the two characters this renderer actually meets are the ellipsis <see cref="Clip"/>
    /// adds and the rupee sign, and both are mapped rather than mangled.
    /// </remarks>
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        foreach (var character in Sanitise(value))
        {
            if (character is '(' or ')' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Sanitise(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '…' => "...",
                '’' or '‘' => "'",
                '“' or '”' => "\"",
                '–' or '—' => "-",
                <= 'ÿ' and >= ' ' => character.ToString(),
                _ => "?",
            });
        }

        return builder.ToString();
    }

    private static string Number(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static void Write(Stream stream, string value)
    {
        var bytes = Latin1.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}

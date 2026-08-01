using System.Globalization;
using System.Text;
using MageRide.FleetBilling.Domain;

namespace MageRide.FleetBilling.Export;

/// <summary>
/// The consolidated invoice as a PDF, for the Fleet Portal's download (US-13.10, SCR-FP-010).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written here rather than taken as a dependency, and that is a real decision.</b> A PDF
/// renderer (QuestPDF, iText, a headless browser) is a large dependency, a licence question and — in
/// two of the three cases — a native binary in every container, for one document with a title, six
/// metadata lines and a table. What this produces is a PDF 1.4 file using the three base-14 fonts
/// every conforming reader is required to have built in, so nothing is embedded and nothing is
/// rasterised. wallet-svc (C046) took the other branch for the driver statement and answers
/// <c>415</c> with the reason; the difference is that C060's deliverable names PDF export outright.
/// </para>
/// <para>
/// <b>The cross-reference table is the part that has to be exact.</b> A PDF is read backwards — the
/// trailer points at <c>startxref</c>, which points at a table of byte offsets, one per object — so
/// every offset is recorded as the bytes are written rather than computed afterwards, and
/// <c>InvoicePdfTests</c> parses the file back and checks that each offset lands on its own
/// <c>N 0 obj</c> header. An offset that is one byte out produces a file that opens in some readers
/// and not others, which is the failure mode a "looks fine on my machine" check would miss.
/// </para>
/// <para>
/// <b>The table is set in Courier, on purpose.</b> Base-14 Courier is metrically exact — every glyph
/// is 600/1000 em — so the amount column can be right-aligned by arithmetic rather than by shipping
/// Helvetica's width tables, and an invoice whose numbers do not line up is one an accounts
/// department does not trust.
/// </para>
/// <para>
/// <b>Text outside WinAnsi becomes a question mark, and this file says so out loud.</b> The base-14
/// fonts cover Latin-1 and nothing else; a Sinhala or Tamil glyph needs an embedded font, which is
/// the dependency this class exists to avoid. Sri Lankan registration plates are Latin letters and
/// digits, so in practice only an organisation's *name* can be affected — and the name also appears
/// in the CSV, which is UTF-8. Named in the C060 handoff as the one thing a document renderer would
/// buy.
/// </para>
/// </remarks>
internal static class InvoicePdf
{
    // A4 in points, and a margin wide enough to survive a printer's unprintable edge.
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 42;
    private const double Leading = 13;
    private const double BodySize = 10;
    private const double TableSize = 9;

    /// <summary>Courier's fixed advance width, in em/1000. The whole reason the table is monospaced.</summary>
    private const double CourierAdvance = 0.6;

    private const int CatalogObject = 1;
    private const int PagesObject = 2;
    private const int HelveticaObject = 3;
    private const int HelveticaBoldObject = 4;
    private const int CourierObject = 5;
    private const int FirstPageObject = 6;

    /// <summary>Renders one invoice as a complete PDF 1.4 document.</summary>
    public static byte[] Render(FleetInvoiceDetail detail, string fleetName)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var contents = Paginate(detail, fleetName);

        // Object numbering: the five fixed objects, then one page and one content stream per page,
        // interleaved so a page and the stream it names are adjacent in the file.
        var pageObjects = new List<int>(contents.Count);

        for (var index = 0; index < contents.Count; index++)
        {
            pageObjects.Add(FirstPageObject + (index * 2));
        }

        var objects = new List<(int Number, string Body)>
        {
            (CatalogObject, $"<< /Type /Catalog /Pages {PagesObject} 0 R >>"),
            (PagesObject,
                $"<< /Type /Pages /Count {contents.Count} /Kids [{string.Join(' ', pageObjects.Select(n => $"{n} 0 R"))}] >>"),
            (HelveticaObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            (HelveticaBoldObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
            (CourierObject, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"),
        };

        for (var index = 0; index < contents.Count; index++)
        {
            var pageNumber = pageObjects[index];
            var streamNumber = pageNumber + 1;
            var stream = contents[index];

            objects.Add((pageNumber,
                $"<< /Type /Page /Parent {PagesObject} 0 R "
                + $"/MediaBox [0 0 {Number(PageWidth)} {Number(PageHeight)}] "
                + $"/Resources << /Font << /F1 {HelveticaObject} 0 R /F2 {HelveticaBoldObject} 0 R "
                + $"/F3 {CourierObject} 0 R >> >> /Contents {streamNumber} 0 R >>"));

            // Uncompressed. A FlateDecode filter would save a few kilobytes on a document this size
            // and would make the content unreadable to anything but a PDF parser — including the
            // test that asserts a vehicle's plate is actually on the page.
            objects.Add((streamNumber,
                $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}\nendstream"));
        }

        return Assemble(objects);
    }

    /// <summary>Writes the objects, the cross-reference table and the trailer.</summary>
    /// <remarks>
    /// Latin-1 throughout, which is what <c>WinAnsiEncoding</c> means on the wire; every string that
    /// reaches here has already been through <see cref="Escape"/>, so no byte above 0x7E survives
    /// unescaped.
    /// </remarks>
    private static byte[] Assemble(IReadOnlyList<(int Number, string Body)> objects)
    {
        using var buffer = new MemoryStream();

        void Write(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            buffer.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n");

        // A comment line of high bytes, which is what tells a transfer agent the file is binary.
        buffer.WriteByte((byte)'%');
        buffer.Write([0xE2, 0xE3, 0xCF, 0xD3], 0, 4);
        buffer.WriteByte((byte)'\n');

        // Object number → byte offset, recorded as each object starts. Never computed afterwards:
        // an offset derived from a second pass over the same strings is an offset that stops being
        // right the first time the encoding changes.
        var offsets = new SortedDictionary<int, long>();

        foreach (var (number, body) in objects.OrderBy(entry => entry.Number))
        {
            offsets[number] = buffer.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        var startXref = buffer.Position;
        var size = objects.Count + 1;

        Write($"xref\n0 {size}\n");

        // Object 0 is always the head of the free list, and every entry is exactly 20 bytes:
        // ten digits, a space, five digits, a space, one letter, then CRLF. A reader that seeks by
        // multiplication rather than by parsing depends on that being true to the byte.
        Write("0000000000 65535 f \n");

        for (var number = 1; number < size; number++)
        {
            var offset = offsets[number];
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        Write($"trailer\n<< /Size {size} /Root {CatalogObject} 0 R >>\nstartxref\n"
              + $"{startXref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");

        return buffer.ToArray();
    }

    /// <summary>Lays the invoice out, breaking to a new page when the table runs off this one.</summary>
    private static List<string> Paginate(FleetInvoiceDetail detail, string fleetName)
    {
        var invoice = detail.Invoice;
        var pages = new List<string>();
        var page = new StringBuilder();
        var y = PageHeight - Margin;

        void NewPage()
        {
            pages.Add(page.ToString());
            page = new StringBuilder();
            y = PageHeight - Margin;
        }

        void Text(string value, double x, double size, string font)
        {
            page.Append(CultureInfo.InvariantCulture,
                $"BT /{font} {Number(size)} Tf {Number(x)} {Number(y)} Td ({Escape(value)}) Tj ET\n");
        }

        void Right(string value, double rightEdge, double size)
        {
            var width = value.Length * CourierAdvance * size;
            Text(value, rightEdge - width, size, "F3");
        }

        void Rule()
        {
            page.Append(CultureInfo.InvariantCulture,
                $"0.5 w {Number(Margin)} {Number(y)} m {Number(PageWidth - Margin)} {Number(y)} l S\n");
        }

        Text("MageRide — fleet invoice", Margin, 16, "F2");
        y -= 24;

        foreach (var line in Metadata(detail, fleetName))
        {
            Text(line, Margin, BodySize, "F1");
            y -= Leading;
        }

        y -= 6;
        Rule();
        y -= Leading;

        Text("Vehicle", Margin, TableSize, "F2");
        Text("Type", Margin + 150, TableSize, "F2");
        Text("Status", Margin + 260, TableSize, "F2");
        Right("Amount (Rs)", PageWidth - Margin, TableSize);
        y -= 4;
        Rule();
        y -= Leading;

        if (detail.Lines.Count == 0)
        {
            // Not an empty table: an operator holding a bill for nothing is entitled to be told why,
            // and "no Mode B vehicles" is the answer AL-03 gives.
            Text(
                "No chargeable vehicles this month. Mode A vehicles are free and are not billed (AL-03).",
                Margin,
                BodySize,
                "F1");

            y -= Leading;
        }

        foreach (var line in detail.Lines)
        {
            if (y < Margin + (Leading * 4))
            {
                NewPage();
            }

            Text(line.RegistrationNumber, Margin, TableSize, "F3");
            Text(line.VehicleType, Margin + 150, TableSize, "F3");
            Text(line.Status, Margin + 260, TableSize, "F3");
            Right(InvoiceCsv.Rupees(line.AmountMinor), PageWidth - Margin, TableSize);
            y -= Leading;
        }

        if (y < Margin + (Leading * 3))
        {
            NewPage();
        }

        y -= 4;
        Rule();
        y -= Leading + 2;

        // Σ of the lines, not the stored total — for InvoiceCsv's reason: if the two ever disagreed,
        // the document an operator holds should show it rather than hide it.
        Text($"Total ({detail.Lines.Count} vehicle(s))", Margin, 11, "F2");
        Right(InvoiceCsv.Rupees(detail.LineSumMinor), PageWidth - Margin, 11);

        y -= Leading * 2;
        Text(
            invoice.Status == InvoiceStatuses.Paid
                ? $"Settled {invoice.SettledAt:yyyy-MM-dd HH:mm} UTC · journal entry {invoice.JournalEntryId}"
                : "This invoice is settled from the fleet wallet. Top up in the Fleet Portal to pay it.",
            Margin,
            BodySize,
            "F1");

        pages.Add(page.ToString());

        return pages;
    }

    private static IEnumerable<string> Metadata(FleetInvoiceDetail detail, string fleetName)
    {
        var invoice = detail.Invoice;

        yield return $"Organisation:  {fleetName}";
        yield return $"Invoice:       {invoice.Id}";
        yield return $"Period:        {invoice.PeriodMonth:MMMM yyyy}";
        yield return $"Status:        {invoice.Status}";

        if (invoice.DueAt is { } dueAt)
        {
            yield return $"Due:           {dueAt:yyyy-MM-dd}";
        }

        yield return $"Currency:      {invoice.Currency}";
    }

    /// <summary>PDF literal-string escaping, plus the WinAnsi ceiling.</summary>
    /// <remarks>
    /// <c>(</c>, <c>)</c> and <c>\</c> end or escape a literal string and must be escaped; anything
    /// outside Latin-1 has no glyph in a base-14 font and becomes <c>?</c> rather than a byte that
    /// renders as something else. Bytes 0x7F–0xFF are written as octal escapes, which keeps the file
    /// pure ASCII and immune to a transfer that mangles the high bit.
    /// </remarks>
    internal static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        foreach (var character in value)
        {
            switch (character)
            {
                case '(' or ')' or '\\':
                    builder.Append('\\').Append(character);
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case >= ' ' and <= '~':
                    builder.Append(character);
                    break;
                case <= (char)0xFF and > (char)0x7E:
                    builder.Append('\\').Append(Convert.ToString((int)character, 8).PadLeft(3, '0'));
                    break;
                default:
                    builder.Append('?');
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

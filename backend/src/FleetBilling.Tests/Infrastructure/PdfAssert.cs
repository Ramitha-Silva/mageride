using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MageRide.FleetBilling.Tests.Infrastructure;

/// <summary>
/// Parses a PDF back far enough to prove a reader could open it.
/// </summary>
/// <remarks>
/// <para>
/// A PDF is read <em>backwards</em>: the file ends with <c>startxref</c> pointing at a
/// cross-reference table, which holds one 20-byte entry per object giving its byte offset, and the
/// trailer names the catalogue. Nothing in <c>InvoicePdf</c> is checked by the compiler, so this is
/// the part that has to be asserted — an offset one byte out produces a file that opens in some
/// readers and not others, which is the failure a "it looked fine" check would miss.
/// </para>
/// <para>
/// It is a structural check and not a renderer: what a page <em>says</em> is asserted by the calling
/// test looking for its own strings in the (deliberately uncompressed) content streams.
/// </para>
/// </remarks>
internal static partial class PdfAssert
{
    public static void IsWellFormed(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        var startXrefIndex = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(startXrefIndex > 0, "the file has no startxref, so nothing can find the object table.");

        var startXref = long.Parse(
            text[(startXrefIndex + "startxref".Length)..].Trim().Split('\n')[0].Trim(),
            CultureInfo.InvariantCulture);

        Assert.True(
            startXref > 0 && startXref < pdf.Length,
            $"startxref points at byte {startXref}, outside a {pdf.Length}-byte file.");

        var xref = text[(int)startXref..];
        Assert.StartsWith("xref\n", xref, StringComparison.Ordinal);

        var header = xref.Split('\n')[1].Split(' ');
        var size = int.Parse(header[1], CultureInfo.InvariantCulture);

        Assert.True(size > 5, $"a document with {size} objects cannot carry a catalogue, a page tree and a page.");

        // Entry 0 is the head of the free list; entries 1..size-1 are objects. Each is exactly 20
        // bytes, which is what lets a reader seek by multiplication rather than by parsing.
        var entries = xref.Split('\n').Skip(2).Take(size).ToArray();

        Assert.Equal("0000000000 65535 f ", entries[0]);

        for (var number = 1; number < size; number++)
        {
            var entry = entries[number];

            Assert.True(
                XrefEntry().IsMatch(entry),
                $"cross-reference entry {number} is '{entry}', which is not a 20-byte in-use record.");

            var offset = (int)long.Parse(entry[..10], CultureInfo.InvariantCulture);

            Assert.True(offset > 0 && offset < pdf.Length, $"object {number} claims byte {offset}.");
            Assert.StartsWith(
                $"{number} 0 obj",
                text[offset..Math.Min(text.Length, offset + 32)],
                StringComparison.Ordinal);
        }

        // The trailer names a catalogue, and the catalogue names a page tree with at least one page.
        Assert.Contains("/Root 1 0 R", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Pages", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Page ", text, StringComparison.Ordinal);

        // Every stream declares the length it actually has, or a reader stops mid-page.
        foreach (Match match in StreamObject().Matches(text))
        {
            var declared = int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture);
            var body = match.Groups["body"].Value;

            Assert.Equal(declared, Encoding.Latin1.GetByteCount(body));
        }
    }

    [GeneratedRegex(@"^\d{10} \d{5} n $", RegexOptions.CultureInvariant)]
    private static partial Regex XrefEntry();

    [GeneratedRegex(
        @"<< /Length (?<length>\d+) >>\nstream\n(?<body>.*?)\nendstream",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StreamObject();
}

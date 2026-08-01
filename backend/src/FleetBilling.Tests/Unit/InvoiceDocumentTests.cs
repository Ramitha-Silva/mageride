using System.Text;
using MageRide.FleetBilling.Domain;
using MageRide.FleetBilling.Export;
using MageRide.FleetBilling.Tests.Infrastructure;

namespace MageRide.FleetBilling.Tests.Unit;

/// <summary>
/// The two renderers, against shapes an integration test cannot easily produce: a hundred vehicles,
/// a plate with a comma in it, an organisation whose name is not Latin.
/// </summary>
public sealed class InvoiceDocumentTests
{
    /// <summary>
    /// Pagination. A fleet of a hundred buses does not fit on one A4 page, and a table that ran off
    /// the bottom would be an invoice that silently omitted vehicles somebody is being charged for.
    /// </summary>
    [Fact]
    public void A_large_fleet_paginates_and_every_vehicle_is_on_the_document()
    {
        var detail = Detail(Enumerable.Range(1, 120)
            .Select(index => Line($"WP CAB-{index:D4}", 30_000))
            .ToArray());

        var pdf = InvoicePdf.Render(detail, "C060 Big Fleet");

        PdfAssert.IsWellFormed(pdf);

        var text = Encoding.Latin1.GetString(pdf);

        Assert.Contains("/Count 3", text, StringComparison.Ordinal);

        foreach (var line in detail.Lines)
        {
            Assert.Contains($"({line.RegistrationNumber})", text, StringComparison.Ordinal);
        }

        // And the total is Σ of the lines, not a number carried separately.
        Assert.Equal(3_600_000, detail.LineSumMinor);
        Assert.Contains("(36000.00)", text, StringComparison.Ordinal);
    }

    /// <summary>RFC 4180: a field containing the delimiter is quoted, or it becomes two columns.</summary>
    [Fact]
    public void A_plate_containing_a_comma_or_a_quote_is_escaped()
    {
        var detail = Detail([Line("WP, ABC-1234", 30_000), Line("WP \"X\" 1", 30_000)]);

        var csv = Encoding.UTF8.GetString(InvoiceCsv.Render(detail, "C060, Ltd"));

        Assert.Contains("\"WP, ABC-1234\",van,DUE,300.00,30000,LKR", csv, StringComparison.Ordinal);
        Assert.Contains("\"WP \"\"X\"\" 1\"", csv, StringComparison.Ordinal);
        Assert.Contains("# fleet,\"C060, Ltd\"", csv, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CSV is UTF-8 and carries a trilingual name intact; the PDF's base-14 fonts cover Latin-1
    /// and substitute the rest.
    /// </summary>
    /// <remarks>
    /// Stated in a test rather than left as a surprise: an embedded font is the dependency
    /// <c>InvoicePdf</c> exists to avoid, and Sri Lankan plates are Latin, so only an organisation's
    /// own name can be affected — and it is intact in the CSV, which is the file an accounts
    /// department reconciles.
    /// </remarks>
    [Fact]
    public void A_sinhala_organisation_name_survives_the_csv_and_degrades_visibly_in_the_pdf()
    {
        const string Name = "මගේ රථ සමූහය";

        var detail = Detail([Line("WP QA-1234", 30_000)]);

        var csv = Encoding.UTF8.GetString(InvoiceCsv.Render(detail, Name));
        Assert.Contains(Name, csv, StringComparison.Ordinal);

        var rendered = InvoicePdf.Render(detail, Name);
        PdfAssert.IsWellFormed(rendered);

        var pdf = Encoding.Latin1.GetString(rendered);

        // Every Sinhala code point becomes '?' — one per character, spaces kept — and the plate
        // beside it is untouched.
        Assert.Contains("Organisation:  ??? ?? ?????", pdf, StringComparison.Ordinal);
        Assert.Contains("(WP QA-1234)", pdf, StringComparison.Ordinal);
    }

    /// <summary>
    /// A literal string's delimiters are escaped, or a plate with a bracket in it ends the string
    /// early and the rest of the page becomes operators.
    /// </summary>
    [Theory]
    [InlineData("(", "\\(")]
    [InlineData(")", "\\)")]
    [InlineData("\\", "\\\\")]
    [InlineData("A(B)C", "A\\(B\\)C")]
    [InlineData("plain", "plain")]
    public void Pdf_literal_strings_escape_their_delimiters(string input, string expected) =>
        Assert.Equal(expected, InvoicePdf.Escape(input));

    /// <summary>An empty breakdown is a document, not an exception.</summary>
    [Fact]
    public void An_invoice_with_no_lines_still_renders_both_formats()
    {
        var detail = Detail([]);

        var csv = Encoding.UTF8.GetString(InvoiceCsv.Render(detail, "C060 Mode A Only"));

        Assert.Contains("# vehicles,0", csv, StringComparison.Ordinal);
        Assert.Contains("TOTAL,,,0.00,0,LKR", csv, StringComparison.Ordinal);

        PdfAssert.IsWellFormed(InvoicePdf.Render(detail, "C060 Mode A Only"));
    }

    private static FleetInvoiceDetail Detail(IReadOnlyList<FleetInvoiceLine> lines) =>
        new(
            new FleetInvoice(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Guid.NewGuid(),
                new DateOnly(2026, 7, 1),
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                lines.Sum(line => line.AmountMinor),
                "LKR",
                lines.Count == 0 ? InvoiceStatuses.Free : InvoiceStatuses.Due,
                JournalEntryId: null,
                DueAt: new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
                OverdueAt: null,
                SettledAt: null,
                CreatedAt: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                VehicleCount: lines.Count),
            lines);

    private static FleetInvoiceLine Line(string registration, long amountMinor) =>
        new(Guid.NewGuid(), registration, "van", amountMinor, "LKR", InvoiceLineStatuses.Due);
}

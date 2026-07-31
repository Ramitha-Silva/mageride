using MageRide.Ocr.Configuration;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Ocr;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Tests.Unit;

/// <summary>
/// The TSV writer's output, recorded from <c>tesseract sample.png stdout --psm 11 tsv</c> (5.3.4).
/// </summary>
public sealed class TesseractTsvTests
{
    private const string Recorded =
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
        + "1\t1\t0\t0\t0\t0\t0\t0\t640\t200\t-1\t\n"
        + "2\t1\t1\t0\t0\t0\t23\t38\t303\t24\t-1\t\n"
        + "3\t1\t1\t1\t0\t0\t23\t38\t303\t24\t-1\t\n"
        + "4\t1\t1\t1\t1\t0\t23\t38\t303\t24\t-1\t\n"
        + "5\t1\t1\t1\t1\t1\t23\t38\t47\t24\t94.073685\tNIC\n"
        + "5\t1\t1\t1\t1\t2\t92\t38\t234\t24\t94.073685\t199012345678\n"
        + "2\t1\t2\t0\t0\t0\t23\t108\t331\t24\t-1\t\n"
        + "5\t1\t2\t1\t1\t2\t145\t108\t209\t24\t75.467468\t2029-04-30\n";

    [Fact]
    public void Only_word_rows_become_words()
    {
        // Levels 1–4 are page, block, paragraph and line. Each carries the ENCLOSING box, so a
        // parser that took them would mask whole paragraphs where one number was printed.
        var words = TesseractTsv.Parse(Recorded);

        Assert.Equal(3, words.Count);
        Assert.Equal(["NIC", "199012345678", "2029-04-30"], words.Select(word => word.Text));
    }

    [Fact]
    public void Confidence_is_rescaled_to_the_platforms_zero_to_one()
    {
        var words = TesseractTsv.Parse(Recorded);

        Assert.Equal(0.94073685m, words[0].Confidence, 6);
        Assert.Equal(0.75467468m, words[2].Confidence, 6);
    }

    [Fact]
    public void The_box_survives_exactly()
    {
        var nic = TesseractTsv.Parse(Recorded)[1];

        Assert.Equal((92, 38, 234, 24), (nic.Left, nic.Top, nic.Width, nic.Height));
        Assert.Equal(326, nic.Right);
        Assert.Equal(62, nic.Bottom);
    }

    [Fact]
    public void A_malformed_row_is_skipped_rather_than_thrown_on()
    {
        // One unreadable row is one word missing from a page of them; throwing would lose the whole
        // page, and with it every redaction box on it.
        var words = TesseractTsv.Parse(Recorded + "5\t1\t2\tnonsense\n" + "5\tx\ty\n");

        Assert.Equal(3, words.Count);
    }

    [Fact]
    public void Empty_output_is_an_empty_page_not_a_crash()
    {
        Assert.Empty(TesseractTsv.Parse(null));
        Assert.Empty(TesseractTsv.Parse(string.Empty));
        Assert.Empty(TesseractTsv.Parse("level\tpage_num\n"));
    }
}

/// <summary>D6' §7.5's fallback, over pages this test writes rather than photographs.</summary>
public sealed class TesseractFieldExtractorTests
{
    private const decimal Ceiling = 0.60m;

    private static TesseractFieldExtractor Build() =>
        new(Options.Create(new OcrOptions { TesseractConfidenceCeiling = Ceiling }));

    private static OcrPage Page(params (string Text, int Top)[] lines) =>
        new(true, [.. lines.SelectMany((line, index) => Words(line.Text, line.Top))]);

    private static IEnumerable<OcrWord> Words(string line, int top)
    {
        var left = 40;

        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return new OcrWord(word, left, top, word.Length * 18, 30, 0.95m);

            left += (word.Length * 18) + 14;
        }
    }

    [Fact]
    public void Every_field_the_fallback_produces_is_capped_below_the_auto_verify_threshold()
    {
        // This is the whole reason the ceiling exists. AL-27 approves a vehicle with no human
        // involvement on these fields, and "a date near a label" is not a basis for that.
        var fields = Build().Extract(
            Page(("MOTOR INSURANCE", 40), ("INSURER CEYLINCO", 100), ("EXPIRY 31.03.2027", 160)),
            DocumentKinds.Insurance,
            null);

        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.True(field.Confidence <= Ceiling));
    }

    [Fact]
    public void An_expiry_is_read_off_its_label_and_normalised()
    {
        var fields = Build().Extract(
            Page(("REVENUE LICENCE", 40), ("LICENCE NO RL8891234", 100), ("EXPIRY 31.01.2027", 160)),
            DocumentKinds.RevenueLicense,
            null);

        Assert.Equal("2027-01-31", Value(fields, DocumentFieldKeys.RevenueExpiry));
        Assert.Equal("RL8891234", Value(fields, DocumentFieldKeys.RevenueNo));
    }

    [Fact]
    public void A_label_on_one_line_and_its_value_on_the_next_still_reads()
    {
        var fields = Build().Extract(
            Page(("DATE OF EXPIRY", 40), ("31.03.2027", 100)), DocumentKinds.Insurance, null);

        Assert.Equal("2027-03-31", Value(fields, DocumentFieldKeys.InsuranceExpiry));
    }

    [Fact]
    public void With_no_expiry_label_at_all_the_latest_date_on_the_page_is_taken()
    {
        // Right far more often than it is wrong, and an officer confirms it either way — returning
        // nothing would have made the same officer type it.
        var fields = Build().Extract(
            Page(("MOTOR COVER", 40), ("01.04.2026", 100), ("31.03.2027", 160)), DocumentKinds.Insurance, null);

        Assert.Equal("2027-03-31", Value(fields, DocumentFieldKeys.InsuranceExpiry));
    }

    [Fact]
    public void The_licence_number_and_the_NIC_are_told_apart_on_a_page_carrying_both()
    {
        var fields = Build().Extract(
            Page(("DRIVING LICENCE", 40), ("LICENCE NO B1234567", 100), ("NIC 199012345678", 160)),
            DocumentKinds.DrivingLicense,
            DocumentSides.Front);

        Assert.Equal("B1234567", Value(fields, DocumentFieldKeys.LicenceNo));
        Assert.Equal("199012345678", Value(fields, DocumentFieldKeys.NicNo));
    }

    [Fact]
    public void The_NIC_is_readable_on_this_path_because_the_raw_page_is_in_perimeter()
    {
        // The redacted copy has a black rectangle where it was; this engine reads the original, on
        // our own hardware. D-36 governs what LEAVES, not what we may read ourselves.
        var fields = Build().Extract(
            Page(("NIC 901234567V", 40)), DocumentKinds.DrivingLicense, DocumentSides.Front);

        Assert.Equal("901234567V", Value(fields, DocumentFieldKeys.NicNo));
    }

    [Fact]
    public void The_plate_is_read_and_the_comparison_is_left_to_the_platform()
    {
        var fields = Build().Extract(Page(("WP QA-1234", 40)), DocumentKinds.Registration, null);

        Assert.Equal("WP-QA-1234", Value(fields, DocumentFieldKeys.PlateText));

        // D5' §14.1a's verdict is a comparison against a value only the pipeline holds. An engine
        // that returned it would be guessing at the answer to a question it was not asked.
        Assert.DoesNotContain(fields, field => field.Key == DocumentFieldKeys.RegNoMatch);
    }

    [Fact]
    public void The_licence_classes_come_off_the_reverse()
    {
        var fields = Build().Extract(
            Page(("CLASSES", 40), ("A1 B C1", 100)), DocumentKinds.DrivingLicense, DocumentSides.Back);

        Assert.Equal("A1,B,C1", Value(fields, DocumentFieldKeys.AllowedVehicleTypes));
    }

    [Fact]
    public void An_unread_page_produces_nothing_rather_than_empty_fields() =>
        Assert.Empty(Build().Extract(OcrPage.Unavailable, DocumentKinds.Insurance, null));

    private static string? Value(IReadOnlyList<ExtractedField> fields, string key) =>
        fields.FirstOrDefault(field => field.Key == key)?.Value;
}

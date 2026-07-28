using MageRide.Provisioning.Bulk;

namespace MageRide.Provisioning.Tests.Domain;

/// <summary>The T-09 upload reader: rows of <c>imei,registrationNumber</c>.</summary>
public sealed class BulkCsvTests
{
    [Fact]
    public void A_header_row_is_recognised_and_skipped()
    {
        var lines = BulkCsv.Parse("imei,registrationNumber\n359586015829435,WP-QA-1234\n");

        var line = Assert.Single(lines);
        Assert.Equal(2, line.LineNumber);
        Assert.Equal("359586015829435", line.Imei);
        Assert.Equal("WP-QA-1234", line.RegistrationNumber);
    }

    /// <summary>
    /// The header is recognised by content, not by position. A file whose first row is a real IMEI
    /// has no header, and skipping row 1 unconditionally would silently drop a tracker.
    /// </summary>
    [Fact]
    public void A_file_with_no_header_keeps_its_first_row()
    {
        var lines = BulkCsv.Parse("359586015829435,WP-QA-1234\n359586015829436,WP-QA-1235\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal("359586015829435", lines[0].Imei);
        Assert.Equal(1, lines[0].LineNumber);
    }

    /// <summary>The line number is the spreadsheet's, so a report points at the row to fix.</summary>
    [Fact]
    public void Line_numbers_count_every_physical_line_including_the_header()
    {
        var lines = BulkCsv.Parse("IMEI,registrationNumber\n359586015829435,WP-QA-1\n359586015829436,WP-QA-2\n");

        Assert.Equal([2, 3], lines.Select(line => line.LineNumber));
    }

    [Fact]
    public void Crlf_endings_and_a_utf8_bom_survive_an_excel_export()
    {
        var lines = BulkCsv.Parse("﻿imei,registrationNumber\r\n359586015829435,WP-QA-1234\r\n");

        var line = Assert.Single(lines);
        Assert.Equal("359586015829435", line.Imei);
        Assert.Equal("WP-QA-1234", line.RegistrationNumber);
    }

    [Fact]
    public void Quoted_fields_are_unwrapped_and_a_doubled_quote_is_one_quote()
    {
        var lines = BulkCsv.Parse("\"359586015829435\",\"WP \"\"QA\"\" 1234\"\n");

        var line = Assert.Single(lines);
        Assert.Equal("359586015829435", line.Imei);
        Assert.Equal("WP \"QA\" 1234", line.RegistrationNumber);
    }

    [Fact]
    public void Blank_lines_are_skipped_rather_than_reported()
    {
        var lines = BulkCsv.Parse("359586015829435,WP-QA-1\n\n   \n359586015829436,WP-QA-2\n");

        Assert.Equal(2, lines.Count);
    }

    /// <summary>
    /// A malformed row is recorded and the parse keeps going. Throwing on the first bad line
    /// cannot tell an operator that row 4,127 of 5,000 is the problem, which is the whole job of
    /// the per-row report.
    /// </summary>
    [Fact]
    public void A_row_with_the_wrong_column_count_is_reported_and_the_parse_continues()
    {
        var lines = BulkCsv.Parse("359586015829435,WP-QA-1\nonly-one-column\n359586015829436,WP-QA-2\n");

        Assert.Equal(3, lines.Count);
        Assert.Null(lines[0].Error);
        Assert.Contains("expected 2 columns", lines[1].Error);
        Assert.Null(lines[2].Error);
    }

    [Fact]
    public void An_empty_file_yields_no_rows()
    {
        Assert.Empty(BulkCsv.Parse(string.Empty));
        Assert.Empty(BulkCsv.Parse("\n\n"));
    }

    /// <summary>A header on its own is not a batch — the caller uploaded a template.</summary>
    [Fact]
    public void A_header_alone_yields_no_rows() =>
        Assert.Empty(BulkCsv.Parse("imei,registrationNumber\n"));

    [Fact]
    public void Report_values_are_quoted_so_a_plate_with_a_comma_cannot_shift_a_column()
    {
        Assert.Equal("\"WP,QA\"", BulkCsv.Quote("WP,QA"));
        Assert.Equal("\"say \"\"hi\"\"\"", BulkCsv.Quote("say \"hi\""));
        Assert.Equal("\"\"", BulkCsv.Quote(null));
    }
}

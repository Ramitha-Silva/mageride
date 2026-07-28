using System.Globalization;
using System.Text;

namespace MageRide.Provisioning.Bulk;

/// <summary>One line lifted off the CSV, before anything has been resolved.</summary>
/// <param name="LineNumber">1-based, counting every physical line including the header — so a
/// report line points at the row an operator sees in their spreadsheet.</param>
public sealed record CsvLine(int LineNumber, string Imei, string RegistrationNumber, string? Error);

/// <summary>
/// The bulk upload's CSV reader: rows of <c>imei,registrationNumber</c> (D3', T-09).
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a dependency. The grammar is two columns of text with no embedded
/// newlines, and the failure mode that matters is not "this file uses an exotic quoting dialect"
/// but "row 4,127 of 5,000 is wrong and the operator has to be told which one". A parser that
/// throws on the first bad row cannot do that, so this one records a per-line error and keeps
/// going — the line still becomes a row in the job, and the report explains it.
/// </para>
/// <para>
/// Quoted fields are handled because the file usually comes out of Excel, which quotes anything it
/// feels like. A doubled quote inside a quoted field is an escaped quote, as RFC 4180 has it.
/// </para>
/// </remarks>
public static class BulkCsv
{
    /// <summary>The header a file may or may not start with. Matched case-insensitively.</summary>
    private const string ImeiColumn = "imei";

    /// <summary>Parses the whole file. Never throws for content — only for a null argument.</summary>
    public static IReadOnlyList<CsvLine> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = new List<CsvLine>();
        var lineNumber = 0;

        foreach (var raw in content.Split('\n'))
        {
            lineNumber++;

            // Trims the \r of a CRLF file and the BOM a Windows export puts on line 1.
            var line = raw.Trim('\r', '﻿', ' ', '\t');

            if (line.Length == 0)
            {
                continue;
            }

            var fields = SplitFields(line);

            // The header is optional and is recognised by content, not by position: a file whose
            // first row is a real IMEI has no header, and skipping it unconditionally would
            // silently drop a tracker.
            if (lineNumber == 1 && fields.Count > 0
                && fields[0].Trim().Equals(ImeiColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Count != 2)
            {
                lines.Add(new CsvLine(
                    lineNumber,
                    fields.Count > 0 ? fields[0].Trim() : string.Empty,
                    fields.Count > 1 ? fields[1].Trim() : string.Empty,
                    $"expected 2 columns (imei,registrationNumber), found {fields.Count}"));

                continue;
            }

            lines.Add(new CsvLine(lineNumber, fields[0].Trim(), fields[1].Trim(), null));
        }

        return lines;
    }

    /// <summary>Escapes a value for the error report. Everything is quoted; nothing has to be guessed.</summary>
    public static string Quote(string? value) =>
        '"' + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    /// <summary>Formats a row number the way a spreadsheet numbers it.</summary>
    public static string Line(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>(2);
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (quoted)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        fields.Add(field.ToString());

        return fields;
    }
}

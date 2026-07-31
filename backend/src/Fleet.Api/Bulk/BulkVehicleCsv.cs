using System.Globalization;
using System.Text;

namespace MageRide.Fleet.Bulk;

/// <summary>
/// One line lifted off the CSV, before anything has been resolved.
/// </summary>
/// <param name="LineNumber">
/// 1-based, counting every physical line including the header — so a report line points at the row
/// an operator sees in their spreadsheet.
/// </param>
/// <param name="Error">
/// A shape problem with the line itself (wrong number of columns). A value that is *well-formed and
/// wrong* — an unknown vehicle type, a Mode C row — is not decided here; the importer decides it,
/// so the report speaks in the same kebab error codes the single-vehicle POST does.
/// </param>
public sealed record BulkVehicleCsvLine(
    int LineNumber,
    string RegistrationNumber,
    string VehicleType,
    string Mode,
    string? ModeBBilling,
    string? DefaultMonthlyFareMinor,
    string? Error);

/// <summary>
/// The bulk upload's CSV reader: <c>registrationNumber,vehicleType,mode[,modeBBilling[,defaultMonthlyFareMinor]]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a dependency, for provisioning-svc's reason: the grammar is a few
/// columns of text with no embedded newlines, and the failure mode that matters is not an exotic
/// quoting dialect but "row 4,127 of 5,000 is wrong and the operator has to be told which one". A
/// parser that throws on the first bad row cannot do that, so this one records a per-line error and
/// keeps going.
/// </para>
/// <para>
/// <b>The last two columns are optional and are US-13.1b's.</b> "The setting (and default fare for
/// Paid) is captured at onboarding (single &amp; bulk CSV)" — so a school-transport operator can
/// bring a spreadsheet with the fare in it, and a bus company whose fleet is all Mode A can bring
/// three columns. `fleet.yaml` prints the three-column form in its description and says nothing
/// about the pair; accepting both is what makes the sentence in US-13.1b true without breaking the
/// file the contract describes.
/// </para>
/// <para>
/// Quoted fields are handled because the file usually comes out of Excel, which quotes what it
/// feels like. A doubled quote inside a quoted field is an escaped quote, as RFC 4180 has it.
/// </para>
/// </remarks>
public static class BulkVehicleCsv
{
    /// <summary>The header a file may or may not start with. Matched case-insensitively.</summary>
    private const string RegistrationColumn = "registrationnumber";

    /// <summary>The CSV the error report is written as, and the columns an operator re-uploads.</summary>
    public const string ReportHeader = "row,registrationNumber,vehicleType,mode,error,detail";

    /// <summary>Parses the whole file. Never throws for content — only for a null argument.</summary>
    public static IReadOnlyList<BulkVehicleCsvLine> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = new List<BulkVehicleCsvLine>();
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
            // first row is a real plate has no header, and skipping it unconditionally would
            // silently drop a vehicle.
            if (lineNumber == 1 && fields.Count > 0
                && fields[0].Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Equals(RegistrationColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Count is < 3 or > 5)
            {
                lines.Add(new BulkVehicleCsvLine(
                    lineNumber,
                    At(fields, 0),
                    At(fields, 1),
                    At(fields, 2),
                    null,
                    null,
                    "expected 3 to 5 columns "
                    + "(registrationNumber,vehicleType,mode[,modeBBilling[,defaultMonthlyFareMinor]]), "
                    + $"found {fields.Count}"));

                continue;
            }

            lines.Add(new BulkVehicleCsvLine(
                lineNumber,
                At(fields, 0),
                At(fields, 1),
                At(fields, 2),
                Optional(fields, 3),
                Optional(fields, 4),
                null));
        }

        return lines;
    }

    /// <summary>Escapes a value for the error report. Everything is quoted; nothing has to be guessed.</summary>
    public static string Quote(string? value) =>
        '"' + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    /// <summary>Formats a row number the way a spreadsheet numbers it.</summary>
    public static string Line(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string At(IReadOnlyList<string> fields, int index) =>
        index < fields.Count ? fields[index].Trim() : string.Empty;

    private static string? Optional(IReadOnlyList<string> fields, int index) =>
        index < fields.Count && fields[index].Trim().Length > 0 ? fields[index].Trim() : null;

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>(5);
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

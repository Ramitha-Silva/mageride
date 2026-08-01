using System.Globalization;
using System.Text;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Persistence;
using MageRide.Shared.Time;

namespace MageRide.AdminBff.Reporting;

/// <summary>
/// The transactions report as a file — CSV and PDF over exactly the rows the JSON route returns
/// (US-9A.15, SCR-AP-006).
/// </summary>
/// <remarks>
/// <para>
/// <b>One row list, three renderings.</b> The JSON, the CSV and the PDF are produced from the same
/// <see cref="TransactionRow"/> sequence for the same query, so "the export matches the screen" is
/// structural rather than a coincidence two queries happen to share — the same argument C061's
/// <c>DashboardStatsCsv</c> makes, and the reason its shape is followed here.
/// </para>
/// <para>
/// <b>Money stays in integer minor units and the column says so.</b> <c>amountMinor</c> is the
/// contract's own field name and the platform's representation (CLAUDE.md: "all currency values
/// stored and transmitted as integers"). A rupee column would put a decimal into a file a
/// spreadsheet then treats as floating point, and the spreadsheet would become the authority on how
/// much money moved.
/// </para>
/// <para>
/// <b>Invariant culture throughout, and every CSV field is quoted-escaped.</b> A name with a comma
/// in it would otherwise split one record across two columns, and a comma-decimal culture would do
/// the same to a number. Both are the class of bug that makes a finance export quietly wrong rather
/// than obviously broken.
/// </para>
/// </remarks>
internal static class TransactionExport
{
    /// <summary>The header row, in this order. Shared by both renderings so they cannot drift.</summary>
    public static readonly string[] Columns =
        ["ts", "kind", "amountMinor", "currency", "fromType", "from", "toType", "to", "description", "entryId"];

    /// <summary>The PDF's column widths, in points, summing to A4 minus its margins (523).</summary>
    private static readonly double[] Widths = [86, 62, 52, 32, 46, 74, 46, 74, 51, 0];

    public static byte[] RenderCsv(
        FinanceWindow window, string? kind, IReadOnlyList<TransactionRow> rows, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();

        foreach (var line in Preamble(window, kind, rows, generatedAt))
        {
            builder.Append("# ").Append(line.Replace(": ", ",", StringComparison.Ordinal)).Append("\r\n");
        }

        builder.Append(string.Join(',', Columns)).Append("\r\n");

        foreach (var row in rows)
        {
            builder.Append(string.Join(',', Cells(row).Select(Quote))).Append("\r\n");
        }

        // A BOM, so a spreadsheet opens the file as UTF-8: the party columns carry people's names.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    public static byte[] RenderPdf(
        FinanceWindow window, string? kind, IReadOnlyList<TransactionRow> rows, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(rows);

        // The entry id is dropped from the PDF and kept in the CSV. A page of 36-character UUIDs is
        // a page nobody reads, and the machine-readable rendering is the one that has to carry the
        // key that joins back to the ledger.
        var columns = Columns[..^1]
            .Select((header, index) => (Header: header, Width: Widths[index]))
            .ToArray();

        return PdfTable.Render(
            "MageRide — wallet transactions",
            [.. Preamble(window, kind, rows, generatedAt)],
            columns,
            [.. rows.Select(row => (IReadOnlyList<string>)[.. Cells(row).Take(columns.Length)])]);
    }

    /// <summary>
    /// The self-describing header both renderings carry.
    /// </summary>
    /// <remarks>
    /// <c>rowCount</c> is in it because both files are capped: an export that silently stopped at the
    /// limit would read as "that is all there was", which is the one thing a finance report must not
    /// say when it is not true.
    /// </remarks>
    private static IEnumerable<string> Preamble(
        FinanceWindow window, string? kind, IReadOnlyList<TransactionRow> rows, DateTimeOffset generatedAt) =>
    [
        "MageRide wallet transactions report",
        $"from: {window.From:yyyy-MM-dd}",
        $"to: {window.To:yyyy-MM-dd}",
        $"timezone: {BusinessCalendar.TimeZoneId}",
        $"kinds: {kind ?? string.Join('|', TransactionKinds.All)}",
        $"rowCount: {rows.Count.ToString(CultureInfo.InvariantCulture)}",
        $"generatedAt: {generatedAt.ToUniversalTime():O}",
        "money: integer minor units (LKR cents)",
    ];

    private static IEnumerable<string> Cells(TransactionRow row) =>
    [
        row.Ts.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        row.Kind,
        row.AmountMinor.ToString(CultureInfo.InvariantCulture),
        row.Currency,
        row.FromAccountType,
        Party(row.FromName, row.FromPartyId),
        row.ToAccountType,
        Party(row.ToName, row.ToPartyId),
        row.Description ?? string.Empty,
        row.EntryId.ToString(),
    ];

    /// <summary>
    /// The party as an operator recognises them: a name where there is one, the id where there is
    /// not, and nothing at all for the platform's own two accounts, which have no owner by CHECK.
    /// </summary>
    private static string Party(string? name, Guid? id) =>
        name is { Length: > 0 } ? name : id?.ToString() ?? string.Empty;

    /// <summary>RFC 4180: quote everything, and double an embedded quote.</summary>
    /// <remarks>
    /// Everything rather than only the fields that need it, because deciding per field is where the
    /// one name containing a comma gets missed — and a quoted numeric field is still a number to
    /// every reader that parses CSV rather than splitting on commas.
    /// </remarks>
    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

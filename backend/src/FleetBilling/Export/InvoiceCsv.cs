using System.Globalization;
using System.Text;
using MageRide.FleetBilling.Domain;

namespace MageRide.FleetBilling.Export;

/// <summary>
/// The consolidated invoice as CSV, for the Fleet Portal's download (US-13.10, SCR-FP-010).
/// </summary>
/// <remarks>
/// <para>
/// <b>One row per vehicle plus a TOTAL row, and the TOTAL is Σ of the rows above it.</b> That is the
/// definition of done rendered as a file: an operator who opens this in a spreadsheet and sums the
/// amount column gets the number on the last line, and the number the fleet wallet was debited.
/// </para>
/// <para>
/// <b>Money is printed in rupees with two decimals, and the minor units are printed beside them.</b>
/// The rupee column is what an accounts department reconciles against a bank statement; the minor
/// column is what reconciles against this platform, where every amount is an integer. Printing only
/// the first would make a spreadsheet's floating-point sum the authority on somebody's bill.
/// </para>
/// <para>
/// <b>Invariant culture throughout.</b> A comma-decimal culture would render <c>3.000,00</c> into a
/// comma-separated file and split one number across two columns — the same class of bug C059's
/// geofence WKT builder guards against.
/// </para>
/// <para>
/// <b>No Mode A row can appear</b>, because no Mode A line exists: <c>billing.fleet_invoice_lines</c>
/// is fed from <c>billing.monthly_subscriptions</c>, which carries Mode B rows only (1104). A fleet
/// that runs Mode A vehicles only exports a header, no vehicle rows, and a TOTAL of zero — which is
/// the most direct statement of AL-03 an operator can be handed.
/// </para>
/// </remarks>
internal static class InvoiceCsv
{
    /// <summary>The header row, in this order.</summary>
    public static readonly string[] Columns =
        ["registrationNumber", "vehicleType", "status", "amount", "amountMinor", "currency"];

    /// <summary>Renders one invoice. UTF-8 with a BOM, so Excel opens Sinhala and Tamil plates correctly.</summary>
    public static byte[] Render(FleetInvoiceDetail detail, string fleetName)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var invoice = detail.Invoice;
        var builder = new StringBuilder();

        // A short preamble before the table. Every value here is also in the JSON representation;
        // what a downloaded file needs and an API response does not is to be self-describing when it
        // is opened six months later with no context around it.
        builder.Append("# MageRide fleet invoice\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"# invoiceId,{invoice.Id}\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"# fleet,{Escape(fleetName)}\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"# periodMonth,{invoice.PeriodMonth:yyyy-MM}\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"# status,{invoice.Status}\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"# vehicles,{detail.Lines.Count}\r\n");

        if (invoice.SettledAt is { } settledAt)
        {
            builder.Append(CultureInfo.InvariantCulture, $"# settledAt,{settledAt:O}\r\n");
            builder.Append(CultureInfo.InvariantCulture, $"# journalEntryId,{invoice.JournalEntryId}\r\n");
        }

        builder.Append(string.Join(',', Columns)).Append("\r\n");

        foreach (var line in detail.Lines)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"{Escape(line.RegistrationNumber)},{Escape(line.VehicleType)},{line.Status},"
                + $"{Rupees(line.AmountMinor)},{line.AmountMinor},{line.Currency}\r\n");
        }

        // Σ of the rows above, computed from the lines rather than copied from the invoice: if the
        // two ever disagreed, the file would show it instead of hiding it behind the header.
        builder.Append(CultureInfo.InvariantCulture,
            $"TOTAL,,,{Rupees(detail.LineSumMinor)},{detail.LineSumMinor},{invoice.Currency}\r\n");

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    /// <summary>Minor units as rupees. Invariant, two decimals, no thousands separator.</summary>
    /// <remarks>
    /// No separator on purpose: a comma inside an unquoted CSV field is a column break, and quoting
    /// every number to keep one would make the file harder for the tools that read it.
    /// </remarks>
    internal static string Rupees(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 quoting, applied only where it is needed.</summary>
    private static string Escape(string value) =>
        value.AsSpan().IndexOfAny(Delimiters) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    /// <summary>The four characters RFC 4180 says force a quoted field.</summary>
    private static readonly System.Buffers.SearchValues<char> Delimiters =
        System.Buffers.SearchValues.Create(",\"\n\r");
}

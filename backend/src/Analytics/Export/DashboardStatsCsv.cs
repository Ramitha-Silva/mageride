using System.Globalization;
using System.Text;
using MageRide.Analytics.Domain;
using MageRide.Shared.Time;

namespace MageRide.Analytics.Export;

/// <summary>
/// The filtered dashboard figures as CSV — the export feed admin-bff serves as
/// <c>GET /v1/admin/dashboard/stats.csv</c> (AL-38, US-24.7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly the figures <c>/dashboard/stats</c> returns for the same query</b>, which is what the
/// contract says the file is. It is rendered from the same <see cref="DashboardStats"/> the JSON
/// route serialises, so the two cannot disagree: there is no second query and no second period
/// resolution.
/// </para>
/// <para>
/// <b>One row per metric, and the live rows have no previous value.</b> That empty cell is the
/// export's statement of D6' §I-28.5 — the live block bypasses the period filter, so there is no
/// preceding period for it to be compared against. Writing 0 there, or omitting the rows, would both
/// claim something the platform does not know.
/// </para>
/// <para>
/// <b>Money stays in integer minor units and says so in its name.</b> <c>grossFareMinor</c> and
/// <c>dailyFeeRevenueMinor</c> are the contract's own field names and the platform's own
/// representation (§0 Money); a rupee column would put a decimal point into a file whose other seven
/// rows are counts, and a spreadsheet's floating-point sum would become the authority on revenue.
/// The preamble says so in one line so nobody has to guess.
/// </para>
/// <para>
/// <b>Invariant culture throughout.</b> A comma-decimal culture would render a delta as
/// <c>12,73</c> and split one number across two columns of a comma-separated file — the same class
/// of bug C060's invoice export and C059's geofence builder guard against.
/// </para>
/// <para>
/// <b>UTF-8 with a BOM</b>, so Excel opens the file as UTF-8. Nothing here is outside ASCII today —
/// every field name is a contract identifier — but the preamble carries a timezone name and the file
/// is the one an operator forwards, so the BOM costs three bytes and removes a class of complaint.
/// </para>
/// </remarks>
internal static class DashboardStatsCsv
{
    /// <summary>The header row, in this order.</summary>
    public static readonly string[] Columns = ["metric", "value", "previousValue", "deltaPct"];

    /// <summary>The five period metrics, in the contract's <c>DashboardKpis</c> order.</summary>
    public static readonly string[] KpiMetrics =
        ["completedTrips", "grossFareMinor", "newRiders", "newDrivers", "dailyFeeRevenueMinor"];

    /// <summary>The three real-time metrics, in the contract's <c>DashboardLive</c> order.</summary>
    public static readonly string[] LiveMetrics = ["onlineDrivers", "pendingVerifications", "openTickets"];

    /// <summary>Renders one filtered view. <paramref name="generatedAt"/> is stamped from the service clock.</summary>
    public static byte[] Render(DashboardStats stats, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var builder = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        // A self-describing preamble. Every value here is derivable from the request that produced
        // the file — which is exactly why it belongs in the file: a download opened six months later
        // has no request around it, and percentages with no stated comparison window are
        // unfalsifiable.
        builder.Append("# MageRide admin dashboard statistics\r\n");
        builder.Append(invariant, $"# period,{stats.Period}\r\n");
        builder.Append(invariant, $"# from,{stats.Range.From:yyyy-MM-dd}\r\n");
        builder.Append(invariant, $"# to,{stats.Range.To:yyyy-MM-dd}\r\n");
        builder.Append(invariant, $"# previousFrom,{stats.PreviousRange.From:yyyy-MM-dd}\r\n");
        builder.Append(invariant, $"# previousTo,{stats.PreviousRange.To:yyyy-MM-dd}\r\n");
        builder.Append(invariant, $"# timezone,{BusinessCalendar.TimeZoneId}\r\n");
        builder.Append(invariant, $"# generatedAt,{generatedAt.ToUniversalTime():O}\r\n");
        builder.Append("# money,integer minor units (LKR cents)\r\n");

        builder.Append(string.Join(',', Columns)).Append("\r\n");

        Row(builder, KpiMetrics[0], stats.Kpis.CompletedTrips, stats.PreviousKpis.CompletedTrips, stats.DeltaVsPrev.CompletedTripsPct);
        Row(builder, KpiMetrics[1], stats.Kpis.GrossFareMinor, stats.PreviousKpis.GrossFareMinor, stats.DeltaVsPrev.GrossFarePct);
        Row(builder, KpiMetrics[2], stats.Kpis.NewRiders, stats.PreviousKpis.NewRiders, stats.DeltaVsPrev.NewRidersPct);
        Row(builder, KpiMetrics[3], stats.Kpis.NewDrivers, stats.PreviousKpis.NewDrivers, stats.DeltaVsPrev.NewDriversPct);
        Row(builder, KpiMetrics[4], stats.Kpis.DailyFeeRevenueMinor, stats.PreviousKpis.DailyFeeRevenueMinor, stats.DeltaVsPrev.DailyFeeRevenuePct);

        builder.Append("# live cards — real-time, outside the period filter (AL-38), so no previous period\r\n");

        LiveRow(builder, LiveMetrics[0], stats.Live.OnlineDrivers);
        LiveRow(builder, LiveMetrics[1], stats.Live.PendingVerifications);
        LiveRow(builder, LiveMetrics[2], stats.Live.OpenTickets);

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    /// <summary>A period metric: current, previous, and the percentage between them.</summary>
    /// <remarks>
    /// An undefined delta (the previous period was zero and this one is not) leaves the cell empty
    /// rather than writing <c>0</c> or <c>Infinity</c> — the same decision the JSON makes by omitting
    /// the property, spelled the way a spreadsheet reads it.
    /// </remarks>
    private static void Row(StringBuilder builder, string metric, long value, long previous, double? deltaPct) =>
        builder.Append(
            CultureInfo.InvariantCulture,
            $"{metric},{value},{previous},{Delta(deltaPct)}\r\n");

    private static void LiveRow(StringBuilder builder, string metric, int value) =>
        builder.Append(CultureInfo.InvariantCulture, $"{metric},{value},,\r\n");

    internal static string Delta(double? pct) =>
        pct is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
}

using System.Globalization;
using System.Text;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Export;

namespace MageRide.Analytics.Tests.Unit;

/// <summary>
/// The CSV export feed admin-bff serves as <c>GET /v1/admin/dashboard/stats.csv</c> (AL-38).
/// </summary>
public sealed class DashboardStatsCsvTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

    private static DashboardStats Sample() =>
        new(
            "custom",
            new StatsRange(new DateOnly(2026, 7, 15), new DateOnly(2026, 8, 14)),
            new DashboardKpis(CompletedTrips: 124, GrossFareMinor: 4_520_000, NewRiders: 31, NewDrivers: 5, DailyFeeRevenueMinor: 0),
            DashboardDeltas.Between(
                new DashboardKpis(124, 4_520_000, 31, 5, 0),
                new DashboardKpis(110, 4_100_000, 31, 0, 0)),
            new DashboardLive(OnlineDrivers: 42, PendingVerifications: 7, OpenTickets: 3))
        {
            PreviousRange = new StatsRange(new DateOnly(2026, 6, 14), new DateOnly(2026, 7, 14)),
            PreviousKpis = new DashboardKpis(110, 4_100_000, 31, 0, 0),
        };

    private static string[] Lines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes, Encoding.UTF8.GetPreamble().Length, bytes.Length - Encoding.UTF8.GetPreamble().Length)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Excel decides a file's encoding from the BOM, and this is the file an operator forwards.</summary>
    [Fact]
    public void The_file_starts_with_a_utf8_bom()
    {
        var bytes = DashboardStatsCsv.Render(Sample(), GeneratedAt);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
    }

    /// <summary>
    /// The preamble states the period and <b>both</b> ranges. Percentages with no stated comparison
    /// window are unfalsifiable once the request that produced them is gone.
    /// </summary>
    [Fact]
    public void The_preamble_names_the_period_and_both_ranges()
    {
        var lines = Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt));

        Assert.Contains("# period,custom", lines);
        Assert.Contains("# from,2026-07-15", lines);
        Assert.Contains("# to,2026-08-14", lines);
        Assert.Contains("# previousFrom,2026-06-14", lines);
        Assert.Contains("# previousTo,2026-07-14", lines);
        Assert.Contains("# timezone,Asia/Colombo", lines);
    }

    [Fact]
    public void The_header_is_the_four_columns()
    {
        var lines = Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt));

        Assert.Contains("metric,value,previousValue,deltaPct", lines);
    }

    /// <summary>
    /// Exactly the figures the JSON carries, under the contract's own field names — the file and the
    /// endpoint are rendered from one <see cref="DashboardStats"/>, so they cannot disagree.
    /// </summary>
    [Fact]
    public void Every_kpi_is_a_row_with_its_previous_value_and_delta()
    {
        var lines = Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt));

        Assert.Contains("completedTrips,124,110,12.73", lines);
        Assert.Contains("grossFareMinor,4520000,4100000,10.24", lines);
        Assert.Contains("newRiders,31,31,0", lines);
    }

    /// <summary>An undefined delta is an empty cell, not a zero and not "Infinity".</summary>
    [Fact]
    public void An_undefined_delta_leaves_its_cell_empty()
    {
        var lines = Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt));

        // 5 new drivers against a previous period that had none.
        Assert.Contains("newDrivers,5,0,", lines);
        Assert.Equal(string.Empty, DashboardStatsCsv.Delta(null));
    }

    /// <summary>
    /// The live rows carry no previous value. That empty pair is the file's statement of D6'
    /// §I-28.5 — the live block bypasses the period filter, so it has no preceding period.
    /// </summary>
    [Fact]
    public void The_live_rows_have_no_previous_period()
    {
        var lines = Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt));

        Assert.Contains("onlineDrivers,42,,", lines);
        Assert.Contains("pendingVerifications,7,,", lines);
        Assert.Contains("openTickets,3,,", lines);
        Assert.Contains(lines, line => line.StartsWith("# live cards", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every metric of both contract schemas appears exactly once — a KPI added to
    /// <c>DashboardKpis</c> and forgotten here would silently vanish from the download.
    /// </summary>
    [Fact]
    public void Every_metric_appears_exactly_once()
    {
        var lines = Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt));
        var expected = DashboardStatsCsv.KpiMetrics.Concat(DashboardStatsCsv.LiveMetrics);

        foreach (var metric in expected)
        {
            Assert.Single(lines, line => line.StartsWith(metric + ",", StringComparison.Ordinal));
        }

        Assert.Equal(
            DashboardStatsCsv.KpiMetrics.Length + DashboardStatsCsv.LiveMetrics.Length,
            lines.Count(line => !line.StartsWith('#') && !line.StartsWith("metric,", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A comma-decimal culture would render <c>12,73</c> and split one number across two columns of
    /// a comma-separated file. The renderer is invariant throughout.
    /// </summary>
    [Fact]
    public void The_file_is_invariant_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Contains("completedTrips,124,110,12.73", Lines(DashboardStatsCsv.Render(Sample(), GeneratedAt)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

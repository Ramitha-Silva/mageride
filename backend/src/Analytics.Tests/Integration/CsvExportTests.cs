using System.Text;
using MageRide.Analytics.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Analytics.Tests.Integration;

/// <summary>
/// The CSV export feed, over real rolled-up data — "exactly the figures the endpoint returns for the
/// same query" (AL-38).
/// </summary>
[Collection<AnalyticsCollection>]
public sealed class CsvExportTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 15, 6, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The file and the JSON are rendered from one <c>DashboardStats</c>, so they cannot disagree —
    /// asserted by reading the numbers back out of the file and comparing them with the response.
    /// </summary>
    [Fact]
    public async Task The_export_carries_exactly_the_figures_the_endpoint_returns()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var passenger = await harness.Seed.CreateUserAsync("passenger", Noon);
        var driver = await harness.Seed.CreateUserAsync("driver", Noon);
        var vehicle = await harness.Seed.CreateVehicleAsync(driver);

        await harness.Seed.CompleteRideAsync(passenger, Noon, 45_000);
        await harness.Seed.CompleteRideAsync(passenger, Noon.AddHours(1), 30_000);
        await harness.Seed.ChargeDailyFeeAsync(driver, vehicle, AnalyticsHarness.Today, 10_000);
        await harness.Seed.SetPresenceAsync(driver, vehicle, "ON_RIDE", Noon.AddHours(3));
        await harness.Seed.CreateTicketAsync(passenger, "IN_PROGRESS");

        await harness.RollupAsync(AnalyticsHarness.Today);

        var stats = await harness.StatsAsync("today");
        var rows = Rows(await harness.CsvAsync("today"));

        Assert.Equal(stats.Kpis.CompletedTrips.ToString(null as IFormatProvider), rows["completedTrips"][0]);
        Assert.Equal(stats.Kpis.GrossFareMinor.ToString(null as IFormatProvider), rows["grossFareMinor"][0]);
        Assert.Equal(stats.Kpis.NewRiders.ToString(null as IFormatProvider), rows["newRiders"][0]);
        Assert.Equal(stats.Kpis.NewDrivers.ToString(null as IFormatProvider), rows["newDrivers"][0]);
        Assert.Equal(stats.Kpis.DailyFeeRevenueMinor.ToString(null as IFormatProvider), rows["dailyFeeRevenueMinor"][0]);

        Assert.Equal(stats.Live.OnlineDrivers.ToString(null as IFormatProvider), rows["onlineDrivers"][0]);
        Assert.Equal(stats.Live.OpenTickets.ToString(null as IFormatProvider), rows["openTickets"][0]);

        // The live rows carry no previous period — the export's own statement of D6' §I-28.5.
        Assert.Equal(string.Empty, rows["onlineDrivers"][1]);
        Assert.Equal(string.Empty, rows["onlineDrivers"][2]);
    }

    /// <summary>
    /// A custom range's preamble names both windows, so a downloaded file says what its percentages
    /// were computed against.
    /// </summary>
    [Fact]
    public async Task The_export_states_the_range_it_was_filtered_to()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        var text = Text(await harness.CsvAsync("custom", new DateOnly(2026, 7, 15), new DateOnly(2026, 8, 14)));

        Assert.Contains("# period,custom", text, StringComparison.Ordinal);
        Assert.Contains("# from,2026-07-15", text, StringComparison.Ordinal);
        Assert.Contains("# to,2026-08-14", text, StringComparison.Ordinal);
        Assert.Contains("# previousFrom,2026-06-14", text, StringComparison.Ordinal);
        Assert.Contains("# previousTo,2026-07-14", text, StringComparison.Ordinal);
    }

    /// <summary>An invalid query fails the same way for the download as for the JSON.</summary>
    [Fact]
    public async Task An_invalid_query_is_refused_before_a_file_is_produced()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await AnalyticsHarness.StartAsync(postgres);

        await Assert.ThrowsAsync<Shared.Errors.MageRideValidationException>(
            () => harness.CsvAsync("custom", new DateOnly(2026, 8, 1), new DateOnly(2026, 7, 1)));
    }

    private static string Text(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes, Encoding.UTF8.GetPreamble().Length, bytes.Length - Encoding.UTF8.GetPreamble().Length);

    /// <summary>Metric name → the three cells after it.</summary>
    private static Dictionary<string, string[]> Rows(byte[] bytes)
    {
        var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var line in Text(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#') || line.StartsWith("metric,", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split(',');

            rows[cells[0]] = cells[1..];
        }

        return rows;
    }
}

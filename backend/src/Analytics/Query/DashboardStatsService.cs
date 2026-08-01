using MageRide.Analytics.Configuration;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Analytics.Query;

/// <summary>
/// The read half of AL-38: period KPIs, vs-previous-period deltas, and the real-time block.
/// </summary>
/// <remarks>
/// admin-bff (C062) maps <c>GET /v1/admin/dashboard/stats</c> and <c>stats.csv</c> onto this. It
/// draws no route and enforces no RBAC — deny-by-default and the audit event are the BFF's, because
/// that is where the caller's effective role set is known (AL-06, D-35).
/// </remarks>
public interface IDashboardStatsService
{
    /// <summary>
    /// Resolves <c>?period=&amp;from=&amp;to=</c> and answers the whole payload.
    /// </summary>
    /// <exception cref="Shared.Errors.MageRideValidationException">
    /// The query is not a period this contract admits — <c>400 validation-failed</c>.
    /// </exception>
    Task<DashboardStats> GetAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    /// <summary>The same figures as a CSV download (AL-38's export).</summary>
    Task<byte[]> ExportCsvAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    /// <summary>The materialised days behind a period, oldest first. For diagnostics and backfill decisions.</summary>
    Task<IReadOnlyList<DailyMetric>> DaysAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class DashboardStatsService(
    DailyMetricsRepository metrics,
    LiveCountersRepository live,
    IOptions<AnalyticsOptions> options,
    TimeProvider clock) : IDashboardStatsService
{
    private readonly AnalyticsOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<DashboardStats> GetAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var resolved = Resolve(period, from, to);

        // Sequential, not concurrent: the two aggregates and the live read are three cheap queries
        // and a connection each, and running them in parallel would triple this endpoint's pool
        // draw on the one screen every internal user opens first.
        var current = await metrics.AggregateAsync(resolved.Range, cancellationToken);
        var previous = await metrics.AggregateAsync(resolved.PreviousRange, cancellationToken);
        var counters = await live.ReadAsync(clock.GetUtcNow() - _options.PresenceFreshness, cancellationToken);

        return new DashboardStats(
            resolved.Period,
            resolved.Range,
            current,
            DashboardDeltas.Between(current, previous),
            counters)
        {
            PreviousRange = resolved.PreviousRange,
            PreviousKpis = previous,
        };
    }

    public async Task<byte[]> ExportCsvAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var stats = await GetAsync(period, from, to, cancellationToken);

        return Export.DashboardStatsCsv.Render(stats, clock.GetUtcNow());
    }

    public Task<IReadOnlyList<DailyMetric>> DaysAsync(
        string? period, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        metrics.ListAsync(Resolve(period, from, to).Range, cancellationToken);

    private StatsPeriod Resolve(string? period, DateOnly? from, DateOnly? to) =>
        StatsPeriod.Resolve(period, from, to, StatsPeriod.TodayIn(clock), _options);
}

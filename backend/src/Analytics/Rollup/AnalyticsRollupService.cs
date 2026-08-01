using System.Diagnostics;
using MageRide.Analytics.Configuration;
using MageRide.Analytics.Domain;
using MageRide.Analytics.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Analytics.Rollup;

/// <summary>
/// Materialises <c>analytics.daily_metrics</c> (AL-38). The write half of the read model.
/// </summary>
public interface IAnalyticsRollupService
{
    /// <summary>Recomputes one Asia/Colombo business date.</summary>
    Task<RollupRunResult> RunDayAsync(DateOnly metricDate, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes every Colombo date in an inclusive range, oldest first. Bounded by
    /// <see cref="AnalyticsOptions.MaxBackfillDays"/>.
    /// </summary>
    Task<RollupRunResult> RunRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>
    /// One scheduled pass: today back through <see cref="AnalyticsOptions.RollupLookbackDays"/>.
    /// </summary>
    Task<RollupRunResult> RunScheduledPassAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class AnalyticsRollupService(
    DailyMetricsRepository metrics,
    IOptions<AnalyticsOptions> options,
    TimeProvider clock,
    ILogger<AnalyticsRollupService> logger) : IAnalyticsRollupService
{
    private readonly AnalyticsOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public Task<RollupRunResult> RunDayAsync(DateOnly metricDate, CancellationToken cancellationToken) =>
        RunRangeAsync(metricDate, metricDate, cancellationToken);

    public async Task<RollupRunResult> RunRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["to"] = ["to must not be before from."] });
        }

        var days = to.DayNumber - from.DayNumber + 1;

        if (days > _options.MaxBackfillDays)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["to"] = [$"A backfill covers at most {_options.MaxBackfillDays} days; this one covers {days}."],
                });
        }

        var started = Stopwatch.GetTimestamp();
        var now = clock.GetUtcNow();

        // Oldest first, one statement per day. Deliberately not one statement over the whole range
        // grouped by day: a range grouped in SQL would only write the days that had activity, and a
        // day with none has to become a zero row — otherwise "no row" would mean both "nothing
        // happened" and "not rolled up yet", and the period sum could not tell them apart.
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (dayStart, dayEnd) = BusinessCalendar.DayRange(day);

            await metrics.RollupAsync(day, dayStart, dayEnd, now, cancellationToken);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        logger.LogInformation(
            "Rolled up {Days} Colombo metric day(s) {From}..{To} in {ElapsedMs} ms",
            days,
            from,
            to,
            (long)elapsed.TotalMilliseconds);

        return new RollupRunResult(from, to, days, elapsed);
    }

    public Task<RollupRunResult> RunScheduledPassAsync(CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(clock);

        // Inclusive of today, so LookbackDays = 1 means "today only".
        return RunRangeAsync(today.AddDays(-(_options.RollupLookbackDays - 1)), today, cancellationToken);
    }
}

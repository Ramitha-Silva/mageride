using MageRide.Analytics.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Analytics.Rollup;

/// <summary>
/// Keeps the recent Colombo days materialised. The scheduled half of AL-38's read model.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interval, not a midnight alarm.</b> Every pass is idempotent — one upsert per day, keyed by
/// the date — so re-running costs a handful of aggregates and catches everything a once-a-night
/// timer would miss: a fare that settled this morning for a ride completed last night, a deployment
/// that was rolling at midnight, a replica whose clock moved. A nightly alarm gets exactly one
/// attempt per day to be running, and its failure mode is a day the dashboard never learns about.
/// fleet-billing-svc's runner is written under the same rule (C060).
/// </para>
/// <para>
/// <b>Every replica runs it and there is no lease.</b> A lock would protect an operation that is
/// already idempotent, and would add a way for the dashboard to stop updating entirely when the lock
/// holder dies badly. Two replicas rolling up the same day write the same five numbers.
/// </para>
/// <para>
/// <b>A pass that throws must not take the host down.</b> An unhandled exception here would end the
/// <see cref="BackgroundService"/> for the process's lifetime, so one bad database moment would
/// freeze the dashboard until somebody restarted the pod — and a frozen dashboard looks exactly like
/// a quiet week. The next tick retries.
/// </para>
/// <para>
/// The job resolves its service from a scope per pass rather than holding one: the repository takes
/// a connection factory, and a singleton holding a scoped dependency is how a background service
/// ends up with a connection that outlives its pool.
/// </para>
/// </remarks>
internal sealed class AnalyticsRollupJob(
    IServiceScopeFactory scopes,
    IOptions<AnalyticsOptions> options,
    TimeProvider clock,
    ILogger<AnalyticsRollupJob> logger) : BackgroundService
{
    private readonly AnalyticsOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RollupEnabled)
        {
            // Loud, because the failure is invisible from the outside: `/admin/dashboard/stats`
            // keeps answering, the live cards keep moving, and only the period figures quietly stop.
            logger.LogError(
                "Analytics:RollupEnabled is false: analytics.daily_metrics is never refreshed, so every "
                + "period on the admin dashboard reports whatever was last materialised. The live cards "
                + "are unaffected, which is what makes this hard to notice. A backfill through "
                + "IAnalyticsRollupService.RunRangeAsync still works.");

            return;
        }

        using var timer = new PeriodicTimer(_options.RollupInterval, clock);

        do
        {
            try
            {
                using var scope = scopes.CreateScope();

                await scope.ServiceProvider
                    .GetRequiredService<IAnalyticsRollupService>()
                    .RunScheduledPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A pass that threw must not end the job for the process's lifetime.
            catch (Exception exception)
            {
                logger.LogError(exception, "The analytics rollup pass failed. Retrying next tick.");
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

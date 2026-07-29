using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Levels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Timers;

/// <summary>
/// D5' §4.2's level-up half: turns 4- and 5-star ratings into levels, on a clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sweep and not a consumer.</b> A rating is not an event on any topic D6' §2.1 declares —
/// it is a row in <c>trips.ratings</c>, written by whoever collects the rating — so there is nothing
/// to subscribe to. Recomputing from the table is also what makes the engine idempotent: it
/// compares the total against <c>points_awarded_total</c> and applies the difference, so running it
/// twice, or on two replicas at once, awards nothing twice.
/// </para>
/// <para>
/// <b>Why it exists at all when the read path already refreshes.</b>
/// <c>GET /v1/drivers/{id}/level</c> and the Job Board gate both refresh the driver they are about,
/// so a driver who looks is never told something stale. But the dispatch hot path reads the level
/// through <c>Reputation.GetDriverLevel</c> over gRPC, which reads the table directly — so without
/// this sweep a driver's hundredth five-star ride would not improve their scoring until they
/// happened to open the level screen.
/// </para>
/// </remarks>
public sealed class DriverLevelWorker(
    IServiceProvider services,
    IOptions<DispatchOptions> options,
    ILogger<DriverLevelWorker> logger) : BackgroundService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Driver Level sweep running every {Interval}, up to {Batch} drivers a pass (D5' §4.2)",
            _options.LevelSweepInterval, _options.LevelSweepBatchSize);

        using var ticker = new PeriodicTimer(_options.LevelSweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var counted = await SweepOnceAsync(stoppingToken);

                if (counted > 0)
                {
                    logger.LogDebug("Driver Level sweep recounted {Counted} driver(s)", counted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nothing is lost by a failed pass: the watermark only moves on a committed apply,
                // so the same drivers are on the next pass's list.
                logger.LogError(ex, "Driver Level sweep failed; retrying on the next tick");
            }

            try
            {
                await ticker.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One sweep. Exposed so a test can count levels without waiting on the ticker.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IDriverLevelService>()
            .SweepAsync(cancellationToken);
    }
}

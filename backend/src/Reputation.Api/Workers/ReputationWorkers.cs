using MageRide.Reputation.Configuration;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Detection;
using MageRide.Reputation.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Reputation.Workers;

/// <summary>
/// Settles block states whose time box has passed, and drops network observations past their PDPA
/// retention.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sweep is not what makes an expiry correct.</b> <c>GetBlockStatus</c> already applies the
/// time box on read, so a driver whose delisting ended is dispatchable the moment they are asked
/// about, whether or not this worker has run. What the sweep adds is durability — the row stops
/// saying DELISTED — and the <c>reputation.block_state_changed</c> event, which is what tells
/// anybody who is not asking.
/// </para>
/// <para>
/// A lease is unnecessary here, unlike ride-svc's R-04 timers: the claim is
/// <c>FOR UPDATE SKIP LOCKED</c> inside the same transaction that settles the row, so two replicas
/// sweeping at once take disjoint sets and neither can settle a row twice.
/// </para>
/// </remarks>
public sealed class BlockStateExpiryWorker(
    IServiceProvider services,
    INpgsqlConnectionFactory connections,
    IDetectionRepository detection,
    TimeProvider clock,
    IOptions<ReputationOptions> options,
    ILogger<BlockStateExpiryWorker> logger) : BackgroundService
{
    private readonly ReputationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ExpiryInterval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never fatal: the next tick retries, and the read path is already correct without
                // this worker.
                logger.LogError(ex, "Block-state expiry sweep failed");
            }
        }
    }

    /// <summary>One pass. Exposed so a test can drive it without waiting for a tick.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var scope = services.CreateAsyncScope();
        var reputation = scope.ServiceProvider.GetRequiredService<IReputationService>();

        var settled = await reputation.SettleExpiredAsync(now, cancellationToken);

        await using var connection = await connections.OpenAsync(cancellationToken);
        var purged = await detection.PurgeObservationsAsync(
            connection, now - _options.NetworkObservationRetention, cancellationToken);

        if (settled > 0 || purged > 0)
        {
            logger.LogInformation(
                "Settled {Settled} expired block states, purged {Purged} network observations", settled, purged);
        }

        return settled;
    }
}

/// <summary>Runs the E-07 detector on a timer.</summary>
/// <remarks>
/// Its cadence is a latency choice and nothing else — <c>ux_fraud_flags_window</c> makes a pass
/// idempotent inside its detection window, so running more often finds the same patterns sooner and
/// raises no more flags.
/// </remarks>
public sealed class CollusionDetectorWorker(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<ReputationOptions> options,
    ILogger<CollusionDetectorWorker> logger) : BackgroundService
{
    private readonly ReputationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.DetectorInterval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var detector = scope.ServiceProvider.GetRequiredService<ICollusionDetector>();

                await detector.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "E-07 detection pass failed");
            }
        }
    }
}

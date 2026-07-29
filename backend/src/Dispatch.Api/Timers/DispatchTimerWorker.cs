using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Timers;

/// <summary>
/// Sweeps <c>dispatch.timers</c>: US-6A.11's 120-second global cascade deadline and R-15's
/// last-will release grace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="OfferExpiryWorker"/> on purpose.</b> That one watches
/// <c>rides.timers</c> and is R-04's guarantee that a *single offer* cannot outlive its 15 seconds;
/// this one watches the two clocks whose subject is the whole ride or the driver's session. The
/// tables are different, the failure modes are different — a stuck offer strands one driver, a
/// stuck global deadline strands a passenger watching a spinner for ever — and each is switched on
/// by its own flag so an operator can stop one without stopping the other.
/// </para>
/// <para>
/// Same lease discipline as the offer sweep: one
/// <c>UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)</c> that pushes <c>fire_at</c> out,
/// so two replicas split a batch rather than both firing it, and a worker that dies mid-fire hands
/// the row back instead of taking the ride's only deadline with it.
/// </para>
/// </remarks>
public sealed class DispatchTimerWorker(
    IServiceProvider services,
    IOptions<DispatchOptions> options,
    ILogger<DispatchTimerWorker> logger) : BackgroundService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Dispatch timer sweep running every {Interval}: the {Timeout} global cascade deadline (US-6A.11) " +
            "and the {Grace} last-will release grace (R-15)",
            _options.TimerPollInterval, _options.GlobalTimeout, _options.OfferReleaseGrace);

        using var ticker = new PeriodicTimer(_options.TimerPollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that throws must not kill the worker: the next tick retries, and the
                // rows are still marked unfired, so nothing is lost.
                logger.LogError(ex, "Dispatch timer sweep failed; retrying on the next tick");
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

    /// <summary>One sweep. Exposed so a test can fire a deadline without waiting on the ticker.</summary>
    /// <returns>How many timers were claimed.</returns>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var connectionFactory = scope.ServiceProvider.GetRequiredService<INpgsqlConnectionFactory>();
        var timers = scope.ServiceProvider.GetRequiredService<IDispatchTimerRepository>();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        IReadOnlyList<DueDispatchTimer> due;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            // Autocommit: the claim is one statement and must be visible to other replicas
            // immediately. Holding it open across the ride-svc calls below would make the fire
            // path's own writes to these rows — on a different connection — wait for this
            // transaction, which is a deadlock against itself.
            due = await timers.ClaimDueAsync(
                connection, null, _options.TimerBatchSize, _options.TimerLease, cancellationToken);
        }

        foreach (var timer in due)
        {
            await dispatch.RunTimerAsync(timer, cancellationToken);
        }

        return due.Count;
    }
}

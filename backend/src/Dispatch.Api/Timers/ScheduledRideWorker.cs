using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Timers;

/// <summary>
/// D5' §3.7's T-30 trigger: a scheduled ride "goes live 30 min prior".
/// </summary>
/// <remarks>
/// <para>
/// <b>The booking table is its own timer.</b> No <c>dispatch.timers</c> row is armed for this, and
/// none can be: that table's <c>ride_id</c> has a foreign key onto <c>rides.rides</c>, and at T-30
/// the ride does not exist yet — creating it is the whole job. <c>ix_sched_due</c> (migration 0704)
/// is a partial index on <c>pickup_time WHERE status = 'SCHEDULED'</c>, which is exactly "the next
/// thing to fire", and the status column is the claim.
/// </para>
/// <para>
/// <b>It materialises and stops.</b> ride-svc emits <c>ride.requested</c> inside the transaction
/// that created the ride (R-13); the ordinary consumer picks it up and runs the ordinary round,
/// which discovers the booking behind the ride and restricts itself to the intent list. Dispatching
/// from here as well would give one ride two racing first rounds.
/// </para>
/// <para>
/// The interval is 30 seconds rather than the offer backstop's 500 ms, and the difference is the
/// point: R-04 promises a fire within a second of a 15-second window, while this one is placing a
/// ride half an hour early.
/// </para>
/// </remarks>
public sealed class ScheduledRideWorker(
    IServiceProvider services,
    IOptions<DispatchOptions> options,
    ILogger<ScheduledRideWorker> logger) : BackgroundService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Scheduled-ride sweep running every {Interval}: bookings are materialised at T-{Lead} (D5' §3.7)",
            _options.ScheduledPollInterval, _options.ScheduledLeadTime);

        using var ticker = new PeriodicTimer(_options.ScheduledPollInterval);

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
                // A sweep that throws must not kill the worker: the claim is rolled back with the
                // transaction, so every booking it held is still SCHEDULED and still due.
                logger.LogError(ex, "Scheduled-ride sweep failed; retrying on the next tick");
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

    /// <summary>One sweep. Exposed so a test can fire T-30 without waiting on the ticker.</summary>
    /// <returns>How many bookings were claimed.</returns>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IScheduledRideService>()
            .MaterialiseDueAsync(cancellationToken);
    }
}

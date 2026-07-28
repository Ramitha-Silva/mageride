using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch.Timers;

/// <summary>
/// The R-04 durable backstop: sweeps due <c>rides.timers.kind='offer_expiry'</c> rows and returns
/// each unanswered ride to <c>Matching</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the guarantee, not the Redis TTL.</b> ADD §9.4 marks <c>offer:{rideId}</c> a "fast
/// hint, NOT authoritative"; R-04 says the durable backstop must fire "independent of Redis". The
/// test that matters flushes the whole keyspace mid-offer and asserts the ride still comes back to
/// Matching at the deadline.
/// </para>
/// <para>
/// Claims are leases: one <c>UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)</c> that
/// pushes <c>fire_at</c> out by <c>Dispatch:TimerLease</c>, so two replicas sweeping the same
/// half-second split the batch instead of both firing the same expiry, and a worker that dies
/// mid-expiry loses the lease rather than the ride's only backstop.
/// </para>
/// </remarks>
public sealed class OfferExpiryWorker(
    IServiceProvider services,
    IOptions<DispatchOptions> options,
    ILogger<OfferExpiryWorker> logger) : BackgroundService
{
    private readonly DispatchOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Offer-expiry backstop sweeping rides.timers every {Interval}", _options.TimerPollInterval);

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
                logger.LogError(ex, "Offer-expiry sweep failed; retrying on the next tick");
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

    /// <summary>One sweep. Exposed so a test can fire the backstop without waiting on the ticker.</summary>
    /// <returns>How many timers were claimed.</returns>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var connectionFactory = scope.ServiceProvider.GetRequiredService<INpgsqlConnectionFactory>();
        var timers = scope.ServiceProvider.GetRequiredService<IOfferTimerRepository>();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        IReadOnlyList<Domain.DueOfferTimer> due;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            // Autocommit: the claim is one statement and must be visible to other replicas
            // immediately. Holding it open across the ride-svc calls below would make ExpireAsync's
            // own writes to these rows — on a different connection — wait for this transaction,
            // which is a deadlock against itself.
            due = await timers.ClaimDueAsync(
                connection, null, _options.TimerBatchSize, _options.TimerLease, cancellationToken);
        }

        foreach (var timer in due)
        {
            await dispatch.ExpireAsync(timer, cancellationToken);
        }

        return due.Count;
    }
}

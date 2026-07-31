using MageRide.Notification.Configuration;
using MageRide.Notification.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Sending;

/// <summary>
/// D-27's exponential-backoff worker: drains <c>comms.notifications</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A lease, not a lock.</b> Each pass leases a batch by pushing <c>next_attempt_at</c> out
/// (<see cref="INotificationRepository.LeaseDueAsync"/>) and then talks to the gateways with no
/// transaction open. A worker that dies mid-send leaves rows that become due again when the lease
/// elapses — self-healing, and multi-replica safe without a leader election, which is the same
/// shape ride-svc's R-04 timer sweep uses.
/// </para>
/// <para>
/// <b>The lease is derived, not configured.</b> It is the per-attempt budget of the slowest
/// transport plus a margin; making it a knob would let an operator set it below the push timeout,
/// which would hand the same row to a second replica while the first was still waiting on APNs.
/// </para>
/// </remarks>
internal sealed class DeliveryWorker(
    INotificationRepository notifications,
    INotificationDeliverer deliverer,
    IOptions<NotificationOptions> options,
    IOptions<SmsOptions> smsOptions,
    TimeProvider clock,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly SmsOptions _sms = smsOptions?.Value ?? throw new ArgumentNullException(nameof(smsOptions));

    /// <summary>See the remarks: the slowest single attempt, doubled, with a floor.</summary>
    private TimeSpan Lease
    {
        get
        {
            var slowest = TimeSpan.FromTicks(Math.Max(
                _options.PushTimeout.Ticks,
                _sms.RequestTimeout.Ticks * _sms.MaxAttemptsPerGateway * 2));

            return TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, slowest.Ticks * 2));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        logger.LogInformation(
            "Delivering notifications every {Interval} in batches of {Batch} (lease {Lease}).",
            _options.DeliveryInterval, _options.DeliveryBatchSize, Lease);

        using var timer = new PeriodicTimer(_options.DeliveryInterval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A failed pass must not take the loop down: the rows are still there and the next
                // pass will find them.
                logger.LogError(exception, "A notification delivery pass failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass. Exposed so a test can drive it without a clock.</summary>
    internal async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var due = await notifications.LeaseDueAsync(
            clock.GetUtcNow(), Lease, _options.DeliveryBatchSize, cancellationToken);

        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await deliverer.DeliverAsync(row, cancellationToken);
        }

        return due.Count;
    }
}

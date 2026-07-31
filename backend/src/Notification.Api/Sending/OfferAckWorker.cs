using MageRide.Notification.Configuration;
using MageRide.Notification.Domain;
using MageRide.Notification.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Sending;

/// <summary>
/// E-01's other half: an offer push that nobody acked within three seconds becomes an SMS,
/// <b>exactly once</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly-once is two database facts, not one worker's care.</b>
/// <see cref="INotificationRepository.ClaimUnackedOffersAsync"/> moves the push from <c>Sent</c> to
/// <c>FellBackToSms</c> in the same statement that selects it, so two replicas sweeping the same
/// instant produce one claimed row between them. The SMS is then enqueued under
/// <c>fallback:{pushId}</c>, which <c>ux_notifications_dedupe</c> makes unique — so even a worker
/// that crashed between the claim and the insert, and a claim that somehow ran twice, produce one
/// message. Two independent guards, because sending a driver two SMS costs money and interrupts
/// them, and sending none costs them a fare.
/// </para>
/// <para>
/// <b>The fallback is the same notification, on the other channel.</b> Its
/// <c>notification_type</c> stays <c>RIDE_OFFER</c> — a support reader looking for "why did this
/// driver get an SMS" finds the offer, and <c>fallback_of</c> points at the push it replaced. Only
/// the channel and the template differ, and the template is the one
/// <see cref="NotificationTypeSpec.FallbackTemplateKey"/> names.
/// </para>
/// <para>
/// <b>An ack that arrives late changes nothing.</b> <c>TryAckAsync</c> is bound to <c>Sent</c>, so
/// a handset that woke up on the fourth second finds the row already fallen back and the driver
/// gets both messages once. That is the honest outcome of a slow device: an ack cannot un-send an
/// SMS.
/// </para>
/// </remarks>
internal sealed class OfferAckWorker(
    INotificationRepository notifications,
    IRecipientRepository recipients,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<OfferAckWorker> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        logger.LogInformation(
            "Watching for offer pushes unacked after {Window} (E-01), sweeping every {Interval}.",
            _options.OfferAckWindow, _options.OfferAckSweepInterval);

        using var timer = new PeriodicTimer(_options.OfferAckSweepInterval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "An offer-ack sweep failed; the rows stay claimable.");
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

    /// <summary>One pass. Exposed so a test can drive it deterministically.</summary>
    /// <returns>How many SMS fallbacks were enqueued.</returns>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var unacked = await notifications.ClaimUnackedOffersAsync(
            now, _options.OfferAckBatchSize, cancellationToken);

        if (unacked.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var push in unacked)
        {
            var spec = NotificationCatalogue.TryGet(push.NotificationType, out var found) ? found : null;

            if (spec?.FallbackTemplateKey is not { } templateKey)
            {
                // Claimed but nothing to fall back to. Only RIDE_OFFER arms an ack deadline, so
                // this means a row was written with one and no fallback — worth saying out loud.
                logger.LogWarning(
                    "Notification {Id} ({Type}) had an ack deadline and no fallback template; nothing was sent.",
                    push.Id, push.NotificationType);

                continue;
            }

            if (push.RecipientUserId is not { } driverId)
            {
                logger.LogWarning("Unacked offer {Id} has no recipient account; no SMS fallback is possible.", push.Id);
                continue;
            }

            var driver = await recipients.FindAsync(driverId, cancellationToken);

            if (driver?.Phone is not { Length: > 0 } phone)
            {
                logger.LogWarning(
                    "Driver {DriverId} missed offer push {Id} and has no number on file; E-01's fallback cannot run.",
                    driverId, push.Id);

                continue;
            }

            var row = await notifications.EnqueueAsync(
                new NewNotification(
                    DedupeKey: NotificationDedupe.Fallback(push.Id),
                    NotificationType: push.NotificationType,
                    TemplateKey: templateKey,
                    Channel: NotificationChannels.Sms,
                    RecipientUserId: driverId,
                    RecipientPhone: phone,
                    Language: push.Language,
                    Priority: NotificationPriorities.High,
                    Payload: push.Payload,
                    Status: NotificationStatuses.Pending,
                    NextAttemptAt: now,
                    FallbackOf: push.Id),
                cancellationToken);

            if (row is null)
            {
                // The second guard doing its job: this push has already produced its one SMS.
                logger.LogDebug("Offer push {Id} already has its SMS fallback; nothing more is sent.", push.Id);
                continue;
            }

            sent++;

            logger.LogInformation(
                "Offer push {PushId} to driver {DriverId} was not acked within {Window}; falling back to SMS {SmsId} (E-01).",
                push.Id, driverId, _options.OfferAckWindow, row.Id);
        }

        return sent;
    }
}

/// <summary>
/// Drops settled notifications past <c>Notification:Retention</c>.
/// </summary>
/// <remarks>
/// This is the control that takes <c>recipient_phone</c> back out of the database. The column holds
/// an E.164 number in the clear for the two recipients who have no account (AL-21, AL-45), because
/// a durable queue cannot re-derive a delivery address it was never given; the retention window is
/// how long it stays. Deliberately a PDPA control (E-06) as much as housekeeping — which is why it
/// deletes rather than nulls the column.
/// </remarks>
internal sealed class RetentionWorker(
    INotificationRepository notifications,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(_options.RetentionSweepInterval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await SweepAsync(stoppingToken);

                if (removed > 0)
                {
                    logger.LogInformation(
                        "Removed {Count} notification(s) older than {Retention}.", removed, _options.Retention);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "A notification retention sweep failed.");
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

    internal Task<int> SweepAsync(CancellationToken cancellationToken) =>
        notifications.PurgeBeforeAsync(
            clock.GetUtcNow() - _options.Retention, _options.RetentionBatchSize, cancellationToken);
}

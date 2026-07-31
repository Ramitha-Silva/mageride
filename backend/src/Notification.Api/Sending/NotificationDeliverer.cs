using MageRide.Notification.Configuration;
using MageRide.Notification.Domain;
using MageRide.Notification.Persistence;
using MageRide.Notification.Push;
using MageRide.Notification.Sms;
using MageRide.Notification.Templates;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Sending;

/// <summary>What an inline dispatch did, for a caller that cannot wait for the worker.</summary>
/// <param name="Gateways">
/// Every SMS gateway the message was handed to — two on a D-33 parallel send, whether or not both
/// answered. safety-svc records one per column on <c>safety.sos_events</c>.
/// </param>
public sealed record InlineDelivery(
    Guid Id, string Status, string? Provider, IReadOnlyList<string> Gateways, string? Error);

/// <summary>Sends one queued notification and records what happened to it.</summary>
public interface INotificationDeliverer
{
    Task DeliverAsync(NotificationRow row, CancellationToken cancellationToken);

    /// <summary>
    /// Delivers one row **on the calling request** and reports the outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For the types with a latency SLO, and only those.</b> D-33 budgets an SOS five seconds at
    /// the 99th percentile from button tap to dispatch; the queue drains every
    /// <c>Notification:DeliveryInterval</c> in batches, so under a backlog of ride offers an SOS
    /// would wait behind them. Bypassing the queue makes the SLO independent of queue depth, which
    /// is what "design for it" means for an emergency path.
    /// </para>
    /// <para>
    /// The row is still written and still claimed by <c>ux_notifications_dedupe</c> first — this is
    /// a different <em>moment</em> of delivery, not a different pipeline. A failure leaves the row
    /// <c>Pending</c> with a backoff, so the worker still picks it up: an inline attempt that lost
    /// both gateways is retried rather than dropped.
    /// </para>
    /// </remarks>
    Task<InlineDelivery?> DeliverNowAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// The transport half: render, address, send, record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three failure kinds, and they are not the same.</b> A template that cannot be rendered fails
/// the notification outright — it will be missing the same value in five seconds, and a retry loop
/// over a permanent fault is how a queue fills up. A gateway that refused is retried on D-27's
/// backoff. A recipient with no live device is <c>Failed</c> immediately, because the queue cannot
/// invent a handset.
/// </para>
/// <para>
/// <b>A push goes to every live device and succeeds if any one of them took it.</b> AL-08 binds one
/// install per app per person, but a driver who is also a passenger has two, and a ride offer must
/// reach the driver app whichever one FCM answers for first. A token the provider calls dead is
/// deleted rather than retried — the same reason 1302 carries <c>ux_notif_tokens_token</c>.
/// </para>
/// </remarks>
internal sealed class NotificationDeliverer(
    INotificationRepository notifications,
    IDeviceTokenRepository devices,
    ITemplateSource templates,
    IEnumerable<IPushChannel> pushChannels,
    ISmsSender sms,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationDeliverer> logger) : INotificationDeliverer
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IReadOnlyList<IPushChannel> _pushChannels = [.. pushChannels];

    public async Task<InlineDelivery?> DeliverNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await notifications.FindAsync(id, cancellationToken);

        if (row is null)
        {
            return null;
        }

        // The gateway names are captured during the send rather than read back off the row: the row
        // records the *winner*, and D-33's claim is about how many were tried.
        var trace = new DeliveryTrace();

        await DeliverCoreAsync(row, trace, cancellationToken);

        var settled = await notifications.FindAsync(id, cancellationToken);

        if (settled is null)
        {
            return null;
        }

        var outcome = await notifications.ReadOutcomeAsync(id, cancellationToken);

        return new InlineDelivery(settled.Id, settled.Status, outcome.Provider, trace.Gateways, outcome.LastError);
    }

    public Task DeliverAsync(NotificationRow row, CancellationToken cancellationToken) =>
        DeliverCoreAsync(row, trace: null, cancellationToken);

    /// <summary>
    /// One delivery, optionally recording what the transports were asked to do.
    /// </summary>
    /// <remarks>
    /// <paramref name="trace"/> is threaded rather than stashed in an <c>AsyncLocal</c>: an
    /// async-local set inside a nested call does not flow back to its caller, so the inline
    /// dispatch would have read an empty list every time — which is the sort of bug that shows up as
    /// a column that is quietly always null. The worker passes <see langword="null"/> and pays
    /// nothing.
    /// </remarks>
    private async Task DeliverCoreAsync(
        NotificationRow row, DeliveryTrace? trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var spec = NotificationCatalogue.TryGet(row.NotificationType, out var found)
            ? found
            : null;

        RenderedMessage? message;

        try
        {
            message = await RenderAsync(row, cancellationToken);
        }
        catch (TemplateRenderException exception)
        {
            // Permanent. The message would be missing the same value on every retry, and D-26's
            // rule is that a body with a hole in it is not sent at all.
            logger.LogError(
                exception, "Notification {Id} ({Type}) cannot be rendered; it will not be sent.", row.Id, row.NotificationType);

            await notifications.MarkFailedAsync(row.Id, null, exception.Message, cancellationToken);
            return;
        }
        catch (HttpRequestException exception)
        {
            // content-svc is unreachable. Transient by nature — retry on the backoff.
            await RetryOrFailAsync(row, null, exception.Message, cancellationToken);
            return;
        }

        if (string.Equals(row.Channel, NotificationChannels.Sms, StringComparison.Ordinal))
        {
            await DeliverSmsAsync(row, spec, message, trace, cancellationToken);
        }
        else
        {
            await DeliverPushAsync(row, spec, message, cancellationToken);
        }
    }

    // -----------------------------------------------------------------------------------------

    private async Task<RenderedMessage?> RenderAsync(NotificationRow row, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.TemplateKey))
        {
            // A silent data message (E-01's offer, P-02's location request) or a broadcast, whose
            // text travels on the payload because it belongs to `content.broadcasts` rather than to
            // a template key.
            var values = row.Values();

            return values.TryGetValue(BodyValue, out var body) && !string.IsNullOrWhiteSpace(body)
                ? new RenderedMessage(values.GetValueOrDefault(TitleValue), body, row.Language, 0)
                : null;
        }

        var template = await templates.ResolveAsync(row.TemplateKey, row.Language, cancellationToken);

        return TemplateRenderer.Render(template, row.Values());
    }

    private async Task DeliverPushAsync(
        NotificationRow row, NotificationTypeSpec? spec, RenderedMessage? message, CancellationToken cancellationToken)
    {
        if (row.RecipientUserId is not { } userId)
        {
            await notifications.MarkFailedAsync(
                row.Id, null, "A push has no account to address (comms.notification_tokens is keyed by user).", cancellationToken);

            return;
        }

        var now = clock.GetUtcNow();
        var tokens = await devices.ListForUserAsync(userId, now - _options.TokenStaleAfter, cancellationToken);

        if (tokens.Count == 0)
        {
            // Not retried: a handset does not appear because we waited. The row records why, which
            // is what a support question about a missing offer needs.
            await notifications.MarkFailedAsync(
                row.Id, null, "No live device token for this recipient.", cancellationToken);

            return;
        }

        var push = new PushMessage(
            Title: message?.Title,
            Body: message?.Body,
            Data: row.Values(),
            Priority: row.Priority,
            Silent: spec?.Silent ?? false);

        var results = await FanOutAsync(tokens, push, cancellationToken);

        var delivered = results.FirstOrDefault(result => result.Result.Delivered);

        foreach (var (device, result) in results.Where(pair => pair.Result.TokenIsDead))
        {
            await devices.DeleteAsync(device.Id, cancellationToken);
        }

        if (delivered.Result is { Delivered: true } success)
        {
            // E-01: the three-second clock starts when the push is accepted by the provider, not
            // when the offer was armed. `ack_deadline_at` is what the sweep compares against.
            var ackDeadline = spec?.AcksExpected == true && _options.OfferSmsFallbackEnabled
                ? clock.GetUtcNow() + _options.OfferAckWindow
                : (DateTimeOffset?)null;

            await notifications.MarkSentAsync(
                row.Id, success.Provider, success.MessageId, ackDeadline, cancellationToken);

            return;
        }

        var errors = string.Join("; ", results.Select(pair => pair.Result.Error).Where(error => error is not null));

        if (results.All(pair => pair.Result.TokenIsDead))
        {
            await notifications.MarkFailedAsync(
                row.Id, results[0].Result.Provider, $"Every device token was rejected: {errors}", cancellationToken);

            return;
        }

        await RetryOrFailAsync(row, results[0].Result.Provider, errors, cancellationToken);
    }

    private async Task<IReadOnlyList<(DeviceToken Device, PushResult Result)>> FanOutAsync(
        IReadOnlyList<DeviceToken> tokens, PushMessage push, CancellationToken cancellationToken)
    {
        var results = new List<(DeviceToken, PushResult)>(tokens.Count);

        // D6' §7.4's "batch send". FCM HTTP v1 has no multicast endpoint, so a batch is N concurrent
        // sends bounded by PushFanoutBatchSize — which matters because a driver with a stale token
        // list must not turn one offer into an unbounded burst.
        foreach (var chunk in tokens.Chunk(_options.PushFanoutBatchSize))
        {
            var sends = chunk.Select(async device =>
            {
                var channel = ChannelFor(device.Platform);

                return channel is null
                    ? (device, PushResult.Failed("none", $"No push transport is configured for {device.Platform}."))
                    : (device, await channel.SendAsync(device, push, cancellationToken));
            });

            results.AddRange(await Task.WhenAll(sends));
        }

        return results;
    }

    private async Task DeliverSmsAsync(
        NotificationRow row,
        NotificationTypeSpec? spec,
        RenderedMessage? message,
        DeliveryTrace? trace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.RecipientPhone))
        {
            await notifications.MarkFailedAsync(row.Id, null, "An SMS row has no destination number.", cancellationToken);
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Body))
        {
            await notifications.MarkFailedAsync(
                row.Id, null, "An SMS has no body: every SMS type renders a template (D-26).", cancellationToken);

            return;
        }

        // D-33 is the only branch here, and it is a property of the type rather than of the caller:
        // an SOS pays for two messages to buy the p99, and nothing else does.
        var result = spec?.DualGateway == true
            ? await sms.SendUrgentAsync(row.RecipientPhone, message.Body, cancellationToken)
            : await sms.SendAsync(row.RecipientPhone, message.Body, cancellationToken);

        if (trace is not null)
        {
            trace.Gateways = result.Attempted;
        }

        if (result.Delivered)
        {
            await notifications.MarkSentAsync(row.Id, result.Gateway, result.MessageId, null, cancellationToken);
            return;
        }

        await RetryOrFailAsync(row, result.Gateway, result.Error ?? "The SMS gateway refused it.", cancellationToken);
    }

    /// <summary>
    /// D-27's exponential backoff, doubling from <c>BackoffBase</c> and capped at <c>BackoffMax</c>.
    /// </summary>
    /// <remarks>
    /// No jitter. The retries here are per row rather than per host and the queue is drained in
    /// batches, so the thundering herd jitter defends against does not arise — and a deterministic
    /// schedule is one a test can assert to the second.
    /// </remarks>
    private async Task RetryOrFailAsync(
        NotificationRow row, string? provider, string error, CancellationToken cancellationToken)
    {
        var attempts = row.Attempts + 1;

        if (attempts >= _options.MaxAttempts)
        {
            logger.LogError(
                "Notification {Id} ({Type}) failed after {Attempts} attempts: {Error}",
                row.Id, row.NotificationType, attempts, error);

            await notifications.MarkFailedAsync(row.Id, provider, error, cancellationToken);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(
            _options.BackoffBase.Ticks * (1L << row.Attempts),
            _options.BackoffMax.Ticks));

        logger.LogWarning(
            "Notification {Id} ({Type}) attempt {Attempts} failed ({Error}); retrying in {Delay}.",
            row.Id, row.NotificationType, attempts, error, delay);

        await notifications.MarkRetryAsync(row.Id, provider, error, clock.GetUtcNow() + delay, cancellationToken);
    }

    private IPushChannel? ChannelFor(string platform)
    {
        // The exact platform first, so a deployment with both a live channel and the log transport
        // registered uses the live one; the log transport answers `*` and is the fallback.
        return _pushChannels.FirstOrDefault(channel =>
                   channel.IsConfigured
                   && string.Equals(channel.Platform, platform, StringComparison.OrdinalIgnoreCase))
               ?? _pushChannels.FirstOrDefault(channel =>
                   channel.IsConfigured
                   && string.Equals(channel.Platform, DevicePlatforms.Any, StringComparison.Ordinal));
    }

    /// <summary>What one delivery's transports were, for a caller that asked to be told.</summary>
    private sealed class DeliveryTrace
    {
        public IReadOnlyList<string> Gateways { get; set; } = [];
    }

    /// <summary>Payload keys a caller uses when the text is not a template's (a broadcast).</summary>
    internal const string TitleValue = "title";

    internal const string BodyValue = "body";
}

using MageRide.Notification.Configuration;
using MageRide.Notification.Domain;
using MageRide.Notification.Persistence;
using MageRide.Shared.RateLimiting;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Sending;

/// <summary>One notification to enqueue.</summary>
/// <param name="Type">A <see cref="NotificationCatalogue"/> type. Decides channel, priority and template.</param>
/// <param name="DedupeKey">The producer's claim (<see cref="NotificationDedupe"/>).</param>
/// <param name="UserId">The account, when there is one.</param>
/// <param name="Phone">
/// E.164, for a recipient with no account (AL-21, AL-45) or when the caller knows the number and
/// the type is an SMS one. Ignored for a push.
/// </param>
/// <param name="Values">
/// Template substitution values and the client data payload, together — the payload column serves
/// both (see <see cref="PayloadValues"/>).
/// </param>
/// <param name="TemplateKeyOverride">
/// Only for the two types whose text is not a template: a broadcast carries its own trilingual
/// message, and a caller of <c>POST /v1/internal/notify/send</c> may name a key directly.
/// </param>
/// <param name="RateLimitSubject">
/// Whose bucket to spend, for the types that have one. P-12's subject is the <em>booker</em>, not
/// the rider being asked — a rider who is pinged by five different bookers has not done anything.
/// </param>
public sealed record NotificationRequest(
    string Type,
    string DedupeKey,
    Guid? UserId = null,
    string? Phone = null,
    IReadOnlyDictionary<string, string>? Values = null,
    string? TemplateKeyOverride = null,
    Guid? RateLimitSubject = null);

/// <summary>What the enqueue did.</summary>
public enum NotificationOutcome
{
    /// <summary>Claimed and queued for delivery.</summary>
    Queued,

    /// <summary>Another delivery of the same fact already claimed it. Not an error.</summary>
    AlreadyClaimed,

    /// <summary>Muted by preference (US-10.7) or refused by a limit (P-12).</summary>
    Suppressed,

    /// <summary>There is nobody to send to — no account, no number.</summary>
    Undeliverable,
}

/// <summary>The result of one enqueue.</summary>
public sealed record NotificationReceipt(NotificationOutcome Outcome, Guid? Id, string? Reason);

/// <summary>Puts a notification on the queue, having decided whether it may be sent at all.</summary>
public interface INotificationService
{
    Task<NotificationReceipt> EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc cref="INotificationService"/>
/// <remarks>
/// <para>
/// <b>Every decision that can refuse a message happens here, before the transport exists.</b> The
/// preference switch (US-10.7), the P-12 buckets and "is there anybody to send to" are all resolved
/// at enqueue, so the delivery worker's only question is "did the gateway take it". That is what
/// keeps a retry from re-asking a question whose answer may have changed — a driver who mutes a
/// type while an attempt is in flight still gets the one that was already accepted, which is the
/// behaviour a queue can actually promise.
/// </para>
/// <para>
/// <b>A refused notification is a row, not a silence.</b> <c>Suppressed</c> costs one insert and
/// answers the support question this service otherwise cannot: "I never got the message". Without
/// it, muted and lost are indistinguishable from the outside.
/// </para>
/// </remarks>
internal sealed class NotificationService(
    INotificationRepository notifications,
    IRecipientRepository recipients,
    ITokenBucketRateLimiter rateLimiter,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationService> logger) : INotificationService
{
    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<NotificationReceipt> EnqueueAsync(
        NotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var spec = NotificationCatalogue.Require(request.Type);
        var now = clock.GetUtcNow();

        var recipient = await ResolveAsync(request, cancellationToken);

        if (recipient is null)
        {
            logger.LogWarning(
                "{Type} for {Dedupe} has no recipient — no account and no number. Nothing is queued.",
                request.Type, request.DedupeKey);

            return new NotificationReceipt(NotificationOutcome.Undeliverable, null, "no-recipient");
        }

        var channel = spec.Channel;

        if (string.Equals(channel, NotificationChannels.Sms, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(recipient.Phone))
        {
            return new NotificationReceipt(NotificationOutcome.Undeliverable, null, "no-phone");
        }

        // Claim the row first, then apply the rules to the row we own. The other order would spend
        // a P-12 token on a redelivered event and let five genuine requests become four.
        var row = await notifications.EnqueueAsync(
            new NewNotification(
                DedupeKey: request.DedupeKey,
                NotificationType: spec.Type,
                TemplateKey: request.TemplateKeyOverride ?? spec.TemplateKey,
                Channel: channel,
                RecipientUserId: recipient.UserId,
                RecipientPhone: string.Equals(channel, NotificationChannels.Sms, StringComparison.Ordinal)
                    ? recipient.Phone
                    : null,
                Language: recipient.Language,
                Priority: spec.Priority,
                Payload: PayloadValues.Write(request.Values),
                Status: NotificationStatuses.Pending,
                NextAttemptAt: now),
            cancellationToken);

        if (row is null)
        {
            // The redelivery case. D6' §2.3 makes every topic at least once, so this is the normal
            // path on a consumer restart, not an anomaly worth a warning.
            logger.LogDebug("{Type} for {Dedupe} was already claimed; nothing queued.", spec.Type, request.DedupeKey);
            return new NotificationReceipt(NotificationOutcome.AlreadyClaimed, null, null);
        }

        if (!recipient.Accepts(spec))
        {
            await notifications.MarkSuppressedAsync(row.Id, "muted by preference (US-10.7)", cancellationToken);
            return new NotificationReceipt(NotificationOutcome.Suppressed, row.Id, "preference");
        }

        if (await IsRateLimitedAsync(spec, request, cancellationToken) is { } refusal)
        {
            await notifications.MarkSuppressedAsync(row.Id, refusal, cancellationToken);
            return new NotificationReceipt(NotificationOutcome.Suppressed, row.Id, refusal);
        }

        return new NotificationReceipt(NotificationOutcome.Queued, row.Id, null);
    }

    private async Task<NotificationRecipient?> ResolveAsync(
        NotificationRequest request, CancellationToken cancellationToken)
    {
        if (request.UserId is { } userId)
        {
            var found = await recipients.FindAsync(userId, cancellationToken);

            if (found is not null)
            {
                // A caller-supplied number wins over the account's only when the account has none —
                // an SOS to an emergency contact addresses a number that is not the user's own, and
                // that case arrives with UserId null.
                return string.IsNullOrWhiteSpace(found.Phone) && !string.IsNullOrWhiteSpace(request.Phone)
                    ? found with { Phone = request.Phone }
                    : found;
            }

            logger.LogWarning("No iam.users row for {UserId}; {Type} cannot be delivered.", userId, request.Type);
        }

        return string.IsNullOrWhiteSpace(request.Phone)
            ? null
            : NotificationRecipient.Anonymous(request.Phone);
    }

    /// <summary>
    /// P-12's two buckets, spent together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the second of two gates, and it is not redundant.</b> ride-svc counts the request
    /// rows inside the transaction that inserts the next one (its own C037 note explains why
    /// Postgres rather than Redis over there); this one bounds the *pushes*, which is what P-12's
    /// "5/hour, 30/day per booker" is about from the rider's side. The token is spent after the
    /// dedupe claim, so a redelivered <c>location.request.issued</c> costs nothing — without that
    /// ordering, at-least-once delivery would quietly turn five requests an hour into four.
    /// </para>
    /// <para>
    /// The hourly bucket is spent first and the daily one only if it passed, so a booker who is out
    /// of hourly requests does not also burn a daily token. Both are the kernel's
    /// <c>RateLimitPolicies</c>, declared by C002 for exactly this component.
    /// </para>
    /// </remarks>
    private async Task<string?> IsRateLimitedAsync(
        NotificationTypeSpec spec, NotificationRequest request, CancellationToken cancellationToken)
    {
        if (!_options.LocationRequestLimitsEnabled
            || !string.Equals(spec.Type, NotificationCatalogue.LocationRequest, StringComparison.Ordinal))
        {
            return null;
        }

        if (request.RateLimitSubject is not { } booker)
        {
            // A location request with no booker cannot be metered, and metering is the point of the
            // rule. Refusing is the safe direction: P-12 is a privacy control, not a cost control.
            logger.LogWarning(
                "A {Type} arrived with no booker to meter (dedupe {Dedupe}); refusing it (P-12).",
                spec.Type, request.DedupeKey);

            return "no rate-limit subject (P-12)";
        }

        var subject = booker.ToString();

        var hourly = await rateLimiter.TryAcquireAsync(
            RateLimitPolicies.LocationRequestHourly, subject, cancellationToken: cancellationToken);

        if (!hourly.Allowed)
        {
            logger.LogInformation(
                "Booker {Booker} is over the P-12 hourly location-request limit; no data message is sent.", booker);

            return "P-12: 5 location requests per hour";
        }

        var daily = await rateLimiter.TryAcquireAsync(
            RateLimitPolicies.LocationRequestDaily, subject, cancellationToken: cancellationToken);

        if (!daily.Allowed)
        {
            logger.LogInformation(
                "Booker {Booker} is over the P-12 daily location-request limit; no data message is sent.", booker);

            return "P-12: 30 location requests per day";
        }

        return null;
    }
}

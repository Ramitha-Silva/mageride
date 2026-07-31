using System.Security.Cryptography;
using System.Text;
using MageRide.Notification.Configuration;
using MageRide.Notification.Domain;
using MageRide.Notification.Persistence;
using MageRide.Notification.Sending;
using MageRide.Shared.Errors;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Endpoints;

/// <summary>`POST /v1/internal/notify/send`.</summary>
/// <param name="Recipients">Account ids. Either this or <paramref name="Phones"/> must be present.</param>
/// <param name="Phones">
/// <b>Δ C051.</b> E.164 numbers, for the recipients who have no account. The contract's original
/// shape took user ids only, which cannot express the two messages the specs are most explicit
/// about: D-33's SOS goes to an <em>emergency contact</em> (<c>iam.users.emergency_contact_phone</c>,
/// a number that is nobody's account) and AL-21's package link goes to an unregistered recipient.
/// </param>
/// <param name="Audience">
/// <b>Δ C051.</b> US-14.8's broadcast, which has a reader in <c>content.broadcasts</c> and had no
/// way to reach a handset. Bounded by <c>Notification:MaxBroadcastRecipients</c>, and truncation is
/// logged.
/// </param>
/// <param name="NotificationType">
/// <b>Δ C051.</b> D5' §14.4's Type — which decides the channel, the priority and whether the
/// recipient may mute it. The contract carried <c>templateKey</c> alone, which cannot answer any of
/// those three questions: a key is wording, a type is behaviour.
/// </param>
public sealed record SendNotificationBody(
    string? TemplateKey,
    string? NotificationType,
    IReadOnlyList<string>? Recipients,
    IReadOnlyList<string>? Phones,
    AudienceBody? Audience,
    IReadOnlyDictionary<string, string>? Data);

/// <summary>Who a broadcast is for. Only the facts a bearer carries (content-svc's rule).</summary>
public sealed record AudienceBody(string? Role);

/// <summary>The 202 of `POST /v1/internal/notify/send`.</summary>
/// <param name="Accepted">Recipients queued for delivery.</param>
/// <param name="Suppressed">Refused by a preference (US-10.7) or a limit (P-12).</param>
/// <param name="Undeliverable">Recipients with no account, no number and no device.</param>
public sealed record SendNotificationResponse(
    Guid DispatchId, int Accepted, int Suppressed, int Undeliverable);

/// <summary>
/// <c>/v1/internal/notify/**</c> — the entry point every other service fans out through.
/// </summary>
/// <remarks>
/// <b>Unset <c>Notification:InternalApiKey</c> leaves this family unmapped</b>, the same posture
/// ride-svc and registry-svc take and the opposite of content-svc's template read. The difference is
/// what the route does: content-svc serves wording, and an open send endpoint is a free SMS gateway
/// and a free push channel into every handset on the platform. Announced loudly at start-up, because
/// with it unmapped fare-svc's payment receipts, safety-svc's SOS and admin-bff's announcements all
/// answer 404 and nothing else changes.
/// </remarks>
public static class InternalNotifyEndpoints
{
    /// <summary>The guard header, matching every other internal plane on the platform (C008).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalNotifyEndpoints(
        this IEndpointRouteBuilder endpoints, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var internalNotify = endpoints.MapGroup("/v1/internal/notify")
            .WithTags("notify")
            .AllowAnonymous()
            .AddEndpointFilter(new InternalKeyFilter(apiKey));

        internalNotify.MapPost("/send", SendAsync).WithName("sendNotification");

        return endpoints;
    }

    private static async Task<Accepted<SendNotificationResponse>> SendAsync(
        SendNotificationBody? body,
        INotificationService notifications,
        IRecipientRepository recipients,
        IOptions<NotificationOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var settings = options.Value;
        var logger = loggerFactory.CreateLogger(typeof(InternalNotifyEndpoints));

        var type = body?.NotificationType?.Trim();

        if (string.IsNullOrWhiteSpace(type) || !NotificationCatalogue.TryGet(type, out var spec))
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["notificationType"] =
                    [
                        "notificationType must be one of D5' §14.4's types; every one is declared in " +
                        "NotificationCatalogue.",
                    ],
                },
                "Unknown or missing notificationType.");
        }

        var userIds = ParseIds(body?.Recipients);
        var phones = Normalise(body?.Phones);

        if (body?.Audience is { } audience)
        {
            var found = await recipients.ListAudienceAsync(
                audience.Role?.Trim(), settings.MaxBroadcastRecipients + 1, cancellationToken);

            if (found.Count > settings.MaxBroadcastRecipients)
            {
                // No silent caps. A broadcast that reached nine tenths of the platform looks exactly
                // like one that reached all of it.
                logger.LogWarning(
                    "A broadcast audience ({Role}) matched more than Notification:MaxBroadcastRecipients ({Max}); "
                    + "the remainder is not notified.",
                    audience.Role ?? "everybody",
                    settings.MaxBroadcastRecipients);

                found = [.. found.Take(settings.MaxBroadcastRecipients)];
            }

            userIds = [.. userIds.Concat(found).Distinct()];
        }

        if (userIds.Count + phones.Count == 0)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["recipients"] = ["At least one recipient, phone number or audience is required."],
                },
                "The send has no recipients.");
        }

        if (userIds.Count + phones.Count > settings.MaxRecipientsPerSend && body?.Audience is null)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["recipients"] = [$"At most {settings.MaxRecipientsPerSend} recipients per send."],
                },
                "Too many recipients.");
        }

        // The dispatch id is a handle for the whole fan-out, and it is also what makes each
        // recipient's dedupe key unique — a caller that retries with the same Idempotency-Key is
        // replayed by the kernel's command log, and one that retries with a new key gets a new
        // dispatch. That is the honest behaviour: this service cannot know whether two identical
        // sends are a mistake or two genuine reminders.
        var dispatchId = Guid.NewGuid();
        var values = body?.Data ?? PayloadValues.Empty;

        var accepted = 0;
        var suppressed = 0;
        var undeliverable = 0;

        foreach (var userId in userIds)
        {
            var receipt = await notifications.EnqueueAsync(
                new NotificationRequest(
                    Type: spec.Type,
                    DedupeKey: NotificationDedupe.For("send", dispatchId.ToString(), spec.Type, userId),
                    UserId: userId,
                    Values: values,
                    TemplateKeyOverride: body?.TemplateKey?.Trim()),
                cancellationToken);

            Count(receipt, ref accepted, ref suppressed, ref undeliverable);
        }

        foreach (var phone in phones)
        {
            // A number that belongs to an account is resolved to it, so the message carries the
            // owner's language and their preferences apply. AL-21's branch is exactly this question
            // asked of a package recipient.
            var known = await recipients.FindByPhoneAsync(phone, cancellationToken);

            var receipt = await notifications.EnqueueAsync(
                new NotificationRequest(
                    Type: spec.Type,
                    DedupeKey: NotificationDedupe.For("send", dispatchId.ToString(), spec.Type)
                               + ":" + Fingerprint(phone),
                    UserId: known?.UserId,
                    Phone: phone,
                    Values: values,
                    TemplateKeyOverride: body?.TemplateKey?.Trim()),
                cancellationToken);

            Count(receipt, ref accepted, ref suppressed, ref undeliverable);
        }

        return TypedResults.Accepted(
            (string?)null, new SendNotificationResponse(dispatchId, accepted, suppressed, undeliverable));
    }

    private static void Count(
        NotificationReceipt receipt, ref int accepted, ref int suppressed, ref int undeliverable)
    {
        switch (receipt.Outcome)
        {
            case NotificationOutcome.Queued:
            case NotificationOutcome.AlreadyClaimed:
                accepted++;
                break;
            case NotificationOutcome.Suppressed:
                suppressed++;
                break;
            default:
                undeliverable++;
                break;
        }
    }

    private static IReadOnlyList<Guid> ParseIds(IReadOnlyList<string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return [];
        }

        var ids = new List<Guid>(values.Count);

        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var id))
            {
                throw new MageRideValidationException(
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["recipients"] = [$"'{value}' is not a user id."],
                    },
                    "A recipient is not a user id.");
            }

            ids.Add(id);
        }

        return [.. ids.Distinct()];
    }

    private static IReadOnlyList<string> Normalise(IReadOnlyList<string>? phones) =>
        phones is not { Count: > 0 }
            ? []
            : [.. phones.Select(static phone => phone.Trim()).Where(static phone => phone.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// A short digest of a number, for the dedupe key.
    /// </summary>
    /// <remarks>
    /// The key is a column in a table with a longer retention than the message it identifies, and it
    /// is read by anybody debugging the queue. Putting a raw MSISDN in it would spread PII across
    /// a second column for no gain — the fan-out only needs the parts to be distinct.
    /// </remarks>
    private static string Fingerprint(string phone) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(phone)))[..16];
}

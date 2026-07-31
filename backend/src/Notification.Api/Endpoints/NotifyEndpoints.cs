using System.Text.Json.Serialization;
using MageRide.Notification.Domain;
using MageRide.Notification.Persistence;
using MageRide.Notification.Push;
using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Http.Idempotency;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Notification.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/notification.yaml. The contract wins over this file: it is
// what C012/C013 generate the KMP client from and what C118 asserts the running service against.
//
// Note what is NOT here: there is no shape in this file, or anywhere in this assembly, that can
// carry a share token. AL-44/AL-45's fence is that a token is minted server-side and SMSed, never
// returned to a client — see Tokens/ShareTokenMinter.
// =============================================================================================

/// <summary>`POST /v1/notify/register-token`.</summary>
public sealed record RegisterTokenBody(string? Token, string? Platform, string? DeviceId);

/// <summary>`PUT /v1/notify/preferences` (US-10.7).</summary>
/// <remarks>
/// The converter is not decoration. Without it the kernel's camelCase dictionary-key policy answers
/// a request that muted <c>LOW_BALANCE</c> with <c>loW_BALANCE</c>, and a client that sends back
/// what it was given has a key that matches no notification type. See
/// <see cref="LiteralKeyDictionaryConverter"/>.
/// </remarks>
public sealed record PreferencesBody(
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter))]
    IReadOnlyDictionary<string, bool>? Preferences);

/// <summary>The effective switches after a write.</summary>
/// <inheritdoc cref="PreferencesBody" path="/remarks"/>
public sealed record PreferencesResponse(
    [property: JsonConverter(typeof(LiteralKeyDictionaryConverter))]
    IReadOnlyDictionary<string, bool> Preferences);

/// <summary>`POST /v1/notify/ack` — Δ C051, see the remarks on <see cref="NotifyEndpoints"/>.</summary>
public sealed record AckBody(string? NotificationId);

/// <summary>
/// The three bearer routes: register a device, set per-type preferences, acknowledge an offer push.
/// </summary>
/// <remarks>
/// <b><c>POST /v1/notify/ack</c> is a Δ C051 addition to the contract, and E-01 is unimplementable
/// without it.</b> D6' §7.4 says "3 s no-ack → SMS fallback", which requires the handset to be able
/// to say it woke up — and neither D3' nor <c>notification.yaml</c> declared a route for it. With no
/// ack path, every offer push on the platform would fall back to SMS after three seconds, which is
/// both the wrong behaviour and a bill. Raised as a micro-change-set in the C051 handoff.
/// </remarks>
public static class NotifyEndpoints
{
    public static IEndpointRouteBuilder MapNotifyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var notify = endpoints.MapGroup("/v1/notify").WithTags("notify").RequireAuthorization();

        notify.MapPost("/register-token", RegisterTokenAsync).WithName("registerPushToken");
        notify.MapPut("/preferences", UpdatePreferencesAsync).WithName("updateNotificationPreferences");

        // Idempotency-exempt, and it is the one route on this service where that is not a
        // convenience: the ack races a three-second deadline, and answering `400
        // idempotency-key-required` to a handset that woke up in time would fire the SMS fallback
        // for a driver who was there. It is idempotent by construction anyway — the guarded UPDATE
        // matches once — which is what `x-idempotency-exempt` means in this repo.
        notify.MapPost("/ack", AckAsync)
            .AllowMissingIdempotencyKey()
            .WithName("acknowledgeNotification");

        return endpoints;
    }

    private static async Task<NoContent> RegisterTokenAsync(
        RegisterTokenBody? body,
        HttpContext context,
        IDeviceTokenRepository devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(devices);

        var token = body?.Token?.Trim();
        var platform = body?.Platform?.Trim().ToLowerInvariant();

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(token))
        {
            errors["token"] = ["A registration token is required."];
        }
        else if (token.Length > 512)
        {
            errors["token"] = ["A registration token is at most 512 characters."];
        }

        if (platform is not (DevicePlatforms.Android or DevicePlatforms.Ios))
        {
            errors["platform"] = ["platform must be 'android' or 'ios'."];
        }

        if (body?.DeviceId is { Length: > 128 })
        {
            errors["deviceId"] = ["deviceId is at most 128 characters."];
        }

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors, "The token registration is not valid.");
        }

        await devices.UpsertAsync(
            context.User.RequireSubjectId(), platform!, token!, body?.DeviceId?.Trim(), cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Ok<PreferencesResponse>> UpdatePreferencesAsync(
        PreferencesBody? body,
        HttpContext context,
        IRecipientRepository recipients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recipients);

        if (body?.Preferences is not { Count: > 0 } requested)
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["preferences"] = ["At least one switch is required."],
                },
                "The body must carry at least one notification-type switch.");
        }

        var accepted = new Dictionary<string, bool>(StringComparer.Ordinal);
        var ignored = new List<string>();

        foreach (var (type, enabled) in requested)
        {
            // Two rejections, and they are different facts. An unknown type is a client bug and is
            // refused loudly, because storing it would grow the column with keys nothing resolves.
            // A safety-critical type is *silently ignored* — `notification.yaml` says so — because
            // the contract promises the write succeeds and the switch does not take effect, which is
            // exactly what iam-svc's `PUT /v1/users/me` does with the same three keys.
            if (!NotificationCatalogue.TryGet(type, out _))
            {
                throw new MageRideValidationException(
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["preferences"] = [$"Unknown notification type '{type}'."],
                    },
                    $"'{type}' is not a notification type this platform sends.");
            }

            if (NotificationCatalogue.SafetyCritical.Contains(type))
            {
                ignored.Add(type);
                continue;
            }

            accepted[type] = enabled;
        }

        var effective = accepted.Count > 0
            ? await recipients.UpdatePreferencesAsync(context.User.RequireSubjectId(), accepted, cancellationToken)
            : (await recipients.FindAsync(context.User.RequireSubjectId(), cancellationToken))?.Preferences
              ?? NotificationRecipient.NoPreferences;

        // The response is what is *in force*, not what was asked for, so a client that muted
        // RIDE_CANCELLED can see that it did not take.
        var answer = new Dictionary<string, bool>(effective, StringComparer.Ordinal);

        foreach (var type in ignored)
        {
            answer[type] = true;
        }

        return TypedResults.Ok(new PreferencesResponse(answer));
    }

    private static async Task<Results<NoContent, NotFound>> AckAsync(
        AckBody? body,
        HttpContext context,
        INotificationRepository notifications,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(clock);

        if (!Guid.TryParse(body?.NotificationId, out var id))
        {
            throw new MageRideValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["notificationId"] = ["A notification id is required."],
                },
                "notificationId must be the id carried on the push.");
        }

        // The guard is in the statement: it matches only a `Sent` notification belonging to the
        // caller. A late ack finds a row that has already fallen back and answers 404 — which is
        // honest, because there is nothing left to acknowledge.
        var acked = await notifications.TryAckAsync(
            id, context.User.RequireSubjectId(), clock.GetUtcNow(), cancellationToken);

        return acked ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}

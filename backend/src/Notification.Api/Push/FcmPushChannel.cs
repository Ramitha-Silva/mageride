using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Notification.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Push;

/// <summary>
/// FCM HTTP v1 — the Android transport (D6' §7.4).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>android.priority = "high"</c> is E-01's whole point.</b> A normal-priority message is
/// collapsed and held by Doze on an idle handset, which on a dispatch offer means a driver who
/// learns about a fare after it has been given to somebody else. High priority bypasses it, and
/// Google rate-limits an app that overuses it — which is why exactly two types in
/// <c>NotificationCatalogue</c> carry it.
/// </para>
/// <para>
/// <b>A silent message carries <c>data</c> and no <c>notification</c>.</b> Including both would make
/// Android draw a tray entry the app has already drawn itself, and — worse for E-01 — a message
/// with a <c>notification</c> member is delivered to the system tray rather than to the app when it
/// is backgrounded, so the ack that stops the SMS fallback would never be sent.
/// </para>
/// <para>
/// <b>404 <c>UNREGISTERED</c> is not a failure to retry.</b> It is FCM saying the app was
/// uninstalled or the token replaced; the row is deleted, because every subsequent offer would
/// otherwise fan out to it for ever (the same reason 1302 carries <c>ux_notif_tokens_token</c>).
/// </para>
/// </remarks>
public sealed class FcmPushChannel(
    IHttpClientFactory clients,
    GoogleAccessTokenSource tokens,
    IOptions<NotificationOptions> options,
    ILogger<FcmPushChannel> logger) : IPushChannel
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "fcm";

    private readonly NotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Platform => DevicePlatforms.Android;

    public string Provider => "fcm";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.FcmProjectId) && tokens.IsConfigured;

    public async Task<PushResult> SendAsync(
        DeviceToken device, PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(message);

        if (!IsConfigured)
        {
            return PushResult.Failed(Provider, "FCM is not configured (Notification:FcmProjectId / service account).");
        }

        var accessToken = await tokens.GetAsync(cancellationToken);

        var body = new
        {
            message = new
            {
                token = device.Token,

                // Omitted entirely on a silent message — see the remarks.
                notification = message.Silent
                    ? null
                    : new { title = message.Title, body = message.Body },

                data = message.Data,

                android = new
                {
                    priority = message.IsHighPriority ? "high" : "normal",

                    // The offer is worthless once its 15 s window closes, so it is given a TTL and
                    // not stored for a handset that comes back tomorrow. Everything else takes
                    // FCM's default four-week store.
                    ttl = message.IsHighPriority && message.Silent ? "60s" : null,
                },
            },
        };

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1/projects/{_options.FcmProjectId}/messages:send")
        {
            Content = JsonContent.Create(body, options: FcmJson),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return PushResult.Ok(Provider, NameOf(text));
            }

            if (IsDeadToken(response.StatusCode, text))
            {
                logger.LogInformation(
                    "FCM reports token {TokenId} is no longer registered; dropping it.", device.Id);

                return PushResult.Dead(Provider, $"FCM {(int)response.StatusCode}: token unregistered");
            }

            return PushResult.Failed(Provider, $"FCM answered {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return PushResult.Failed(Provider, $"FCM was unreachable: {exception.Message}");
        }
    }

    /// <summary>
    /// <c>null</c> members are dropped so an absent <c>notification</c> and an absent <c>ttl</c> are
    /// absent on the wire — FCM rejects <c>"notification": null</c> outright.
    /// </summary>
    private static readonly JsonSerializerOptions FcmJson = new(MageRideJson.Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The <c>name</c> of the created message: <c>projects/{id}/messages/{messageId}</c>.</summary>
    private static string? NameOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// FCM signals a retired token as <c>404 UNREGISTERED</c>, and a malformed one as
    /// <c>400 INVALID_ARGUMENT</c>. Both mean this handle will never work again; every other status
    /// is transient as far as this service is concerned.
    /// </summary>
    private static bool IsDeadToken(HttpStatusCode status, string body) =>
        status == HttpStatusCode.NotFound
        || (status == HttpStatusCode.BadRequest
            && body.Contains("INVALID_ARGUMENT", StringComparison.Ordinal)
            && body.Contains("token", StringComparison.OrdinalIgnoreCase));
}

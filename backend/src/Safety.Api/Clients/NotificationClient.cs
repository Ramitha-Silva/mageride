using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Safety.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Safety.Clients;

/// <summary>What the D-33 dispatch achieved, as far as this service can record it.</summary>
/// <param name="Gateways">
/// Every gateway the alert was handed to. Two on a parallel send, whether or not both answered —
/// which is the fact <c>safety.sos_events</c> keeps one column each for.
/// </param>
public sealed record SosDispatchResult(
    bool Dispatched, IReadOnlyList<string> Gateways, string? Provider, string? Error)
{
    public static SosDispatchResult Failed(string error) => new(false, [], null, error);
}

/// <summary>notification-svc, as far as safety-svc needs it.</summary>
public interface INotificationClient
{
    /// <summary>
    /// Sends the SOS alert and waits for the gateways.
    /// </summary>
    /// <remarks>
    /// <b>Synchronous, and D-33 is why.</b> Every other notification on the platform is accepted
    /// asynchronously and drained by notification-svc's D-27 worker; an SOS is dispatched on this
    /// call, so the five-second budget does not depend on how many ride offers are queued in front
    /// of it, and so this service has a per-gateway outcome to record at all. notification-svc keys
    /// that behaviour off the notification *type* (`SOS_TRIGGERED` is the platform's only
    /// dual-gateway type), not off anything this caller says.
    /// </remarks>
    Task<SosDispatchResult> SendSosAsync(
        string phone, string raiserName, string trackingLink, CancellationToken cancellationToken);

    /// <summary>True when a base address and key are configured well enough to try.</summary>
    bool IsConfigured { get; }
}

/// <inheritdoc cref="INotificationClient"/>
internal sealed class NotificationClient(
    IHttpClientFactory clients,
    IOptions<SafetyOptions> options,
    ILogger<NotificationClient> logger) : INotificationClient
{
    /// <summary>The named client the timeout is attached to.</summary>
    public const string HttpClientName = "notification-svc";

    /// <summary>Matches notification-svc's <c>InternalNotifyEndpoints.ApiKeyHeader</c>.</summary>
    private const string ApiKeyHeader = "X-MageRide-Internal-Key";

    /// <summary>D5' §14.4's type. The one the catalogue marks `DualGateway` (D-33).</summary>
    private const string SosType = "SOS_TRIGGERED";

    private readonly SafetyOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.NotificationBaseUrl);

    public async Task<SosDispatchResult> SendSosAsync(
        string phone, string raiserName, string trackingLink, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        if (!IsConfigured)
        {
            return SosDispatchResult.Failed(
                "Safety:NotificationBaseUrl is not configured, so no SOS can be dispatched.");
        }

        var client = clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/notify/send")
        {
            Content = JsonContent.Create(
                new
                {
                    notificationType = SosType,

                    // A number, not an account: AL-13's emergency contact is somebody the platform
                    // has no user row for, and D-33 has to reach them anyway.
                    phones = new[] { phone },
                    data = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["name"] = raiserName,
                        ["link"] = trackingLink,
                    },
                },
                options: MageRideJson.Options),
        };

        if (!string.IsNullOrWhiteSpace(_options.NotificationInternalApiKey))
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.NotificationInternalApiKey);
        }

        // A fresh key per alert. The R-14 replay is *this service's* job (safety.command_log on
        // POST /v1/sos): a retried SOS that got as far as the gateway must not be deduped away by
        // the callee, because the caller is the one that knows whether the button was pressed twice.
        request.Headers.TryAddWithoutValidation(MageRideHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "notification-svc answered {Status} for an SOS dispatch: {Body}", (int)response.StatusCode, body);

                return SosDispatchResult.Failed($"notification-svc answered {(int)response.StatusCode}.");
            }

            return Parse(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "notification-svc was unreachable for an SOS dispatch.");
            return SosDispatchResult.Failed($"notification-svc was unreachable: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads the inline-delivery block notification-svc returns for a type with a latency SLO.
    /// </summary>
    /// <remarks>
    /// An answer with no <c>deliveries</c> means the alert was queued rather than dispatched — which
    /// is what a notification-svc that has not been upgraded would do. It is reported as
    /// undispatched rather than assumed sent: an SOS this service cannot prove went out is one an
    /// operator should see as unproven.
    /// </remarks>
    private SosDispatchResult Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("deliveries", out var deliveries)
                || deliveries.ValueKind != JsonValueKind.Array
                || deliveries.GetArrayLength() == 0)
            {
                logger.LogWarning(
                    "notification-svc accepted the SOS but reported no inline delivery; it was queued, so the "
                    + "D-33 p99 is not this service's to promise. Response: {Body}", body);

                return SosDispatchResult.Failed("accepted for queued delivery; no gateway outcome reported");
            }

            var delivery = deliveries[0];

            var status = delivery.TryGetProperty("status", out var s) ? s.GetString() : null;
            var provider = delivery.TryGetProperty("provider", out var p) ? p.GetString() : null;
            var error = delivery.TryGetProperty("error", out var e) ? e.GetString() : null;

            var gateways = delivery.TryGetProperty("gateways", out var g) && g.ValueKind == JsonValueKind.Array
                ? g.EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray()
                : [];

            // `Sent` is notification-svc's terminal for a message a gateway took. Anything else —
            // Pending on a retry schedule, Failed, Suppressed — is not a dispatch.
            return new SosDispatchResult(
                string.Equals(status, "Sent", StringComparison.Ordinal), gateways, provider, error);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "notification-svc returned an unreadable SOS dispatch result.");
            return SosDispatchResult.Failed("notification-svc returned an unreadable result");
        }
    }
}

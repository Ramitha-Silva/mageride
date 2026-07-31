using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MageRide.Notification.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Push;

/// <summary>
/// APNs HTTP/2 — the iOS transport (D6' §7.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP/2 is a requirement, not a preference.</b> APNs speaks nothing else, and .NET will happily
/// negotiate 1.1 against a mock, so the client pins <c>RequestVersionExact</c>: a deployment whose
/// proxy downgrades the connection fails loudly instead of silently never delivering.
/// </para>
/// <para>
/// <b>E-01's offer is <c>apns-priority: 10</c> plus <c>content-available: 1</c> and no alert</b> —
/// D6' §7.4 spells out that combination, and it is what wakes a backgrounded driver app so it can
/// ack inside three seconds. An alert payload would draw a banner and hand nothing to the app.
/// </para>
/// <para>
/// <b>The provider token is an ES256 JWT, refreshed on a schedule Apple sets.</b> They reject a
/// token older than an hour and rate-limit an issuer that mints one per request, so it is cached for
/// fifty minutes behind a semaphore — the same shape as the FCM exchange, and for the same reason.
/// </para>
/// </remarks>
public sealed class ApnsPushChannel : IPushChannel, IDisposable
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "apns";

    /// <summary>Apple rejects a provider token older than an hour; mint a fresh one before that.</summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);

    private readonly IHttpClientFactory _clients;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApnsPushChannel> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _providerToken;
    private DateTimeOffset _mintedAt;

    public ApnsPushChannel(
        IHttpClientFactory clients,
        IOptions<NotificationOptions> options,
        TimeProvider clock,
        ILogger<ApnsPushChannel> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Platform => DevicePlatforms.Ios;

    public string Provider => "apns";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ApnsKeyId)
        && !string.IsNullOrWhiteSpace(_options.ApnsTeamId)
        && !string.IsNullOrWhiteSpace(_options.ApnsPrivateKeyPem)
        && !string.IsNullOrWhiteSpace(_options.ApnsTopic);

    public async Task<PushResult> SendAsync(
        DeviceToken device, PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(message);

        if (!IsConfigured)
        {
            return PushResult.Failed(Provider, "APNs is not configured (Notification:Apns* / topic).");
        }

        var aps = message.Silent
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["content-available"] = 1 }
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["alert"] = new { title = message.Title, body = message.Body },
                ["sound"] = "default",
            };

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["aps"] = aps };

        foreach (var (key, value) in message.Data)
        {
            // `aps` is Apple's; everything else in the dictionary is the app's, which is how the
            // deep link and the request id reach it. A data key called `aps` would shadow the
            // envelope, so it is refused rather than merged.
            if (!string.Equals(key, "aps", StringComparison.Ordinal))
            {
                payload[key] = value;
            }
        }

        var client = _clients.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"3/device/{device.Token}")
        {
            Content = JsonContent.Create(payload, options: MageRideJson.Options),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        request.Headers.TryAddWithoutValidation("authorization", $"bearer {await ProviderTokenAsync(cancellationToken)}");
        request.Headers.TryAddWithoutValidation("apns-topic", _options.ApnsTopic);
        request.Headers.TryAddWithoutValidation("apns-push-type", message.Silent ? "background" : "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", message.IsHighPriority ? "10" : "5");

        // The offer expires with its 15 s window; a stored copy delivered later is an offer for a
        // ride somebody else is already driving. `0` means "deliver now or discard".
        if (message.IsHighPriority && message.Silent)
        {
            request.Headers.TryAddWithoutValidation(
                "apns-expiration", _clock.GetUtcNow().AddSeconds(60).ToUnixTimeSeconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var id = response.Headers.TryGetValues("apns-id", out var values) ? values.FirstOrDefault() : null;
                return PushResult.Ok(Provider, id);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (IsDeadToken(response.StatusCode, text))
            {
                _logger.LogInformation("APNs reports token {TokenId} is no longer valid; dropping it.", device.Id);
                return PushResult.Dead(Provider, $"APNs {(int)response.StatusCode}: {ReasonOf(text)}");
            }

            return PushResult.Failed(Provider, $"APNs answered {(int)response.StatusCode}: {ReasonOf(text)}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return PushResult.Failed(Provider, $"APNs was unreachable: {exception.Message}");
        }
    }

    private async Task<string> ProviderTokenAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        if (_providerToken is not null && now - _mintedAt < TokenLifetime)
        {
            return _providerToken;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            now = _clock.GetUtcNow();

            if (_providerToken is not null && now - _mintedAt < TokenLifetime)
            {
                return _providerToken;
            }

            var header = JwtCodec.Segment(new { alg = "ES256", kid = _options.ApnsKeyId });
            var claims = JwtCodec.Segment(new { iss = _options.ApnsTeamId, iat = now.ToUnixTimeSeconds() });
            var signingInput = $"{header}.{claims}";

            using var key = ECDsa.Create();
            key.ImportFromPem(_options.ApnsPrivateKeyPem);

            // IeeeP1363 (r‖s), not DER: that is what JWS ES256 specifies, and .NET's default for
            // SignData is DER — a signature Apple rejects with a 403 that says nothing useful.
            var signature = key.SignData(
                Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            _providerToken = $"{signingInput}.{JwtCodec.Base64Url(signature)}";
            _mintedAt = now;

            return _providerToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <c>410 Unregistered</c> is Apple's "the app is gone"; <c>400 BadDeviceToken</c> is a token
    /// that never addressed this topic. Neither improves on a retry.
    /// </summary>
    private static bool IsDeadToken(HttpStatusCode status, string body) =>
        status == HttpStatusCode.Gone
        || (status == HttpStatusCode.BadRequest && ReasonOf(body) is "BadDeviceToken" or "DeviceTokenNotForTopic");

    private static string ReasonOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no reason";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("reason", out var reason)
                ? reason.GetString() ?? "no reason"
                : "no reason";
        }
        catch (JsonException)
        {
            return "unparseable";
        }
    }

    public void Dispose() => _gate.Dispose();
}

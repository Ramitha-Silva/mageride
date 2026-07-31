using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Notification.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Notification.Sms;

/// <summary>What one gateway did with one message.</summary>
public sealed record SmsResult(bool Delivered, string Gateway, string? MessageId, string? Error)
{
    public static SmsResult Ok(string gateway, string? messageId = null) => new(true, gateway, messageId, null);

    public static SmsResult Failed(string gateway, string error) => new(false, gateway, null, error);
}

/// <summary>One SMS transport (D6' §7.3).</summary>
public interface ISmsGateway
{
    /// <summary>Short name, recorded on the row and in the logs.</summary>
    string Name { get; }

    /// <summary>True when the gateway has enough configuration to be worth trying.</summary>
    bool IsConfigured { get; }

    Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken);
}

/// <summary>Gateway names, so the primary/secondary vocabulary is spelled once.</summary>
public static class SmsGatewayNames
{
    public const string NotifyLk = "notifylk";
    public const string Secondary = "secondary";
    public const string Dev = "dev";
}

/// <summary>
/// Notify.lk REST — D6' §7.3's primary ("~Rs 0.50–1.50 per SMS — OTP, transactional, low-balance").
/// </summary>
/// <remarks>
/// <para>
/// One form POST to <c>/send</c>. <b>Their API answers HTTP 200 with <c>{"status":"error"}</c></b>
/// for a rejected send — an unregistered sender mask, an exhausted balance — so the status line
/// alone is not the outcome and the body has to be read. iam-svc's <c>NotifyLkOtpSender</c> learned
/// the same thing (C026); the parse is repeated here rather than shared because the two services do
/// not reference each other and the C051 handoff proposes folding both into the kernel.
/// </para>
/// <para>
/// <c>to</c> is the national form with no <c>+</c>. The platform stores E.164 everywhere else, so
/// the conversion happens here, at the boundary that wants the other spelling.
/// </para>
/// </remarks>
public sealed class NotifyLkSmsGateway(
    IHttpClientFactory clients,
    IOptions<SmsOptions> options,
    ILogger<NotifyLkSmsGateway> logger) : ISmsGateway
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "sms-notifylk";

    private readonly SmsOptions _sms = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Name => SmsGatewayNames.NotifyLk;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_sms.NotifyLkUserId) && !string.IsNullOrWhiteSpace(_sms.NotifyLkApiKey);

    public async Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!IsConfigured)
        {
            return SmsResult.Failed(Name, "Notify.lk is not configured (Sms:NotifyLkUserId / Sms:NotifyLkApiKey).");
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user_id"] = _sms.NotifyLkUserId!,
            ["api_key"] = _sms.NotifyLkApiKey!,
            ["sender_id"] = _sms.NotifyLkSenderId,
            ["to"] = ToNationalDigits(phone),
            ["message"] = message,
        };

        var client = clients.CreateClient(HttpClientName);

        try
        {
            using var response = await client.PostAsync("send", new FormUrlEncodedContent(form), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return SmsResult.Failed(Name, $"Notify.lk answered {(int)response.StatusCode}.");
            }

            if (!IsAccepted(body, out var reported))
            {
                // The body is logged and kept out of the result: their error strings can carry the
                // destination number, and the result is written to `last_error` on a row.
                logger.LogError("Notify.lk refused the send for {Phone}: {Body}", Redact(phone), body);
                return SmsResult.Failed(Name, $"Notify.lk refused the send ({reported}).");
            }

            return SmsResult.Ok(Name, MessageIdOf(body));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return SmsResult.Failed(Name, $"Notify.lk was unreachable: {exception.Message}");
        }
    }

    /// <summary><c>+94771234567</c> → <c>94771234567</c>. Notify.lk rejects the leading <c>+</c>.</summary>
    internal static string ToNationalDigits(string e164) => e164.TrimStart('+');

    /// <summary>
    /// Success is <c>{"status":"success","data":{…}}</c>; failure comes back as
    /// <c>{"status":"error","message":"…"}</c> under the same 200.
    /// </summary>
    internal static bool IsAccepted(string body, out string reported)
    {
        reported = "unparseable response";

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("status", out var status))
            {
                return false;
            }

            reported = status.GetString() ?? "no status";
            return string.Equals(reported, "success", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? MessageIdOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("data", out var data)
                   && data.TryGetProperty("message_id", out var id)
                ? id.ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Last four digits only. A log line is not a place for a full MSISDN (§0 PII).</summary>
    internal static string Redact(string phone) =>
        phone.Length <= 4 ? "****" : string.Create(CultureInfo.InvariantCulture, $"****{phone[^4..]}");
}

/// <summary>
/// A generic HTTP gateway — the "secondary gateway (Dialog/Mobitel)" of D6' §7.3.
/// </summary>
/// <remarks>
/// <b>No spec prints the secondary provider's request shape</b>, and the two candidates D6' names do
/// not share one. What every one of them accepts is a JSON POST of a destination and a body under a
/// bearer credential, so that is what this sends; a deployment whose gateway wants something else
/// replaces this class rather than reconfiguring it. Recorded as a spec gap in the C026 handoff and
/// again in C051's — this is the second copy of the same guess, and D-33 makes it load-bearing
/// rather than a fallback, because an SOS goes through both gateways at once.
/// </remarks>
public sealed class SecondarySmsGateway(
    IHttpClientFactory clients, IOptions<SmsOptions> options) : ISmsGateway
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "sms-secondary";

    private readonly SmsOptions _sms = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string Name => SmsGatewayNames.Secondary;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_sms.SecondaryGateway);

    public async Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!IsConfigured)
        {
            return SmsResult.Failed(Name, "No secondary SMS gateway is configured (Sms:SecondaryGateway).");
        }

        var client = clients.CreateClient(HttpClientName);

        try
        {
            using var response = await client.PostAsJsonAsync(
                string.Empty,
                new
                {
                    to = phone,
                    from = _sms.SecondarySenderId ?? _sms.NotifyLkSenderId,
                    message,
                },
                cancellationToken);

            return response.IsSuccessStatusCode
                ? SmsResult.Ok(Name)
                : SmsResult.Failed(Name, $"The secondary SMS gateway answered {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return SmsResult.Failed(Name, $"The secondary SMS gateway was unreachable: {exception.Message}");
        }
    }
}

/// <summary>The dev gateway: writes the message to the log instead of sending it.</summary>
/// <remarks>
/// Guarded the same way iam-svc guards its own (<c>Sms:AllowDevSenderOutsideDevelopment</c>). It
/// puts a share token — the whole credential for an unauthenticated tracking page — in plaintext in
/// a log, which is exactly why it is refused outside Development by default.
/// </remarks>
public sealed class LoggingSmsGateway(ILogger<LoggingSmsGateway> logger) : ISmsGateway
{
    public string Name => SmsGatewayNames.Dev;

    public bool IsConfigured => true;

    public Task<SmsResult> SendAsync(string phone, string message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[dev-sms] to {Phone}: {Message}", NotifyLkSmsGateway.Redact(phone), message);
        return Task.FromResult(SmsResult.Ok(Name, Guid.NewGuid().ToString("n")));
    }
}

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MageRide.Iam.Configuration;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Otp;

/// <summary>
/// The Notify.lk REST gateway — D6' §7.3's primary SMS transport ("Notify.lk REST, ~Rs 0.50–1.50
/// per SMS — OTP, transactional, low-balance").
/// </summary>
/// <remarks>
/// <para>
/// One form POST to <c>/send</c> with <c>user_id</c>, <c>api_key</c>, <c>sender_id</c>, <c>to</c>
/// and <c>message</c>. Their API answers <b>HTTP 200 with <c>{"status":"error"}</c></b> for a
/// rejected send — an unregistered sender mask, an exhausted balance — so the status line alone is
/// not the outcome and the body has to be read. A sender that trusted the 200 would report every
/// OTP as delivered and no user could ever sign in.
/// </para>
/// <para>
/// <c>to</c> is the national form without a <c>+</c>: <c>94771234567</c>. The rest of the platform
/// stores E.164 (<c>PhoneNumbers</c>), so the conversion happens here, at the boundary that wants
/// the other spelling.
/// </para>
/// </remarks>
public sealed class NotifyLkOtpSender(
    IHttpClientFactory httpClientFactory,
    SmsTemplates templates,
    IOptions<SmsOptions> smsOptions,
    IOptions<OtpOptions> otpOptions,
    ILogger<NotifyLkOtpSender> logger) : IOtpSender
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "notifylk";

    private readonly SmsOptions _sms = smsOptions?.Value ?? throw new ArgumentNullException(nameof(smsOptions));
    private readonly OtpOptions _otp = otpOptions?.Value ?? throw new ArgumentNullException(nameof(otpOptions));

    public string Provider => SmsOptions.NotifyLkProvider;

    public async Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var message = templates.Render(SmsTemplates.Otp, language, code, (int)_otp.Ttl.TotalMinutes);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user_id"] = _sms.NotifyLkUserId ?? string.Empty,
            ["api_key"] = _sms.NotifyLkApiKey ?? string.Empty,
            ["sender_id"] = _sms.NotifyLkSenderId,
            ["to"] = ToNationalDigits(phone),
            ["message"] = message,
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsync("send", new FormUrlEncodedContent(form), cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OtpDeliveryException(
                $"Notify.lk answered {(int)response.StatusCode} for {Redact(phone)}.");
        }

        if (!IsAccepted(body, out var reported))
        {
            // The body is logged and the exception is not given it: their error strings can carry
            // the destination number, and an OTP failure is not a reason to put a phone number in
            // an exception message that may reach a client.
            logger.LogError("Notify.lk refused the send for {Phone}: {Body}", Redact(phone), body);
            throw new OtpDeliveryException($"Notify.lk refused the send ({reported}).");
        }
    }

    /// <summary>
    /// <c>+94771234567</c> → <c>94771234567</c>. Notify.lk rejects the leading <c>+</c>.
    /// </summary>
    internal static string ToNationalDigits(string e164) => e164.TrimStart('+');

    /// <summary>
    /// Their success shape is <c>{"status":"success","data":{…}}</c>; failures come back as
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

    /// <summary>Last four digits only. An OTP log line is not a place for a full MSISDN.</summary>
    internal static string Redact(string phone) =>
        phone.Length <= 4 ? "****" : string.Create(CultureInfo.InvariantCulture, $"****{phone[^4..]}");
}

/// <summary>
/// A generic HTTP fallback gateway — the "secondary gateway (Dialog/Mobitel)" of D6' §7.3 and
/// D7' §4.2's <c>Sms__SecondaryGateway</c>.
/// </summary>
/// <remarks>
/// <para>
/// No spec prints the secondary provider's request shape, and the two candidates D6' names do not
/// share one. What every one of them does accept is a JSON POST of a destination and a body under
/// a bearer credential, so that is what this sends; a deployment whose gateway wants something
/// else replaces this class rather than reconfiguring it. Recorded as a spec gap in the C026
/// handoff.
/// </para>
/// <para>
/// Note the asymmetry with SOS: D-33 sends an SOS through <em>both</em> gateways in parallel and
/// takes whichever lands first, because five seconds matters more than the cost of two messages.
/// An OTP is not an emergency — it goes to the primary, and only reaches here if that failed.
/// </para>
/// </remarks>
public sealed class SecondaryGatewayOtpSender(
    IHttpClientFactory httpClientFactory,
    SmsTemplates templates,
    IOptions<SmsOptions> smsOptions,
    IOptions<OtpOptions> otpOptions) : IOtpSender
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "sms-secondary";

    private readonly SmsOptions _sms = smsOptions?.Value ?? throw new ArgumentNullException(nameof(smsOptions));
    private readonly OtpOptions _otp = otpOptions?.Value ?? throw new ArgumentNullException(nameof(otpOptions));

    public string Provider => "secondary";

    public async Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var message = templates.Render(SmsTemplates.Otp, language, code, (int)_otp.Ttl.TotalMinutes);

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            string.Empty,
            new
            {
                to = phone,
                from = _sms.SecondarySenderId ?? _sms.NotifyLkSenderId,
                message,
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OtpDeliveryException(
                $"The secondary SMS gateway answered {(int)response.StatusCode} for {NotifyLkOtpSender.Redact(phone)}.");
        }
    }
}

/// <summary>An SMS gateway would not take the message.</summary>
/// <remarks>
/// Distinct from a transport exception so <see cref="FallbackOtpSender"/> can tell "the gateway
/// said no" from "the gateway is unreachable" — both are worth failing over, neither is worth a
/// 500.
/// </remarks>
public sealed class OtpDeliveryException : Exception
{
    public OtpDeliveryException(string message)
        : base(message)
    {
    }

    public OtpDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OtpDeliveryException()
    {
    }
}

/// <summary>
/// Sends through the primary gateway and falls back to the secondary (D6' §7.3).
/// </summary>
/// <remarks>
/// <para>
/// The per-gateway retry lives in the resilience pipeline on each named client
/// (<c>AddMageRideResilience</c>, D6' §8.3), which is where "Retry: 2 attempts" is configured.
/// This class is the *other* half: when the primary is exhausted, the message goes to the
/// secondary rather than to nobody.
/// </para>
/// <para>
/// Both failing is a <c>503 dependency-unavailable</c>, not a 500. The caller's OTP allowance has
/// already been spent by then — the token bucket is consumed before the send, because it exists to
/// bound how often we *try* — so the response says "try again shortly" and means it.
/// </para>
/// </remarks>
public sealed class FallbackOtpSender(
    IOtpSender primary, IOtpSender secondary, ILogger<FallbackOtpSender> logger) : IOtpSender
{
    public string Provider => $"{primary.Provider}+{secondary.Provider}";

    public async Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        try
        {
            await primary.SendAsync(phone, code, language, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is OtpDeliveryException or HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "The primary SMS gateway ({Primary}) could not deliver to {Phone}; falling back to {Secondary} (D6' §7.3)",
                primary.Provider,
                NotifyLkOtpSender.Redact(phone),
                secondary.Provider);
        }

        try
        {
            await secondary.SendAsync(phone, code, language, cancellationToken);
        }
        catch (Exception ex) when (ex is OtpDeliveryException or HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Both SMS gateways refused the OTP for {Phone}", NotifyLkOtpSender.Redact(phone));
            throw new MageRideException(
                MageRideErrors.DependencyUnavailable, "The SMS gateway is unavailable; try again shortly.");
        }
    }
}

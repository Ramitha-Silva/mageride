using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Iam.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Otp;

/// <summary>
/// The Fit SMS v4 REST gateway (<c>https://app.fitsms.lk/api/v4/sms/send</c>).
/// </summary>
/// <remarks>
/// <para>
/// One JSON POST to <c>sms/send</c> under a bearer token, carrying <c>recipient</c>,
/// <c>sender_id</c>, <c>type</c>, <c>message</c> and <c>expiry_time</c>.
/// </para>
/// <para>
/// Like Notify.lk, <b>their API answers HTTP 200 with <c>{"status":"error"}</c></b> for a rejected
/// send — an unregistered sender mask, an exhausted balance — so the status line alone is not the
/// outcome and the body has to be read. A sender that trusted the 200 would report every OTP as
/// delivered and no user could ever sign in.
/// </para>
/// <para>
/// <c>recipient</c> is the national form without a <c>+</c>: <c>94771234567</c>, the same spelling
/// Notify.lk wants, so the conversion is <see cref="NotifyLkOtpSender.ToNationalDigits"/> rather
/// than a second copy of it.
/// </para>
/// </remarks>
public sealed class FitSmsOtpSender(
    IHttpClientFactory httpClientFactory,
    SmsTemplates templates,
    IOptions<SmsOptions> smsOptions,
    IOptions<OtpOptions> otpOptions,
    ILogger<FitSmsOtpSender> logger) : IOtpSender
{
    /// <summary>The named client the resilience pipeline is attached to.</summary>
    public const string HttpClientName = "fitsms";

    /// <summary>Their <c>type</c> for a GSM-7 body. The only value their send docs name.</summary>
    public const string PlainType = "plain";

    /// <summary>
    /// Their floor for <c>expiry_time</c>: "Must be at least +60 Seconds from the current time".
    /// </summary>
    internal const int MinimumExpirySeconds = 60;

    /// <summary>Their ceiling: "Max: 24 hours after creation".</summary>
    internal const int MaximumExpirySeconds = 24 * 60 * 60;

    private readonly SmsOptions _sms = smsOptions?.Value ?? throw new ArgumentNullException(nameof(smsOptions));
    private readonly OtpOptions _otp = otpOptions?.Value ?? throw new ArgumentNullException(nameof(otpOptions));

    public string Provider => SmsOptions.FitSmsProvider;

    public async Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var message = templates.Render(SmsTemplates.Otp, language, code, (int)_otp.Ttl.TotalMinutes);

        var request = new FitSmsSendRequest(
            Recipient: NotifyLkOtpSender.ToNationalDigits(phone),
            SenderId: _sms.FitSmsSenderId,
            Type: MessageTypeFor(message, _sms.FitSmsUnicodeType),
            Message: message,
            ExpirySeconds: ExpirySecondsFor(_otp.Ttl));

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync("sms/send", request, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Their 4xx bodies carry the reason, and it is the only place a bad token or an
            // unregistered mask is spelt out — so it is logged, and the exception is not given it.
            logger.LogError(
                "Fit SMS answered {Status} for {Phone}: {Body}",
                (int)response.StatusCode,
                NotifyLkOtpSender.Redact(phone),
                body);

            throw new OtpDeliveryException(
                $"Fit SMS answered {(int)response.StatusCode} for {NotifyLkOtpSender.Redact(phone)}.");
        }

        if (!IsAccepted(body, out var reported, out var ruid))
        {
            // As above: their error strings can carry the destination number, and an OTP failure is
            // not a reason to put a phone number in an exception message that may reach a client.
            logger.LogError("Fit SMS refused the send for {Phone}: {Body}", NotifyLkOtpSender.Redact(phone), body);
            throw new OtpDeliveryException($"Fit SMS refused the send ({reported}).");
        }

        // The one field worth keeping: `ruid` is what their support and their /sms/{ruid} lookup
        // trace a missing OTP by, and it is meaningless without a line saying which send it was.
        logger.LogDebug("Fit SMS accepted the OTP for {Phone} as {Ruid}", NotifyLkOtpSender.Redact(phone), ruid);
    }

    /// <summary>
    /// <c>plain</c> for a GSM-7 body, the configured unicode type for anything else.
    /// </summary>
    /// <remarks>
    /// Not a constant, because AL-26 makes Sinhala the default language: the common OTP on this
    /// platform is UCS-2, and telling a gateway a Sinhala body is <c>plain</c> is how it arrives as
    /// question marks. See <see cref="SmsOptions.FitSmsUnicodeType"/> for the escape hatch.
    /// </remarks>
    internal static string MessageTypeFor(string message, string? unicodeType) =>
        IsGsmSevenBit(message) || string.IsNullOrWhiteSpace(unicodeType)
            ? PlainType
            : unicodeType;

    /// <summary>
    /// Whether every character is ASCII.
    /// </summary>
    /// <remarks>
    /// A deliberate under-approximation of GSM-7, which also holds a handful of non-ASCII
    /// characters (<c>£</c>, <c>é</c>, <c>§</c>). Getting those wrong costs one extra segment on a
    /// message no template here produces; getting Sinhala wrong costs every Sinhala user their
    /// sign-in, so the cheap test is the safe one.
    /// </remarks>
    internal static bool IsGsmSevenBit(string message) => message.All(static c => c <= 127);

    /// <summary>
    /// The OTP's own TTL, clamped into the window their API accepts.
    /// </summary>
    /// <remarks>
    /// The message is worthless once the code it carries has expired, so the delivery deadline is
    /// the code's. D7' §4.2's <c>Otp__Ttl</c> is five minutes, comfortably inside their window;
    /// the clamp is for a deployment that shortens it below their sixty-second floor.
    /// </remarks>
    internal static int ExpirySecondsFor(TimeSpan ttl) =>
        Math.Clamp((int)Math.Round(ttl.TotalSeconds), MinimumExpirySeconds, MaximumExpirySeconds);

    /// <summary>
    /// Their success shape is <c>{"status":"success","data":{"ruid":…}}</c>; failures come back as
    /// <c>{"status":"error","message":"…"}</c> under the same 200.
    /// </summary>
    /// <remarks>
    /// The same envelope Notify.lk uses, parsed separately rather than shared, because the halves
    /// differ where it matters: this one carries <c>data.ruid</c>, the id a missing OTP is traced
    /// by, and a shared parser would have to answer <c>null</c> for it on every Notify.lk send.
    /// </remarks>
    internal static bool IsAccepted(string body, out string reported, out string? ruid)
    {
        reported = "unparseable response";
        ruid = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("status", out var status))
            {
                return false;
            }

            reported = status.GetString() ?? "no status";

            if (!string.Equals(reported, "success", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("ruid", out var id))
            {
                ruid = id.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The send body, as their field names spell it.
    /// </summary>
    /// <remarks>
    /// A named type with explicit <see cref="JsonPropertyNameAttribute"/>s rather than an anonymous
    /// object: <c>PostAsJsonAsync</c> serialises with the web defaults, whose camelCase policy would
    /// send <c>senderId</c> and <c>expiryTime</c> — both of which their API ignores, leaving a send
    /// with no mask rather than an error anyone would see.
    /// </remarks>
    internal sealed record FitSmsSendRequest(
        [property: JsonPropertyName("recipient")] string Recipient,
        [property: JsonPropertyName("sender_id")] string SenderId,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("expiry_time")] int ExpirySeconds);
}

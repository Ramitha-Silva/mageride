using System.Text.Json;
using MageRide.Iam.Otp;
using Microsoft.Extensions.Logging.Abstractions;

namespace MageRide.Iam.Tests.Otp;

/// <summary>
/// The trilingual OTP bodies (D-26) and the two gateways that carry them (D6' §7.3).
/// </summary>
public sealed class SmsTemplateTests
{
    private static readonly SmsTemplates Templates = new();

    [Theory]
    [InlineData("en")]
    [InlineData("si")]
    [InlineData("ta")]
    public void Every_language_has_a_body_and_it_carries_the_code(string language)
    {
        var body = Templates.Render(SmsTemplates.Otp, language, "123456", 5);

        Assert.Contains("123456", body, StringComparison.Ordinal);
        Assert.Contains("5", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{code}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{minutes}", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The platform rule is Si/Ta/En for every user-facing string, so the three must actually be
    /// three — a copy-pasted English body in the Sinhala slot passes every other check here.
    /// </summary>
    [Fact]
    public void The_three_languages_are_genuinely_different_text()
    {
        var en = Templates.Render(SmsTemplates.Otp, "en", "123456", 5);
        var si = Templates.Render(SmsTemplates.Otp, "si", "123456", 5);
        var ta = Templates.Render(SmsTemplates.Otp, "ta", "123456", 5);

        Assert.NotEqual(en, si);
        Assert.NotEqual(en, ta);
        Assert.NotEqual(si, ta);
    }

    /// <summary>A code that arrives in the wrong language beats one that never arrives.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    public void An_unknown_language_falls_back_to_english_rather_than_failing(string? language)
    {
        Assert.Equal(
            Templates.Render(SmsTemplates.Otp, "en", "123456", 5),
            Templates.Render(SmsTemplates.Otp, language, "123456", 5));
    }

    [Fact]
    public void An_unknown_template_is_a_programming_error()
    {
        Assert.Throws<KeyNotFoundException>(() => Templates.Render("low-balance", "en", "123456", 5));
    }

    /// <summary>
    /// Sinhala and Tamil force UCS-2, where one SMS segment is 70 characters rather than 160. A
    /// two-segment OTP doubles the per-sign-in cost across the whole country.
    /// </summary>
    [Theory]
    [InlineData("si")]
    [InlineData("ta")]
    public void The_unicode_bodies_fit_one_ucs2_segment(string language)
    {
        var body = Templates.Render(SmsTemplates.Otp, language, "123456", 5);

        Assert.True(body.Length <= 70, $"{language} OTP body is {body.Length} characters; UCS-2 segments hold 70");
    }

    [Fact]
    public void A_template_set_with_no_english_body_is_refused_at_construction()
    {
        // English is the fallback for every unknown language, so a set without it would leave
        // some users with no message at all.
        var exception = Assert.Throws<InvalidOperationException>(() => new SmsTemplates(
            """{"templates":{"otp":{"si":"...","ta":"..."}}}"""));

        Assert.Contains("otp", exception.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The phone-number conversions every gateway needs, and the fallback that keeps an outage from
/// being a lock-out.
/// </summary>
public sealed class OtpDeliveryTests
{
    /// <summary>The platform stores E.164; the gateway wants the national form.</summary>
    [Fact]
    public void The_destination_is_sent_in_the_national_form()
    {
        Assert.Equal("94771234567", SmsPhone.ToNationalDigits("+94771234567"));
    }

    [Fact]
    public void A_log_line_never_carries_a_whole_msisdn()
    {
        Assert.Equal("****4567", SmsPhone.Redact("+94771234567"));
        Assert.Equal("****", SmsPhone.Redact("+94"));
    }

    [Fact]
    public async Task The_primary_gateway_is_used_when_it_works()
    {
        var primary = new RecordingSender("primary");
        var secondary = new RecordingSender("secondary");

        await Fallback(primary, secondary).SendAsync("+94771234567", "123456", "en", CancellationToken.None);

        Assert.Equal(1, primary.Sent);
        Assert.Equal(0, secondary.Sent);
    }

    [Fact]
    public async Task A_primary_failure_falls_through_to_the_secondary_gateway()
    {
        var primary = new RecordingSender("primary", () => new OtpDeliveryException("balance exhausted"));
        var secondary = new RecordingSender("secondary");

        await Fallback(primary, secondary).SendAsync("+94771234567", "123456", "si", CancellationToken.None);

        Assert.Equal(1, primary.Sent);
        Assert.Equal(1, secondary.Sent);
        Assert.Equal("si", secondary.LastLanguage);
    }

    [Fact]
    public async Task An_unreachable_primary_falls_through_too()
    {
        var primary = new RecordingSender("primary", () => new HttpRequestException("connection refused"));
        var secondary = new RecordingSender("secondary");

        await Fallback(primary, secondary).SendAsync("+94771234567", "123456", "en", CancellationToken.None);

        Assert.Equal(1, secondary.Sent);
    }

    /// <summary>
    /// Both gateways down is a 503 the client can act on, not a 500 its retry policy reads as a
    /// bug in us.
    /// </summary>
    [Fact]
    public async Task Both_gateways_failing_is_a_dependency_outage()
    {
        var primary = new RecordingSender("primary", () => new OtpDeliveryException("down"));
        var secondary = new RecordingSender("secondary", () => new OtpDeliveryException("also down"));

        var exception = await Assert.ThrowsAsync<MageRide.Shared.Errors.MageRideException>(
            () => Fallback(primary, secondary).SendAsync("+94771234567", "123456", "en", CancellationToken.None));

        Assert.Equal(MageRide.Shared.Errors.MageRideErrors.DependencyUnavailable.Code, exception.Error.Code);
    }

    [Fact]
    public void The_composite_names_both_gateways_for_diagnostics()
    {
        Assert.Equal(
            "primary+secondary",
            Fallback(new RecordingSender("primary"), new RecordingSender("secondary")).Provider);
    }

    private static FallbackOtpSender Fallback(IOtpSender primary, IOtpSender secondary) =>
        new(primary, secondary, NullLogger<FallbackOtpSender>.Instance);

    private sealed class RecordingSender(string provider, Func<Exception>? failWith = null) : IOtpSender
    {
        public string Provider => provider;

        public int Sent { get; private set; }

        public string? LastLanguage { get; private set; }

        public Task SendAsync(string phone, string code, string language, CancellationToken cancellationToken)
        {
            Sent++;
            LastLanguage = language;

            return failWith is null ? Task.CompletedTask : Task.FromException(failWith());
        }
    }
}

/// <summary>
/// Fit SMS's wire quirks — the envelope that reports a refusal under a 200, the field names their
/// API spells with underscores, and the message type a Sinhala body has to carry.
/// </summary>
public sealed class FitSmsDeliveryTests
{
    /// <summary>
    /// The trap: a rejected send comes back as <b>HTTP 200 with
    /// <c>status: error</c></b>, so the status line alone is not the outcome.
    /// </summary>
    [Theory]
    [InlineData("""{"status":"success","data":{"ruid":"03bd1b3d590f40819aa83a49c1ca1a41"}}""", true)]
    [InlineData("""{"status":"error","message":"Insufficient balance"}""", false)]
    [InlineData("""{"data":{}}""", false)]
    [InlineData("not json at all", false)]
    [InlineData("", false)]
    public void A_two_hundred_is_not_the_same_as_a_delivery(string body, bool accepted)
    {
        Assert.Equal(accepted, FitSmsOtpSender.IsAccepted(body, out _, out _));
    }

    /// <summary>
    /// <c>ruid</c> is what their support and their <c>/sms/{ruid}</c> lookup trace a missing OTP
    /// by, so it is the one field the sender keeps.
    /// </summary>
    [Fact]
    public void The_tracking_id_is_carried_out_of_a_successful_send()
    {
        FitSmsOtpSender.IsAccepted(
            """{"status":"success","data":{"ruid":"03bd1b3d590f40819aa83a49c1ca1a41","total_receivers":1}}""",
            out _,
            out var ruid);

        Assert.Equal("03bd1b3d590f40819aa83a49c1ca1a41", ruid);
    }

    /// <summary>A success with no <c>data</c> block is still a success; there is just nothing to trace by.</summary>
    [Fact]
    public void A_success_without_a_tracking_id_is_still_a_success()
    {
        Assert.True(FitSmsOtpSender.IsAccepted("""{"status":"success"}""", out _, out var ruid));
        Assert.Null(ruid);
    }

    [Fact]
    public void The_reported_status_is_carried_out_for_the_log()
    {
        FitSmsOtpSender.IsAccepted("""{"status":"error","message":"Invalid sender id"}""", out var reported, out _);

        Assert.Equal("error", reported);
    }

    /// <summary>
    /// AL-26 makes Sinhala the default language, so the COMMON OTP on this platform is UCS-2. A
    /// gateway told a Sinhala body is <c>plain</c> delivers question marks.
    /// </summary>
    [Theory]
    [InlineData("si")]
    [InlineData("ta")]
    public void A_sinhala_or_tamil_body_is_sent_as_unicode(string language)
    {
        var body = new SmsTemplates().Render(SmsTemplates.Otp, language, "123456", 5);

        Assert.Equal("unicode", FitSmsOtpSender.MessageTypeFor(body, "unicode"));
    }

    [Fact]
    public void An_english_body_is_sent_as_plain()
    {
        var body = new SmsTemplates().Render(SmsTemplates.Otp, "en", "123456", 5);

        Assert.Equal(FitSmsOtpSender.PlainType, FitSmsOtpSender.MessageTypeFor(body, "unicode"));
    }

    /// <summary>The escape hatch: a deployment whose gateway refuses <c>unicode</c> clears the setting.</summary>
    [Fact]
    public void Clearing_the_unicode_type_sends_everything_as_plain()
    {
        var body = new SmsTemplates().Render(SmsTemplates.Otp, "si", "123456", 5);

        Assert.Equal(FitSmsOtpSender.PlainType, FitSmsOtpSender.MessageTypeFor(body, null));
        Assert.Equal(FitSmsOtpSender.PlainType, FitSmsOtpSender.MessageTypeFor(body, "  "));
    }

    /// <summary>
    /// The message is worthless once the code it carries has expired, so the delivery deadline is
    /// the code's — clamped into the window their API accepts ("at least +60 Seconds", max 24 h).
    /// </summary>
    [Theory]
    [InlineData(300, 300)]
    [InlineData(30, FitSmsOtpSender.MinimumExpirySeconds)]
    [InlineData(48 * 60 * 60, FitSmsOtpSender.MaximumExpirySeconds)]
    public void The_expiry_is_the_otp_ttl_clamped_to_their_window(int ttlSeconds, int expected)
    {
        Assert.Equal(expected, FitSmsOtpSender.ExpirySecondsFor(TimeSpan.FromSeconds(ttlSeconds)));
    }

    /// <summary>The platform stores E.164; their <c>recipient</c> is the national form.</summary>
    [Fact]
    public void The_destination_is_sent_in_the_national_form()
    {
        Assert.Equal("94771234567", SmsPhone.ToNationalDigits("+94771234567"));
    }

    /// <summary>
    /// <c>PostAsJsonAsync</c> serialises with the web defaults, whose camelCase policy would send
    /// <c>senderId</c> and <c>expiryTime</c> — names their API ignores, leaving a send with no mask
    /// rather than an error anyone would see. The property attributes are what stop it, and this is
    /// what would fail if somebody replaced the record with an anonymous object.
    /// </summary>
    [Fact]
    public void The_send_body_uses_their_snake_case_field_names()
    {
        var request = new FitSmsOtpSender.FitSmsSendRequest(
            Recipient: "94771234567",
            SenderId: "MageRide",
            Type: "unicode",
            Message: "123456 is your MageRide code.",
            ExpirySeconds: 300);

        var json = JsonSerializer.Serialize(request, JsonSerializerOptions.Web);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("94771234567", document.RootElement.GetProperty("recipient").GetString());
        Assert.Equal("MageRide", document.RootElement.GetProperty("sender_id").GetString());
        Assert.Equal("unicode", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(300, document.RootElement.GetProperty("expiry_time").GetInt32());
    }
}

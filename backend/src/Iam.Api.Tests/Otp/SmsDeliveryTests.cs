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
/// Notify.lk's wire quirks, and the fallback that keeps an outage from being a lock-out.
/// </summary>
public sealed class OtpDeliveryTests
{
    /// <summary>
    /// Their API answers <b>HTTP 200 with <c>status: error</c></b> for a rejected send. A sender
    /// that trusted the status line would report every OTP as delivered and nobody could sign in.
    /// </summary>
    [Theory]
    [InlineData("""{"status":"success","data":{"user_id":1}}""", true)]
    [InlineData("""{"status":"error","message":"Insufficient balance"}""", false)]
    [InlineData("""{"data":{}}""", false)]
    [InlineData("not json at all", false)]
    [InlineData("", false)]
    public void A_two_hundred_is_not_the_same_as_a_delivery(string body, bool accepted)
    {
        Assert.Equal(accepted, NotifyLkOtpSender.IsAccepted(body, out _));
    }

    [Fact]
    public void The_reported_status_is_carried_out_for_the_log()
    {
        NotifyLkOtpSender.IsAccepted("""{"status":"error","message":"Invalid sender"}""", out var reported);

        Assert.Equal("error", reported);
    }

    /// <summary>The platform stores E.164; Notify.lk rejects the leading plus.</summary>
    [Fact]
    public void The_destination_is_sent_in_the_national_form()
    {
        Assert.Equal("94771234567", NotifyLkOtpSender.ToNationalDigits("+94771234567"));
    }

    [Fact]
    public void A_log_line_never_carries_a_whole_msisdn()
    {
        Assert.Equal("****4567", NotifyLkOtpSender.Redact("+94771234567"));
        Assert.Equal("****", NotifyLkOtpSender.Redact("+94"));
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

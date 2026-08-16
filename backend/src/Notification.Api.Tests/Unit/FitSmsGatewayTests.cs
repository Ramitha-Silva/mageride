using MageRide.Notification.Configuration;
using MageRide.Notification.Sms;

namespace MageRide.Notification.Tests.Unit;

/// <summary>
/// The Fit SMS gateway's three decisions that are not HTTP: what their envelope means, what
/// <c>type</c> a body needs, and what deadline their API will accept.
/// </summary>
/// <remarks>
/// Every one of these is a failure that a passing HTTP call would hide — an accepted 200 carrying
/// <c>{"status":"error"}</c>, a Sinhala SOS sent as <c>plain</c>, an <c>expiry_time</c> their API
/// rejects. iam-svc's <c>FitSmsSendTests</c> covers the same ground for the OTP path; the two are
/// separate because the two services are.
/// </remarks>
public sealed class FitSmsGatewayTests
{
    // --- the envelope -------------------------------------------------------------------

    [Fact]
    public void A_success_envelope_is_accepted_and_carries_the_ruid()
    {
        const string body = """
            {"status":"success","data":{"ruid":"03bd1b3d590f40819aa83a49c1ca1a41","total_receivers":1,
            "status":"pending","from":"MageRide"}}
            """;

        Assert.True(FitSmsGateway.IsAccepted(body, out var reported, out var ruid));
        Assert.Equal("success", reported);
        Assert.Equal("03bd1b3d590f40819aa83a49c1ca1a41", ruid);
    }

    /// <summary>
    /// Their API answers <b>HTTP 200</b> for a refused send. A gateway that trusted the status line
    /// would mark every SOS delivered and `safety.sos_events` would record a message nobody got.
    /// </summary>
    [Fact]
    public void An_error_envelope_under_a_200_is_a_failure()
    {
        const string body = """{"status":"error","message":"Sender ID not approved"}""";

        Assert.False(FitSmsGateway.IsAccepted(body, out var reported, out var ruid));
        Assert.Equal("error", reported);
        Assert.Null(ruid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"data":{"ruid":"x"}}""")]
    public void Anything_that_is_not_a_success_object_is_a_failure(string body)
    {
        Assert.False(FitSmsGateway.IsAccepted(body, out _, out _));
    }

    /// <summary>A success with no <c>data</c> is still a success — the ruid is for tracing, not truth.</summary>
    [Fact]
    public void A_success_without_a_ruid_is_still_accepted()
    {
        Assert.True(FitSmsGateway.IsAccepted("""{"status":"success"}""", out _, out var ruid));
        Assert.Null(ruid);
    }

    // --- plain vs unicode ---------------------------------------------------------------

    /// <summary>
    /// AL-26 makes Sinhala the default language and D-26 renders every body in the recipient's own,
    /// so the common message on this platform is UCS-2. Sent as <c>plain</c> it arrives as question
    /// marks — which for an SOS is the whole message lost.
    /// </summary>
    [Theory]
    [InlineData("Your MageRide code is 123456", FitSmsGateway.PlainType)]
    [InlineData("SOS: track at https://passenger.mageride.lk/track?token=abc", FitSmsGateway.PlainType)]
    [InlineData("ඔබගේ MageRide කේතය 123456", "unicode")]
    [InlineData("உங்கள் MageRide குறியீடு 123456", "unicode")]
    public void The_type_follows_the_body_not_the_caller(string message, string expected)
    {
        Assert.Equal(expected, FitSmsGateway.MessageTypeFor(message, "unicode"));
    }

    /// <summary>
    /// The escape hatch: a deployment whose gateway refuses <c>unicode</c> sets the option empty and
    /// gets <c>plain</c>, rather than a send that fails.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unset_unicode_type_falls_back_to_plain(string? unicodeType)
    {
        Assert.Equal(FitSmsGateway.PlainType, FitSmsGateway.MessageTypeFor("ඔබගේ කේතය", unicodeType));
    }

    // --- expiry -------------------------------------------------------------------------

    /// <summary>
    /// Their floor is "+60 seconds" and their ceiling is 24 hours. Both are clamped rather than
    /// validated, because a rejected <c>expiry_time</c> loses the message for a reason nobody
    /// configuring a retention window would connect to it.
    /// </summary>
    [Theory]
    [InlineData(0, FitSmsGateway.MinimumExpirySeconds)]
    [InlineData(30, FitSmsGateway.MinimumExpirySeconds)]
    [InlineData(60, 60)]
    [InlineData(3600, 3600)]
    [InlineData(24 * 60 * 60, FitSmsGateway.MaximumExpirySeconds)]
    [InlineData(48 * 60 * 60, FitSmsGateway.MaximumExpirySeconds)]
    public void The_expiry_is_clamped_into_the_window_their_api_accepts(int seconds, int expected)
    {
        Assert.Equal(expected, FitSmsGateway.ExpirySecondsFor(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>The shipped default sits on their ceiling and survives the clamp unchanged.</summary>
    [Fact]
    public void The_default_expiry_is_inside_their_window()
    {
        var configured = new SmsOptions().FitSmsExpiry;

        Assert.Equal(
            (int)configured.TotalSeconds,
            FitSmsGateway.ExpirySecondsFor(configured));
    }

    // --- the wire shape -----------------------------------------------------------------

    /// <summary>
    /// Their field names are snake_case and <c>PostAsJsonAsync</c> serialises with the web
    /// defaults' camelCase policy, so the record carries explicit names. Without them
    /// <c>sender_id</c> goes as <c>senderId</c>, their API ignores it, and the message is sent with
    /// no mask rather than refused.
    /// </summary>
    [Fact]
    public void The_request_serialises_with_their_field_names()
    {
        var request = new FitSmsGateway.FitSmsSendRequest(
            Recipient: "94771234567",
            SenderId: "MageRide",
            Type: FitSmsGateway.PlainType,
            Message: "test",
            ExpirySeconds: 3600);

        var json = System.Text.Json.JsonSerializer.Serialize(
            request,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"recipient\":\"94771234567\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sender_id\":\"MageRide\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expiry_time\":3600", json, StringComparison.Ordinal);
        Assert.DoesNotContain("senderId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("expiryTime", json, StringComparison.Ordinal);
    }

    // --- which gateway a provider selects ------------------------------------------------

    /// <summary>
    /// The provider names the primary. An unknown value selects NOTHING rather than defaulting to a
    /// gateway, so a typo fails every send loudly instead of quietly sending through whichever
    /// gateway happened to be registered.
    /// </summary>
    [Theory]
    [InlineData("fitsms", SmsGatewayNames.FitSms)]
    [InlineData("FitSms", SmsGatewayNames.FitSms)]
    [InlineData("notifylk", SmsGatewayNames.NotifyLk)]
    [InlineData("dev", SmsGatewayNames.Dev)]
    [InlineData("fitsmss", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_provider_selects_the_primary_by_name(string? provider, string expected)
    {
        Assert.Equal(expected, SmsSender.PrimaryNameFor(provider));
    }
}

using MageRide.Iam.Configuration;
using MageRide.Iam.Otp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MageRide.Iam.Tests.Otp;

/// <summary>
/// Which SMS transport the service comes up with, and the three ways it refuses to come up at
/// all. Every check is an options validation, so it fires at host start rather than on the first
/// user who tries to sign in.
/// </summary>
public sealed class OtpSenderSelectionTests
{
    private static SmsOptions Resolve(IHostEnvironment environment, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIamServices(configuration, environment);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SmsOptions>>().Value;
    }

    [Fact]
    public void Development_defaults_to_the_dev_sender()
    {
        Assert.Equal(SmsOptions.DevProvider, Resolve(TestEnvironment.Development).Provider);
    }

    [Fact]
    public void The_dev_sender_logs_the_code_rather_than_sending_it()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIamServices(configuration, TestEnvironment.Development);

        using var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<IOtpSender>();

        Assert.IsType<DevLoggingOtpSender>(sender);
        Assert.Equal(SmsOptions.DevProvider, sender.Provider);
    }

    [Fact]
    public void The_dev_sender_is_refused_outside_development_unless_it_is_asked_for()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Resolve(TestEnvironment.Production));

        Assert.Contains("AllowDevSenderOutsideDevelopment", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_replica_can_opt_into_the_dev_sender_deliberately()
    {
        var options = Resolve(
            TestEnvironment.Production,
            ("Sms:Provider", SmsOptions.DevProvider),
            ("Sms:AllowDevSenderOutsideDevelopment", "true"));

        Assert.Equal(SmsOptions.DevProvider, options.Provider);
    }

    [Fact]
    public void Selecting_notify_lk_without_credentials_fails_fast_rather_than_swallowing_every_otp()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(TestEnvironment.Development, ("Sms:Provider", SmsOptions.NotifyLkProvider)));

        Assert.Contains("Sms:NotifyLkApiKey", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Notify_lk_is_the_sender_once_it_is_configured()
    {
        var sender = ResolveSender(
            ("Sms:Provider", SmsOptions.NotifyLkProvider),
            ("Sms:NotifyLkUserId", "12345"),
            ("Sms:NotifyLkApiKey", "not-a-real-key"));

        Assert.IsType<NotifyLkOtpSender>(sender);
    }

    [Fact]
    public void Selecting_fit_sms_without_a_token_fails_fast_rather_than_swallowing_every_otp()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(TestEnvironment.Development, ("Sms:Provider", SmsOptions.FitSmsProvider)));

        Assert.Contains("Sms:FitSmsApiToken", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_sms_is_the_sender_once_it_is_configured()
    {
        var sender = ResolveSender(
            ("Sms:Provider", SmsOptions.FitSmsProvider),
            ("Sms:FitSmsApiToken", "588|not-a-real-token"));

        Assert.IsType<FitSmsOtpSender>(sender);
        Assert.Equal(SmsOptions.FitSmsProvider, sender.Provider);
    }

    /// <summary>
    /// Their cap is on an ALPHANUMERIC mask. A mask that is a telephone number is a longer string
    /// they accept, so the check must not reject one.
    /// </summary>
    [Fact]
    public void An_alphanumeric_sender_mask_over_eleven_characters_is_a_configuration_error()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(
                TestEnvironment.Development,
                ("Sms:Provider", SmsOptions.FitSmsProvider),
                ("Sms:FitSmsApiToken", "588|not-a-real-token"),
                ("Sms:FitSmsSenderId", "MageRideLanka")));

        Assert.Contains("Sms:FitSmsSenderId", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_numeric_sender_mask_is_not_bound_by_the_alphanumeric_cap()
    {
        var options = Resolve(
            TestEnvironment.Development,
            ("Sms:Provider", SmsOptions.FitSmsProvider),
            ("Sms:FitSmsApiToken", "588|not-a-real-token"),
            ("Sms:FitSmsSenderId", "94771234567"));

        Assert.Equal("94771234567", options.FitSmsSenderId);
    }

    /// <summary>
    /// The fallback wraps whichever primary was chosen — the secondary gateway is not Notify.lk's
    /// alone, and a deployment on Fit SMS that configured one would otherwise silently lose it.
    /// </summary>
    [Fact]
    public void A_configured_secondary_gateway_wraps_fit_sms_too()
    {
        var sender = ResolveSender(
            ("Sms:Provider", SmsOptions.FitSmsProvider),
            ("Sms:FitSmsApiToken", "588|not-a-real-token"),
            ("Sms:SecondaryGateway", "https://sms.example.lk/send"),
            ("Sms:SecondaryApiKey", "also-not-real"));

        Assert.IsType<FallbackOtpSender>(sender);
        Assert.Equal("fitsms+secondary", sender.Provider);
    }

    /// <summary>
    /// D6' §7.3's secondary gateway is only wired in when one is configured — a deployment with
    /// one gateway is a deployment with one gateway, not a broken one.
    /// </summary>
    [Fact]
    public void A_configured_secondary_gateway_wraps_notify_lk_in_the_fallback()
    {
        var sender = ResolveSender(
            ("Sms:Provider", SmsOptions.NotifyLkProvider),
            ("Sms:NotifyLkUserId", "12345"),
            ("Sms:NotifyLkApiKey", "not-a-real-key"),
            ("Sms:SecondaryGateway", "https://sms.example.lk/send"),
            ("Sms:SecondaryApiKey", "also-not-real"));

        Assert.IsType<FallbackOtpSender>(sender);
        Assert.Equal("notifylk+secondary", sender.Provider);
    }

    [Fact]
    public void A_secondary_gateway_that_is_not_a_url_is_a_configuration_error()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(
                TestEnvironment.Development,
                ("Sms:Provider", SmsOptions.DevProvider),
                ("Sms:SecondaryGateway", "dialog-sms-gateway")));

        Assert.Contains("Sms:SecondaryGateway", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_provider_is_a_configuration_error()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(TestEnvironment.Development, ("Sms:Provider", "twilio")));

        Assert.Contains("Sms:Provider", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    private static IOtpSender ResolveSender(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIamServices(configuration, TestEnvironment.Development);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOtpSender>();
    }
}

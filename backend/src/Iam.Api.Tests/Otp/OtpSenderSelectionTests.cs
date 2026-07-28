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
    public void Selecting_notify_lk_fails_fast_rather_than_swallowing_every_otp()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(TestEnvironment.Development, ("Sms:Provider", SmsOptions.NotifyLkProvider)));

        Assert.Contains("C026", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_provider_is_a_configuration_error()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(TestEnvironment.Development, ("Sms:Provider", "twilio")));

        Assert.Contains("Sms:Provider", string.Join(' ', exception.Failures), StringComparison.Ordinal);
    }
}

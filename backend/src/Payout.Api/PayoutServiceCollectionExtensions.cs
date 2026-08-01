using MageRide.Payout.Configuration;
using MageRide.Payout.Payouts;
using MageRide.Payout.Persistence;
using MageRide.Payout.Wallet;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MageRide.Payout;

/// <summary>payout-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class PayoutServiceCollectionExtensions
{
    public static IServiceCollection AddPayoutServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PayoutOptions>()
            .Bind(configuration.GetSection(PayoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Stateless over the kernel's connection factory.
        services.TryAddSingleton<IPayoutRepository, PayoutRepository>();
        services.TryAddSingleton<IPayoutLedgerClient, PayoutLedgerClient>();
        services.TryAddSingleton<IBankOrigination, BankOrigination>();

        // Scoped: the runner resolves one per tick, and a request resolves one per request.
        services.TryAddScoped<PayoutRunService>();

        services.AddHostedService<PayoutRunner>();

        var settings = configuration.GetSection(PayoutOptions.SectionName).Get<PayoutOptions>() ?? new PayoutOptions();

        services.AddHttpClient(PayoutLedgerClient.HttpClientName, client =>
        {
            if (!string.IsNullOrWhiteSpace(settings.WalletBaseUrl))
            {
                client.BaseAddress = new Uri(settings.WalletBaseUrl.TrimEnd('/') + '/');
            }

            // D6' §8.3's internal hop. A payout debit is a small write against an indexed key.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient(BankOrigination.HttpClientName, client =>
        {
            if (!string.IsNullOrWhiteSpace(settings.BankBaseUrl))
            {
                client.BaseAddress = new Uri(settings.BankBaseUrl.TrimEnd('/') + '/');
            }

            // A bank is not a pod: its own budget rather than the internal one.
            client.Timeout = settings.BankTimeout;
        });

        return services;
    }
}

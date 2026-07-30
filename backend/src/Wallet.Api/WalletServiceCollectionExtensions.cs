using MageRide.Wallet.Caching;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Gateways;
using MageRide.Wallet.Ledger;
using MageRide.Wallet.Money;
using MageRide.Wallet.Persistence;

namespace MageRide.Wallet;

/// <summary>wallet-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class WalletServiceCollectionExtensions
{
    public static IServiceCollection AddWalletServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WalletOptions>()
            .Bind(configuration.GetSection(WalletOptions.SectionName))
            .Configure(options => BindD7SpelledVariables(configuration, options))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: every repository holds a connection factory and a handful of SQL strings.
        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<ILedgerRepository, LedgerRepository>();
        services.AddSingleton<ITopupRepository, TopupRepository>();
        services.AddSingleton<IVoucherRepository, VoucherRepository>();
        services.AddSingleton<ITransferRepository, TransferRepository>();

        services.AddSingleton<IWalletBalanceCache, WalletBalanceCache>();
        services.AddSingleton<ILedgerService, LedgerService>();

        services.AddSingleton<VoucherService>();
        services.AddSingleton<TransferService>();
        services.AddSingleton<TopupService>();

        // The LankaQR "gateway" makes no outbound call — AL-15's deep link and QR payload are composed
        // from the deployment's own templates — so it needs no HttpClient.
        services.AddSingleton<IPaymentGateway, LankaQrGateway>();

        // OnePay does. Through IHttpClientFactory so the socket is pooled and the timeout is the one
        // D6' §8.3 budgets rather than the .NET default of 100 seconds on a payment path.
        services.AddHttpClient<IPaymentGateway, OnepayGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<WalletOptions>>().Value;

            if (!string.IsNullOrWhiteSpace(options.OnepayBaseUrl))
            {
                client.BaseAddress = new Uri(
                    options.OnepayBaseUrl.EndsWith('/') ? options.OnepayBaseUrl : options.OnepayBaseUrl + "/");
            }

            client.Timeout = TimeSpan.FromSeconds(15);

            if (!string.IsNullOrWhiteSpace(options.OnepayApiKey))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {options.OnepayApiKey}");
            }
        });

        return services;
    }

    /// <summary>
    /// Honours the four variables D7' §4.2 spells without this service's prefix.
    /// </summary>
    /// <remarks>
    /// <c>Onepay__ApiKey</c>, <c>LankaQr__MerchantId</c>, <c>ComBankIpg__WebhookSecret</c> and
    /// <c>LowBalance__ThresholdMinor</c> are how §4.2 prints them and how <c>.env.app.example</c> ships
    /// them — a flat namespace shared by every co-located service. A <c>Wallet:*</c> value wins where
    /// both are set, so a deployment can move to the prefixed form without a flag day; an operator who
    /// followed the spec is never left setting a key nothing reads. The same problem content-svc's
    /// <c>Cache__Ttl</c> records.
    /// </remarks>
    private static void BindD7SpelledVariables(IConfiguration configuration, WalletOptions options)
    {
        options.OnepayApiKey ??= configuration["Onepay:ApiKey"];
        options.OnepayBaseUrl ??= configuration["Onepay:BaseUrl"];
        options.OnepayWebhookSecret ??= configuration["Onepay:WebhookSecret"];

        options.LankaQrMerchantId ??= configuration["LankaQr:MerchantId"];
        options.LankaQrDeepLinkTemplate ??= configuration["LankaQr:DeepLinkTemplate"];
        options.LankaQrPayloadTemplate ??= configuration["LankaQr:PayloadTemplate"];

        // D7' §4.2 gives the LankaQR confirm secret to the Commercial Bank IPG variable (D-12): the
        // bank is the acquirer for that rail, and §7.2 keeps the IPG webhook for settlement
        // reconciliation. `LankaQr:WebhookSecret` is accepted too, for a deployment that names it after
        // the rail rather than after the bank.
        options.LankaQrWebhookSecret ??=
            configuration["LankaQr:WebhookSecret"] ?? configuration["ComBankIpg:WebhookSecret"];

        if (configuration.GetSection($"{WalletOptions.SectionName}:LowBalanceThresholdMinor").Exists())
        {
            return;
        }

        if (long.TryParse(configuration["LowBalance:ThresholdMinor"], out var threshold) && threshold >= 0)
        {
            options.LowBalanceThresholdMinor = threshold;
        }
    }
}

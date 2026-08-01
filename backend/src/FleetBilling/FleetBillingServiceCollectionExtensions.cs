using MageRide.FleetBilling.Authorization;
using MageRide.FleetBilling.Billing;
using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Gateways;
using MageRide.FleetBilling.Money;
using MageRide.FleetBilling.Notifications;
using MageRide.FleetBilling.Persistence;
using MageRide.FleetBilling.Wallet;
using MageRide.Shared.Resilience;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling;

/// <summary>fleet-billing-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class FleetBillingServiceCollectionExtensions
{
    public static IServiceCollection AddFleetBillingServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FleetBillingOptions>()
            .Bind(configuration.GetSection(FleetBillingOptions.SectionName))
            .Configure(options => BindSharedSpellings(options, configuration))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: each holds a connection factory and a handful of SQL strings.
        services.AddSingleton<IFleetInvoiceRepository, FleetInvoiceRepository>();
        services.AddSingleton<IFleetAccessRepository, FleetAccessRepository>();
        services.AddSingleton<IFleetWalletRepository, FleetWalletRepository>();
        services.AddSingleton<IFleetTopupRepository, FleetTopupRepository>();

        // Scoped: each takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<IInvoiceRunService, InvoiceRunService>();
        services.AddScoped<IInvoiceSettlementService, InvoiceSettlementService>();
        services.AddScoped<IDunningService, DunningService>();
        services.AddScoped<IFleetTopupService, FleetTopupService>();

        // Scoped because it resolves the repository that decides the request.
        // `AddEndpointFilter<T>` resolves the filter from the request's own scope.
        services.AddScoped<FleetBillingAccessFilter>();

        AddOutboundClients(services, configuration);

        services.AddSingleton<FleetBillingRunner>();
        services.AddHostedService(provider => provider.GetRequiredService<FleetBillingRunner>());

        return services;
    }

    /// <summary>
    /// Also reads the four keys the neighbouring services already ship under their own prefixes.
    /// </summary>
    /// <remarks>
    /// D7' §4.2 predates fleet-billing-svc being split out of fleet-svc and gives it no variables, so
    /// a deployment that co-locates it with wallet-svc would otherwise have to set the same OnePay
    /// secret twice under two names — and the failure mode of forgetting is a callback that is
    /// refused and an organisation that is never credited. A <c>FleetBilling:*</c> value wins where
    /// both are set; subscription-svc (C047) reads <c>Wallet:*</c> the same way and for the same
    /// reason.
    /// </remarks>
    private static void BindSharedSpellings(FleetBillingOptions options, IConfiguration configuration)
    {
        options.WalletBaseUrl ??= configuration["Wallet:BaseUrl"];
        options.WalletInternalApiKey ??= configuration["Wallet:InternalApiKey"];

        options.OnepayApiKey ??= configuration["Onepay:ApiKey"];
        options.OnepayBaseUrl ??= configuration["Onepay:BaseUrl"];
        options.OnepayWebhookSecret ??= configuration["Onepay:WebhookSecret"];

        options.LankaQrDeepLinkTemplate ??= configuration["LankaQr:DeepLinkTemplate"];
        options.LankaQrPayloadTemplate ??= configuration["LankaQr:PayloadTemplate"];
        options.LankaQrMerchantId ??= configuration["LankaQr:MerchantId"];

        // D7' §4.2 spells the LankaQR confirm secret `ComBankIpg__WebhookSecret` (D-12); wallet-svc
        // honours both and so does this.
        options.LankaQrWebhookSecret ??=
            configuration["LankaQr:WebhookSecret"] ?? configuration["ComBankIpg:WebhookSecret"];

        options.NotificationBaseUrl ??= configuration["Notification:BaseUrl"];
        options.NotificationInternalApiKey ??= configuration["Notification:InternalApiKey"];
    }

    /// <summary>The three hops this service makes, each mapped only when it has somewhere to go.</summary>
    /// <remarks>
    /// <b>An unconfigured base address is a switch, not a default.</b> Without wallet-svc no invoice
    /// can be settled and no top-up credited, and both answer <c>503</c> rather than recording a
    /// payment that did not happen. Without OnePay the card rail answers <c>503</c> and LankaQR is
    /// unaffected (AL-05 leaves exactly two rails and there is no bank-transfer fallback). Without
    /// notification-svc an overdue invoice is still OVERDUE and the Fleet Portal still draws it; only
    /// the push is missing. Every one is announced at start-up.
    /// </remarks>
    private static void AddOutboundClients(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IFleetLedgerClient, FleetLedgerClient>();
        services.AddSingleton<IDunningNotifier, DunningNotifier>();

        // Always registered, even unconfigured: the client answers 503 with the reason, which is a
        // better failure than a missing registration's InvalidOperationException at request time.
        services.AddHttpClient(FleetLedgerClient.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<FleetBillingOptions>>().Value;

                if (!string.IsNullOrWhiteSpace(options.WalletBaseUrl))
                {
                    client.BaseAddress = new Uri(options.WalletBaseUrl, UriKind.Absolute);
                }

                client.Timeout = options.WalletTimeout;
            })
            // D6' §8.3's internal-hop pipeline. Safe here because every call it carries is
            // idempotent by construction — the debit and the credit collide on the ledger's UNIQUE
            // idempotency key, and the account resolve collides on `ux_accounts_owner`.
            .AddMageRideResilience();

        services.AddHttpClient(OnepayFleetGateway.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<FleetBillingOptions>>().Value;

                if (!string.IsNullOrWhiteSpace(options.OnepayBaseUrl))
                {
                    client.BaseAddress = new Uri(options.OnepayBaseUrl, UriKind.Absolute);
                }

                if (!string.IsNullOrWhiteSpace(options.OnepayApiKey))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", options.OnepayApiKey);
                }
            });

        // No retry on the gateway hop. A retried create-session is a second payment page for the
        // same money, and the operator can be looking at the first one.
        services.AddSingleton<IFleetPaymentGateway>(provider => new OnepayFleetGateway(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(OnepayFleetGateway.HttpClientName),
            provider.GetRequiredService<IOptions<FleetBillingOptions>>(),
            provider.GetRequiredService<ILogger<OnepayFleetGateway>>()));

        // No outbound call at all — AL-15's deep link and the QR payload are composed from the
        // deployment's own templates.
        services.AddSingleton<IFleetPaymentGateway, LankaQrFleetGateway>();

        if (!string.IsNullOrWhiteSpace(configuration[
                $"{FleetBillingOptions.SectionName}:{nameof(FleetBillingOptions.NotificationBaseUrl)}"])
            || !string.IsNullOrWhiteSpace(configuration["Notification:BaseUrl"]))
        {
            services.AddHttpClient(DunningNotifier.HttpClientName)
                .ConfigureHttpClient((provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<FleetBillingOptions>>().Value;

                    client.BaseAddress = new Uri(options.NotificationBaseUrl!, UriKind.Absolute);
                    client.Timeout = options.NotificationTimeout;
                });
        }
    }
}

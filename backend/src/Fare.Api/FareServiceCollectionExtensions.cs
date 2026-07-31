using MageRide.Fare.Configuration;
using MageRide.Fare.Estimates;
using MageRide.Fare.Gateways;
using MageRide.Fare.Payments;
using MageRide.Fare.Persistence;
using MageRide.Fare.Pricing;
using MageRide.Fare.Settlement;

namespace MageRide.Fare;

/// <summary>fare-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class FareServiceCollectionExtensions
{
    public static IServiceCollection AddFareServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FareOptions>()
            .Bind(configuration.GetSection(FareOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: every repository holds a connection factory and a handful of SQL strings.
        services.AddSingleton<ITariffRepository, TariffRepository>();
        services.AddSingleton<IRideRepository, RideRepository>();
        services.AddSingleton<ITrackRepository, TrackRepository>();
        services.AddSingleton<IRidePaymentRepository, RidePaymentRepository>();
        services.AddSingleton<IDriverEarningsRepository, DriverEarningsRepository>();
        services.AddSingleton<IRefundRepository, RefundRepository>();
        services.AddSingleton<ISupportTicketRepository, SupportTicketRepository>();
        services.AddSingleton<IDriverPayoutRepository, DriverPayoutRepository>();

        services.AddSingleton<FarePricingService>();
        services.AddSingleton<FareEstimator>();

        services.AddSingleton<IPenaltyClient, PenaltyClient>();
        services.AddSingleton<IWalletLedgerClient, WalletLedgerClient>();
        services.AddSingleton<IRideSettlementClient, RideSettlementClient>();

        // Δ C050 — the payment machine. Two gateways behind one seam, resolved by method.
        services.AddSingleton<IFareGateway, OnepayFareGateway>();
        services.AddSingleton<IFareGateway, LankaQrFareGateway>();
        services.AddSingleton<PaymentSettlementService>();

        // Scoped: it takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<FareSettlementService>();

        // Scoped for the same reason: each takes IUnitOfWorkFactory.
        services.AddScoped<PaymentService>();
        services.AddScoped<DriverQrService>();
        services.AddScoped<CallbackService>();
        services.AddScoped<RefundService>();

        services.AddHostedService<QrNudgeSweeper>();

        var settings = configuration.GetSection(FareOptions.SectionName).Get<FareOptions>() ?? new FareOptions();

        // Named clients resolved through IHttpClientFactory rather than typed clients: both
        // consumers above are singletons, and a singleton that captures a typed HttpClient pins one
        // message handler for the process's lifetime — which is how a service stops noticing that
        // its dependency moved. The same shape subscription-svc's wallet seam uses.
        services.AddHttpClient(PenaltyClient.HttpClientName, client =>
            ConfigureInternal(client, settings.DispatchBaseUrl, settings.InternalTimeout));

        services.AddHttpClient(WalletLedgerClient.HttpClientName, client =>
            ConfigureInternal(client, settings.WalletBaseUrl, settings.InternalTimeout));

        services.AddHttpClient(RideSettlementClient.HttpClientName, client =>
            ConfigureInternal(client, settings.RideBaseUrl, settings.InternalTimeout));

        // The gateway hop is a payment provider on the public internet, not an internal service, so
        // it gets its own budget rather than D6' §8.3's 2 s.
        services.AddHttpClient(OnepayFareGateway.HttpClientName, client =>
        {
            ConfigureInternal(client, settings.OnepayBaseUrl, TimeSpan.FromSeconds(20));

            if (!string.IsNullOrWhiteSpace(settings.OnepayApiKey))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {settings.OnepayApiKey}");
            }
        });

        return services;
    }

    private static void ConfigureInternal(HttpClient client, string? baseUrl, TimeSpan timeout)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        }

        client.Timeout = timeout;
    }
}

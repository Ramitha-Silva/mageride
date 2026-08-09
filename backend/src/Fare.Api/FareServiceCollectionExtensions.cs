using MageRide.Fare.Configuration;
using MageRide.Fare.Estimates;
using MageRide.Fare.Gateways;
using MageRide.Fare.Observability;
using MageRide.Fare.Payments;
using MageRide.Fare.Persistence;
using MageRide.Fare.Pricing;
using MageRide.Fare.Settlement;
using MageRide.Shared.Observability;

namespace MageRide.Fare;

/// <summary>fare-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class FareServiceCollectionExtensions
{
    /// <summary>The service's scrape-time gauges (C119). One meter, disposed with the host.</summary>
    private static ScrapedGauges Gauges(IServiceProvider services)
    {
        var gauges = new ScrapedGauges(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<ScrapedGauges>());

        OverpaidGauge.Publish(gauges);

        return gauges;
    }

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

        services.AddSingleton<FarePricingService>();
        services.AddSingleton<FareEstimator>();

        services.AddSingleton<IPenaltyClient, PenaltyClient>();
        services.AddSingleton<IWalletLedgerClient, WalletLedgerClient>();
        services.AddSingleton<IRideSettlementClient, RideSettlementClient>();

        // Δ AL-57/AL-59 — the two ride gateways and the D-11 merchant repository are gone. No ride
        // fare reaches an acquirer, so there is no session to open and no merchant to look up; the
        // `wallet` rail posts through IWalletLedgerClient above, which is registered already.
        services.AddSingleton<PaymentSettlementService>();

        // Scoped: it takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<FareSettlementService>();

        // Scoped for the same reason: each takes IUnitOfWorkFactory.
        services.AddScoped<PaymentService>();
        services.AddScoped<DriverQrService>();
        services.AddScoped<RefundService>();

        services.AddHostedService<QrNudgeSweeper>();

        // Δ C119 (R-20). ADD §13.3.1 row 7 as a gauge on the platform meter. A singleton with its
        // own Meter, started as a hosted service so the gauge exists from the first scrape rather
        // than from the first request — nothing else resolves it, because a scrape reads the meter.
        services.AddSingleton(sp => Gauges(sp));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ScrapedGauges>());

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

        // Δ AL-57 — the OnePay ride client is gone with the rail. The only OnePay integration left
        // on the platform is wallet-svc's top-up, where MageRide is the payee and one merchant
        // account is exactly right.

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

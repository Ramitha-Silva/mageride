using MageRide.Fare.Configuration;
using MageRide.Fare.Estimates;
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

        services.AddSingleton<FarePricingService>();
        services.AddSingleton<FareEstimator>();

        services.AddSingleton<IPenaltyClient, PenaltyClient>();
        services.AddSingleton<IWalletLedgerClient, WalletLedgerClient>();

        // Scoped: it takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<FareSettlementService>();

        var settings = configuration.GetSection(FareOptions.SectionName).Get<FareOptions>() ?? new FareOptions();

        // Named clients resolved through IHttpClientFactory rather than typed clients: both
        // consumers above are singletons, and a singleton that captures a typed HttpClient pins one
        // message handler for the process's lifetime — which is how a service stops noticing that
        // its dependency moved. The same shape subscription-svc's wallet seam uses.
        services.AddHttpClient(PenaltyClient.HttpClientName, client =>
            ConfigureInternal(client, settings.DispatchBaseUrl, settings.InternalTimeout));

        services.AddHttpClient(WalletLedgerClient.HttpClientName, client =>
            ConfigureInternal(client, settings.WalletBaseUrl, settings.InternalTimeout));

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

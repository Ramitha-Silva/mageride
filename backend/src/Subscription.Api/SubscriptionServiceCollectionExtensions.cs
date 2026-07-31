using MageRide.Shared.Resilience;
using MageRide.Subscriptions.Configuration;
using MageRide.Subscriptions.Fees;
using MageRide.Subscriptions.ModeB;
using MageRide.Subscriptions.Persistence;
using MageRide.Subscriptions.Wallet;

namespace MageRide.Subscriptions;

/// <summary>subscription-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class SubscriptionServiceCollectionExtensions
{
    public static IServiceCollection AddSubscriptionServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SubscriptionOptions>()
            .Bind(configuration.GetSection(SubscriptionOptions.SectionName))
            .Configure(options => BindWalletSpelledVariables(configuration, options))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: every repository holds a connection factory and a handful of SQL strings.
        services.AddSingleton<IPlanRepository, PlanRepository>();
        services.AddSingleton<IDailyFeeRepository, DailyFeeRepository>();
        services.AddSingleton<IVehicleRepository, VehicleRepository>();
        services.AddSingleton<IVoucherTierRepository, VoucherTierRepository>();
        services.AddSingleton<IModeBBillingRepository, ModeBBillingRepository>();
        services.AddSingleton<IRefundRequestRepository, RefundRequestRepository>();
        services.AddSingleton<IModeBRegistryRepository, ModeBRegistryRepository>();
        services.AddSingleton<IModeBAccessRepository, ModeBAccessRepository>();
        services.AddSingleton<ISubscriptionPaymentRepository, SubscriptionPaymentRepository>();

        services.AddSingleton<DailyFeeService>();
        services.AddSingleton<ModeBBillingService>();

        // Epic 23. Scoped rather than singleton: both take IUnitOfWorkFactory, which the kernel
        // registers scoped so one request holds at most one transaction at a time.
        services.AddScoped<ModeBAccessService>();
        services.AddScoped<ModeBPaymentService>();

        services.AddSingleton<IModeBFileLinks, ModeBFileLinks>();
        services.AddSingleton<ITransferSlipStore, FileSystemTransferSlipStore>();

        services.AddSingleton<IWalletLedgerClient, WalletLedgerClient>();
        services.AddSingleton<IWalletForwarder, WalletForwarder>();

        // Named clients resolved through IHttpClientFactory, not typed clients: the two consumers above
        // are singletons, and a singleton that captures a typed HttpClient pins one message handler for
        // the process's lifetime — which is how a service stops noticing that its dependency moved. The
        // same shape query-svc's geocoder uses.
        var settings = configuration.GetSection(SubscriptionOptions.SectionName).Get<SubscriptionOptions>()
                       ?? new SubscriptionOptions();

        BindWalletSpelledVariables(configuration, settings);

        // Retry is safe on the ledger seam — and only because the debit's idempotency key is the
        // business fact, so wallet-svc replays rather than re-executes. The attempt timeout is this
        // service's own budget rather than D6' §8.3's 15 s default: the whole sequence runs inside a
        // 15 s offer window, and a driver who waits 45 s for an accept has lost the ride either way.
        services.AddHttpClient(WalletLedgerClient.HttpClientName, client => ConfigureWalletClient(settings, client))
            .AddMageRideResilience(new ResilienceOptions
            {
                MaxRetryAttempts = 1,
                AttemptTimeout = settings.WalletTimeout,
            });

        services.AddHttpClient(WalletForwarder.HttpClientName, client =>
        {
            ConfigureWalletClient(settings, client);

            // A driver's transfer or voucher purchase reaches a payment gateway at the far end, which is
            // a different budget from the ledger seam's 2 s. No resilience pipeline: a proxy must not
            // invent retries its caller did not ask for.
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddHostedService<ModeBBillingRunner>();

        return services;
    }

    private static void ConfigureWalletClient(SubscriptionOptions options, HttpClient client)
    {
        if (!string.IsNullOrWhiteSpace(options.WalletBaseUrl))
        {
            client.BaseAddress = new Uri(
                options.WalletBaseUrl.EndsWith('/') ? options.WalletBaseUrl : options.WalletBaseUrl + "/");
        }

        client.Timeout = options.WalletTimeout;
    }

    /// <summary>
    /// Honours <c>Wallet:InternalApiKey</c> and <c>Wallet:BaseUrl</c> as well as this service's own keys.
    /// </summary>
    /// <remarks>
    /// The internal key is one secret shared by the two ends of one seam, and
    /// <c>infra/env/.env.app.example</c> ships it as <c>Wallet__InternalApiKey</c> for the service that
    /// checks it. A deployment that co-locates both — which D7' §2.1's <c>app-services</c> container
    /// does — would otherwise have to set the same value twice under two names, and the failure of
    /// forgetting is silent: fees stop being charged and nothing errors. A <c>Subscription:*</c> value
    /// wins where both are set, so a split deployment can still give this service its own.
    /// </remarks>
    private static void BindWalletSpelledVariables(IConfiguration configuration, SubscriptionOptions options)
    {
        options.WalletInternalApiKey ??= configuration["Wallet:InternalApiKey"];
        options.WalletBaseUrl ??= configuration["Wallet:BaseUrl"];
    }
}

using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Subscriptions.Configuration;
using MageRide.Subscriptions.Endpoints;
using MageRide.Subscriptions.Wallet;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions;

/// <summary>
/// Composition root for subscription-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class SubscriptionApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "subscription-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            UsePostgres = true,

            // No Redis. The D-08 balance cache belongs to dispatch-svc (reader) and wallet-svc
            // (writer); this service never reads a balance — it asks wallet-svc to move money and is
            // told 402 if there is not enough. A cache read here would be a third opinion about one
            // number.
            UseRedis = false,

            // No Kafka and no outbox, and there must not be. The daily fee's event is
            // `wallet.debited`, which wallet-svc emits inside the transaction that posts the money
            // (R-13) — an event published from here would describe a movement this service did not
            // make and could not roll back. The Mode B hand-off to C060 is a table it reads, not a
            // message it waits for.
            UseKafka = false,
            UseOutbox = false,

            // R-14. The refund intake is the route that needs it: a proxy retry or a double tap would
            // put a second identical ticket on the Support queue, and no natural key would collide.
            // The internal fee routes opt out individually — their key is the Colombo day itself.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // rides.command_log; this service's is `subscription.command_log` (migration 1203), with no
        // aggregate-id column because a refund request targets a support ticket that does not exist yet.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "subscription";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddSubscriptionServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapFeeEndpoints();
        app.MapAdminFeeEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<SubscriptionOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalFeeEndpoints(settings.InternalApiKey);
        }

        if (!string.IsNullOrWhiteSpace(settings.WalletBaseUrl))
        {
            app.MapCreditEndpoints();
        }

        WarnAboutFeesThatCannotBeCollected(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service cannot collect anything.
    /// </summary>
    /// <remarks>
    /// The same rule query-svc, fanout-svc, fleet-health-svc, content-svc and wallet-svc are written
    /// under, and it matters here for wallet-svc's reason: every failure below is silent from the
    /// outside. Trips are accepted, months roll over, nothing errors, and the platform's only revenue
    /// quietly does not arrive.
    /// </remarks>
    private static void WarnAboutFeesThatCannotBeCollected(WebApplication app, SubscriptionOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(options.WalletBaseUrl) || string.IsNullOrWhiteSpace(options.WalletInternalApiKey))
        {
            logger.LogError(
                "Subscription:WalletBaseUrl / Subscription:WalletInternalApiKey is not configured, so "
                + "POST /v1/internal/fees/{{driverId}}/charge-before-trip answers 503 and no daily "
                + "platform fee can be charged. This service writes no ledger row of its own — the debit "
                + "is wallet-svc's POST /v1/internal/wallet/{{driverId}}/debit and there is no fallback.");
        }

        if (string.IsNullOrWhiteSpace(options.WalletBaseUrl))
        {
            logger.LogWarning(
                "Subscription:WalletBaseUrl is not configured, so /v1/vouchers/purchase, "
                + "/v1/transfers/driver and /v1/subscriptions/credit-transfer/** are unmapped. Those "
                + "routes forward to wallet-svc, which serves the same operations under /v1/wallet/** — "
                + "a driver's app pointed at those paths is unaffected.");
        }

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            logger.LogWarning(
                "Subscription:InternalApiKey is not configured, so /v1/internal/fees/** is unmapped. "
                + "ride-svc cannot charge the D-13 daily fee on a second trip — which fails inside "
                + "ride-svc rather than here — and the Mode B monthly run can only be triggered by this "
                + "service's own background runner.");
        }

        if (!options.ModeBBillingEnabled)
        {
            logger.LogWarning(
                "Subscription:ModeBBillingEnabled is off: nothing raises billing.monthly_subscriptions, "
                + "so no Mode B vehicle is charged the ~Rs 300 monthly platform fee and "
                + "fleet-billing-svc (C060) has no lines to consolidate. The month simply passes.");
        }

        if (options.FreeTripsPerDay != 1)
        {
            logger.LogWarning(
                "Subscription:FreeTripsPerDay is {FreeTrips}, not 1. US-9.1 is a P0 requirement that the "
                + "first trip of each Asia/Colombo day is free and that the fee falls due before the "
                + "second; this deployment charges from trip {ChargedFrom}.",
                options.FreeTripsPerDay,
                options.FreeTripsPerDay + 1);
        }
    }
}

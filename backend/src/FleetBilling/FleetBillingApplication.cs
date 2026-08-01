using MageRide.FleetBilling.Configuration;
using MageRide.FleetBilling.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using Microsoft.Extensions.Options;

namespace MageRide.FleetBilling;

/// <summary>
/// Composition root for fleet-billing-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class FleetBillingApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "fleet-billing-svc";

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

            // **No Redis.** Nothing here is on a hot path and nothing is cached across replicas. The
            // D-08 balance cache belongs to dispatch-svc (reader) and wallet-svc (writer), and it is
            // a *driver's* balance: a fleet's is read once when an operator opens a billing screen
            // and once per settlement attempt. A cache here would be a third opinion about a number
            // §10 already makes `billing.accounts` the master of.
            UseRedis = false,

            // `fleet.invoice_issued` / `_paid` / `_overdue` go into billing.fleet_outbox (migration
            // 1108) inside the transaction that changes the invoice, and the kernel's LISTEN/NOTIFY
            // dispatcher drains them to `fleet.events` after COMMIT (E-09, R-13). An event that
            // committed and failed to publish would tell the Fleet Portal an invoice was paid that
            // was not; one published without its state change would be worse.
            UseKafka = true,
            UseOutbox = true,

            // R-14. `POST …/wallet/topup` opens a payment session, and a retried request that
            // re-executed would open a second one against the same money. The two provider callbacks
            // and the internal run route opt out individually, each because it already carries a
            // stronger key of its own (R-19's provider transaction id, and the Colombo month).
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins, but this service's own
        // tables are the default. The kernel's defaults describe rides.outbox → `ride.events` and
        // rides.command_log; both live in `billing` here (migration 1108). Deliberately *not*
        // wallet-svc's `billing.outbox` and `billing.command_log`: that outbox drains to
        // `wallet.events` and one table cannot serve two dispatchers publishing to two topics, and
        // that command log's primary key is the bare idempotency key, so two services sharing it
        // would let one client's `Idempotency-Key` collide across a service boundary.
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "billing";
            outbox.Table = "fleet_outbox";
            outbox.Channel = "billing_fleet_outbox";
            outbox.Topic = EventTopics.FleetEvents;
        });

        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "billing";
            commandLog.Table = "fleet_command_log";

            // No aggregate-id column: a top-up targets a session that does not exist yet.
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddFleetBillingServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapFleetBillingEndpoints();
        app.MapTopupCallbackEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<FleetBillingOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalFleetBillingEndpoints(settings.InternalApiKey);
        }

        WarnAboutMoneyThatCannotMove(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service cannot move money.
    /// </summary>
    /// <remarks>
    /// The rule wallet-svc, subscription-svc, fleet-svc and content-svc are written under, and it
    /// matters here for wallet-svc's reason: <b>every failure below is silent from the outside.</b>
    /// Months roll over, operators run buses, nothing errors, and the platform's fleet revenue simply
    /// does not arrive — or arrives and is never collected, which looks identical on every screen.
    /// </remarks>
    private static void WarnAboutMoneyThatCannotMove(WebApplication app, FleetBillingOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(options.WalletBaseUrl) || string.IsNullOrWhiteSpace(options.WalletInternalApiKey))
        {
            logger.LogError(
                "FleetBilling:WalletBaseUrl / FleetBilling:WalletInternalApiKey is not configured: NO FLEET "
                + "INVOICE CAN EVER BE SETTLED and no top-up can be credited. Invoices are still raised, so "
                + "the Fleet Portal shows a growing unpaid balance and every settlement attempt answers 503. "
                + "billing.journal_postings has exactly one writer (D-09) and this is the only way to reach "
                + "it.");
        }

        if (!options.InvoicingEnabled)
        {
            logger.LogError(
                "FleetBilling:InvoicingEnabled is false: NO FLEET IS EVER INVOICED. subscription-svc keeps "
                + "raising per-vehicle Mode B charges into billing.monthly_subscriptions and nothing "
                + "consolidates them, which from the Fleet Portal is indistinguishable from a platform that "
                + "does not charge fleets. POST /v1/internal/fleet-billing/run still works.");
        }
        else if (!options.AutoSettle)
        {
            logger.LogWarning(
                "FleetBilling:AutoSettle is false: an invoice is only ever settled when somebody presses Pay "
                + "in the Fleet Portal. Every organisation that does not will go OVERDUE with a wallet that "
                + "could have covered it.");
        }

        if (string.IsNullOrWhiteSpace(options.OnepayWebhookSecret))
        {
            logger.LogError(
                "Onepay:WebhookSecret is not configured: every OnePay fleet top-up callback is refused, so an "
                + "operator can start a card top-up, pay at the gateway, and never be credited. There is no "
                + "unsigned mode — a wallet-credit endpoint that trusts an unsigned body is a free-money "
                + "endpoint.");
        }

        if (string.IsNullOrWhiteSpace(options.LankaQrWebhookSecret))
        {
            logger.LogError(
                "Neither LankaQr:WebhookSecret nor ComBankIpg:WebhookSecret is configured: every LankaQR "
                + "confirm callback is refused, and an operator who paid through their bank app is never "
                + "credited.");
        }

        if (string.IsNullOrWhiteSpace(options.OnepayApiKey) || string.IsNullOrWhiteSpace(options.OnepayBaseUrl))
        {
            logger.LogWarning(
                "Onepay:ApiKey / Onepay:BaseUrl is not configured: the card rail answers 503 and LankaQR is "
                + "the only way to top up a fleet wallet. AL-05 leaves exactly these two rails and there is "
                + "no bank-transfer fallback.");
        }

        if (string.IsNullOrWhiteSpace(options.LankaQrDeepLinkTemplate))
        {
            logger.LogWarning(
                "LankaQr:DeepLinkTemplate is not configured: the LankaQR rail answers 503. AL-15 makes the "
                + "bank-app deep link the primary path and the QR the fallback, so there is nothing to serve "
                + "without it.");
        }

        if (string.IsNullOrWhiteSpace(options.NotificationBaseUrl))
        {
            logger.LogWarning(
                "FleetBilling:NotificationBaseUrl is not configured: an overdue invoice is recorded OVERDUE "
                + "and published on fleet.events, and nobody's phone rings (US-13.10). The Fleet Portal can "
                + "still draw it.");
        }

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            logger.LogWarning(
                "FleetBilling:InternalApiKey is not configured, so /v1/internal/fleet-billing/** is unmapped. "
                + "A month that was missed cannot be invoiced on demand — only by the hourly runner, and only "
                + "for the current Colombo month.");
        }
    }
}

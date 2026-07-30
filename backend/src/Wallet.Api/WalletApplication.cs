using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Endpoints;
using Microsoft.Extensions.Options;

namespace MageRide.Wallet;

/// <summary>
/// Composition root for wallet-svc. Lives here rather than in <c>Program.cs</c> so the test suite drives
/// the same pipeline the process runs.
/// </summary>
public static class WalletApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "wallet-svc";

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

            // Redis carries the D-08 balance cache dispatch-svc gates every second trip on. This service
            // is its writer; nothing here reads it.
            UseRedis = true,

            // `wallet.debited` / `wallet.credited` / `wallet.low_balance` go into billing.outbox inside
            // the transaction that posts the money, and the kernel's LISTEN/NOTIFY dispatcher drains them
            // to `wallet.events` after COMMIT (E-09, R-13). An event that committed and failed to publish
            // would leave dispatch-svc gating on a stale balance; one published without its postings
            // would have it gating on money nobody has.
            UseKafka = true,
            UseOutbox = true,

            // R-14. Every POST here moves money or opens a payment session, and a retried request that
            // re-executed would double it — so the replay log is not optional on this surface. The two
            // internal ledger routes and the two provider callbacks opt out individually, each because it
            // already carries a stronger key of its own (the ledger's, and R-19's).
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins, but this service's own tables
        // are the default. The kernel's defaults describe rides.outbox → `ride.events` and
        // rides.command_log; both live in `billing` here (migration 1107), and the command log has no
        // aggregate-id column because a top-up targets no aggregate that exists yet.
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "billing";
            outbox.Channel = "billing_outbox";
            outbox.Topic = EventTopics.WalletEvents;
        });

        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "billing";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddWalletServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapWalletEndpoints();
        app.MapTopupEndpoints();
        app.MapVoucherEndpoints();
        app.MapCreditTransferEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<WalletOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalWalletEndpoints(settings.InternalApiKey);
        }

        WarnAboutMoneyThatCannotMove(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service cannot move money.
    /// </summary>
    /// <remarks>
    /// The same rule query-svc, fanout-svc, fleet-health-svc and content-svc are written under, and it
    /// matters more here: every failure below is silent from the inside. A driver pays at a gateway and
    /// is never credited; a fleet's daily fee is never charged; a top-up never reaches the dispatcher.
    /// Nothing errors, and the money simply does not arrive.
    /// </remarks>
    private static void WarnAboutMoneyThatCannotMove(WebApplication app, WalletOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(options.OnepayWebhookSecret))
        {
            logger.LogError(
                "Onepay:WebhookSecret is not configured: every OnePay top-up callback is refused, so a "
                + "driver can start a card top-up, pay at the gateway, and never be credited. There is no "
                + "unsigned mode — a wallet-credit endpoint that trusts an unsigned body is a free-money "
                + "endpoint.");
        }

        if (string.IsNullOrWhiteSpace(options.LankaQrWebhookSecret))
        {
            logger.LogError(
                "Neither LankaQr:WebhookSecret nor ComBankIpg:WebhookSecret is configured: every LankaQR "
                + "confirm callback is refused, and a driver who paid through their bank app is never "
                + "credited.");
        }

        if (string.IsNullOrWhiteSpace(options.OnepayApiKey) || string.IsNullOrWhiteSpace(options.OnepayBaseUrl))
        {
            logger.LogWarning(
                "Onepay:ApiKey / Onepay:BaseUrl is not configured: POST /v1/wallet/topup/onepay answers "
                + "503 and the card rail is unavailable. LankaQR is unaffected (AL-05 leaves exactly these "
                + "two rails, and there is no bank-transfer fallback).");
        }

        if (string.IsNullOrWhiteSpace(options.LankaQrDeepLinkTemplate))
        {
            logger.LogWarning(
                "LankaQr:DeepLinkTemplate is not configured: POST /v1/wallet/topup/lankaqr answers 503. "
                + "AL-15 makes the bank-app deep link the primary path and the QR the fallback, so there "
                + "is nothing to serve without it.");
        }
        else if (string.IsNullOrWhiteSpace(options.LankaQrPayloadTemplate))
        {
            logger.LogInformation(
                "LankaQr:PayloadTemplate is not set: the AL-15 QR fallback is omitted and the deep link is "
                + "the only route. Deliberate — an EMVCo payload belongs to the acquiring bank and a "
                + "composed one would be a plausible, unscannable code.");
        }

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            logger.LogWarning(
                "Wallet:InternalApiKey is not configured, so /v1/internal/wallet/** is unmapped. "
                + "subscription-svc cannot charge the D-13 daily fee, fare-svc cannot settle a penalty or "
                + "pay out a tip, and admin-bff cannot reverse a fee — each of which fails inside that "
                + "service rather than here.");
        }

        if (!options.BalanceCacheEnabled)
        {
            logger.LogWarning(
                "Wallet:BalanceCacheEnabled is off: wallet:bal:{{driverId}} is not written, so a top-up is "
                + "invisible to dispatch-svc's D-08 gate for up to its own 5 s TTL and a debited driver "
                + "stays dispatchable for the same window.");
        }
    }
}

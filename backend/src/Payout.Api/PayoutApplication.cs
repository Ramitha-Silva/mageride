using MageRide.Payout.Configuration;
using MageRide.Payout.Endpoints;
using MageRide.Shared;
using Microsoft.Extensions.Options;

namespace MageRide.Payout;

/// <summary>
/// Composition root for payout-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class PayoutApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "payout-svc";

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

            // **No Redis.** Nothing here is on a hot path: a sweep runs weekly and the two reads a
            // screen makes are indexed. A cached balance would be a second opinion about the number
            // §10 already makes `billing.accounts` the master of — and this service debits against
            // it, so a stale one would sweep the wrong amount.
            UseRedis = false,

            // **No Kafka and no outbox.** The durable record of a payout is the `billing.payouts`
            // row, which Finance reads directly; D6' §2.1 registers no topic for this service and no
            // consumer waits on one. The notification a driver eventually gets ("your payout is on
            // its way") is notification-svc's and has no template yet — named in the handoff rather
            // than invented here.
            UseKafka = false,
            UseOutbox = false,

            // **No command log.** The two mutations are idempotent by a stronger key than a header:
            // the sweep on `run_date` (UNIQUE) and the bank result on `provider_reference` plus a
            // guarded status transition. A fifteenth instance of D4' §5's gap would guard operations
            // that already cannot double-apply.
            UseCommandLog = false,

            UseAuthentication = true,
        };

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddPayoutServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapPayoutEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<PayoutOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalPayoutEndpoints(settings.InternalApiKey);
        }

        Announce(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which of this service's switches is off.
    /// </summary>
    /// <remarks>
    /// Every one of them fails <em>silently</em> and looks like normal operation from the outside:
    /// a wallet balance that keeps growing looks like a busy week, and an instruction resting at
    /// PENDING looks like a bank that is slow. This is the only place the difference is stated.
    /// </remarks>
    private static void Announce(WebApplication app, PayoutOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (!options.Enabled)
        {
            logger.LogError(
                "Payout:Enabled is off. NO driver is ever swept and every balance grows without bound. The "
                + "money is safe on their wallets and nothing is lost — but nobody is paid, and nothing else "
                + "on this platform will say so.");
        }

        if (string.IsNullOrWhiteSpace(options.WalletBaseUrl) || string.IsNullOrWhiteSpace(options.WalletInternalApiKey))
        {
            logger.LogError(
                "Payout:WalletBaseUrl / Payout:WalletInternalApiKey is unset, so no sweep can debit anything. "
                + "The run still selects and reports what it would have moved, and every driver keeps every "
                + "rupee — but no instruction is ever raised.");
        }

        if (string.IsNullOrWhiteSpace(options.BankBaseUrl))
        {
            logger.LogError(
                "Payout:BankBaseUrl is unset, so instructions are raised and rest at PENDING. This is the "
                + "designed state and not a fault: the debit is made and the row records what is owed, so the "
                + "liability is visible before a rail exists. ⚠ Origination needs a sponsor bank and CBSL "
                + "authorisation (ADD §1.18) — a go-live gate, not an engineering task.");
        }

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            logger.LogError(
                "Payout:InternalApiKey is unset, so POST /v1/internal/payouts/{{id}}/result is NOT MAPPED. A "
                + "bank can never report an outcome and every instruction stays SUBMITTED for ever.");
        }

        logger.LogInformation(
            "payout-svc is up: a full sweep every {RunDay} (Asia/Colombo), retaining {RetainMinor} minor units, "
            + "polling every {PollInterval}. A driver with no verified payout profile accrues and is never swept.",
            options.RunDay,
            options.RetainMinor,
            options.PollInterval);
    }
}

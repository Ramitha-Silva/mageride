using MageRide.Fare.Configuration;
using MageRide.Fare.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Fares;
using MageRide.Shared.Http.Idempotency;
using Microsoft.Extensions.Options;

namespace MageRide.Fare;

/// <summary>
/// Composition root for fare-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class FareApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "fare-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // Δ C049: the C022 stub held no state at all. This component reads the tariff table, the
            // ride it is pricing and the track it is measuring, and writes the payment row — so
            // Postgres is on and its readiness probe now means something.
            UsePostgres = true,

            // No Redis. The tariff table is single-digit rows behind an index and the fare path
            // already crosses the network twice; a cache would buy microseconds and cost an
            // invalidation protocol with admin-bff, whose whole point is that a published rate takes
            // effect. `effective_from` versioning exists precisely so nothing has to be invalidated.
            UseRedis = false,

            // No Kafka and no outbox. The events a fare produces are R-05's terminal — and that is
            // published by *ride-svc*, through POST /v1/internal/rides/{id}/payment-settled, which
            // C050 calls. An event emitted here would describe a settlement this component does not
            // make. C050 revisits this with the payment machine.
            UseKafka = false,
            UseOutbox = false,

            // R-14. POST /v1/fare/calculate carries an Idempotency-Key in the contract, and the
            // replay log is what honours it — though the load-bearing guard is the FOR UPDATE on the
            // ride's payment row, because a header dedupes identical requests and what must be
            // single-shot is the ride.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // rides.command_log; this service's is `fares.command_log` (migration 1005), with no
        // aggregate-id column because the ride a calculation names is not an aggregate this service
        // owns.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "fares";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);

        // The issuing half of the fareEstimateToken contract; ride-svc registers the same codec and
        // verifies with the same key.
        builder.Services.AddMageRideFareTokens(builder.Configuration);
        builder.Services.AddFareServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapFareEndpoints();
        app.MapPaymentEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<FareOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalFareEndpoints(settings.InternalApiKey);
        }

        WarnAboutFaresThatCannotBeCollected(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service cannot charge or collect.
    /// </summary>
    /// <remarks>
    /// The same rule wallet-svc, subscription-svc, query-svc, fanout-svc and content-svc are written
    /// under, and it matters here for the same reason: every failure below is silent from the
    /// outside. Rides complete, drivers are paid what the passenger hands them, nothing errors — and
    /// the platform's record of what was owed is quietly wrong.
    /// </remarks>
    private static void WarnAboutFaresThatCannotBeCollected(WebApplication app, FareOptions options)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            logger.LogError(
                "Fare:InternalApiKey is not configured, so POST /v1/fare/calculate is not mapped. ride-svc "
                + "cannot compute a final fare, every completed ride stalls in PaymentPending, and no "
                + "fares.ride_payments row is ever created.");
        }

        if (!options.PenaltySettlementEnabled || string.IsNullOrWhiteSpace(options.DispatchBaseUrl))
        {
            logger.LogWarning(
                "The D-05 cross-trip settlement is off (PenaltySettlementEnabled={Enabled}, DispatchBaseUrl "
                + "{Configured}). A passenger who cancels after an accept is never charged the Rs 50 and the "
                + "driver they stood up is never compensated; the debt accrues on "
                + "dispatch.cancellation_penalties and nothing collects it.",
                options.PenaltySettlementEnabled,
                string.IsNullOrWhiteSpace(options.DispatchBaseUrl) ? "unset" : "set");
        }
        else if (string.IsNullOrWhiteSpace(options.WalletBaseUrl))
        {
            logger.LogError(
                "Fare:DispatchBaseUrl is set but Fare:WalletBaseUrl is not: a cancellation penalty will be "
                + "collected into the fare and then cannot be forwarded to the driver it is owed to. That is "
                + "the one combination that takes money without paying it out — configure both or neither.");
        }

        if (string.IsNullOrWhiteSpace(options.RideBaseUrl) || string.IsNullOrWhiteSpace(options.RideInternalApiKey))
        {
            logger.LogError(
                "Fare:RideBaseUrl / Fare:RideInternalApiKey is not configured, so a terminal payment cannot be "
                + "reported to ride-svc. Fares settle here and every ride stays in PaymentPending; the driver's "
                + "earning is recorded but the ride never closes (R-05).");
        }

        // Δ AL-57 — the two gateway-secret warnings are gone with the callbacks they guarded. What
        // matters now is the wallet seam: without it the `wallet` rail refuses every fare, which is
        // the correct failure (nothing moved) but leaves card-holding passengers on cash.
        if (string.IsNullOrWhiteSpace(options.WalletBaseUrl) || string.IsNullOrWhiteSpace(options.WalletInternalApiKey))
        {
            logger.LogWarning(
                "Fare:WalletBaseUrl / Fare:WalletInternalApiKey is not configured, so the `wallet` ride rail "
                + "answers 503 and every card-paying passenger is offered cash or the driver's QR instead. "
                + "Nothing is charged and nothing is lost — but AL-57's whole point is that card acceptance "
                + "survives, and without this it does not.");
        }

        logger.LogWarning(
            "Fare estimates are priced on a straight line × Fare:RouteDetourFactor ({Factor}), not a routed "
            + "distance: ADD §7.6 puts OSRM/Valhalla in Phase 3 and there is no road network to measure "
            + "against yet. Every quote is approximate by whatever the real detour is.",
            options.RouteDetourFactor);
    }
}

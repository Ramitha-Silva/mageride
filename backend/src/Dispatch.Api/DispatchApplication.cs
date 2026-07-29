using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Dispatch;

/// <summary>
/// Composition root for dispatch-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class DispatchApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "dispatch-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // dispatch-svc owns dispatch.outbox (migration 0709). D6' §2.4 names it alongside
            // ride-svc as an outbox writer and §2.1 registers `dispatch.events` with dispatch-svc
            // as its producer; `offer.created` is the event R-13 exists for.
            UseOutbox = true,
        };

        // Ahead of AddMageRideDefaults so an operator's own Outbox section still wins, but the
        // dispatch defaults apply when nobody sets one. The kernel's defaults describe rides.outbox
        // / ride_outbox / ride.events, which belong to another bounded context — pointing this
        // service at them would have dispatch publishing offers onto ride-svc's topic and waking
        // ride-svc's dispatcher.
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "dispatch";
            outbox.Channel = "dispatch_outbox";
            outbox.Topic = "dispatch.events";
        });

        // Same reasoning for the R-14 replay log: the kernel's defaults name rides.command_log,
        // and two bounded contexts sharing one command log means a booking and a go-online can
        // collide on an identical client-generated Idempotency-Key (the convention C020/C021
        // established, recorded in db/CLAUDE.md). `dispatch.command_log` is migration 0710.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "dispatch";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddDispatchServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapStandbyEndpoints();
        app.MapDirectionalEndpoints();
        app.MapScheduledRideEndpoints();
        app.MapDriverLevelEndpoints();

        // Not mapped at all without a key, exactly as ride-svc and reputation-svc treat their own
        // internal families: an unauthenticated route into the level engine and the penalty ledger
        // is worse than a missing feature, and the start-up warning below says which it is.
        var dispatchOptions = app.Services.GetRequiredService<IOptions<DispatchOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(dispatchOptions.InternalApiKey))
        {
            app.MapInternalDispatchEndpoints(dispatchOptions.InternalApiKey);
        }

        WarnAboutGatesThatCannotClose(app);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which of this service's guarantees are switched off.
    /// </summary>
    /// <remarks>
    /// Every one of these misconfigurations looks like success from the outside — the service
    /// starts, answers health checks and serves presence — and each removes something a spec
    /// requires. A gate that is off is not a gate that passes, and the log line is the only place
    /// the difference is visible.
    /// </remarks>
    private static void WarnAboutGatesThatCannotClose(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DispatchOptions>>().Value;

        if (string.IsNullOrWhiteSpace(options.RideServiceInternalKey))
        {
            app.Logger.LogWarning(
                "Dispatch:RideServiceInternalKey is not configured. ride-svc answers 404 to /v1/internal/rides/** " +
                "without it, so presence works but no ride will ever be offered to anyone.");
        }

        if (!options.ReputationGateEnabled)
        {
            app.Logger.LogWarning(
                "Dispatch:ReputationGateEnabled is off. D5' §3.2's block-state gate does not run: a " +
                "BOOKING_DISABLED or DELISTED driver will be offered rides, and every candidate is scored " +
                "at Driver Level 3.");
        }
        else if (string.IsNullOrWhiteSpace(options.ReputationInternalKey))
        {
            app.Logger.LogInformation(
                "Dispatch:ReputationInternalKey is not configured. reputation-svc accepts any in-cluster " +
                "caller when its own key is unset (C033); if it is set, every gRPC call is answered " +
                "Unauthenticated and the gate fails open.");
        }

        if (!options.WalletGateEnabled)
        {
            app.Logger.LogWarning(
                "Dispatch:WalletGateEnabled is off. The D-08 daily-fee gate does not run: a driver below the " +
                "daily platform fee is offered their second and later trips of the day.");
        }

        if (!options.LastWillEnabled)
        {
            app.Logger.LogInformation(
                "Dispatch:LastWillEnabled is off, so R-15's EMQX last will is not consumed. A driver whose " +
                "session drops mid-offer holds it until the 15 s window expires instead of the {Grace} grace.",
                options.OfferReleaseGrace);
        }

        if (!options.PositionConsumerEnabled)
        {
            app.Logger.LogWarning(
                "Dispatch:PositionConsumerEnabled is off, so nothing refreshes dispatch.driver_presence from " +
                "telemetry.normalized (R-08). Every driver falls out of the candidate pool {Freshness} after " +
                "going online and no position ever moves them.",
                options.PositionFreshness);
        }

        if (string.IsNullOrWhiteSpace(options.InternalApiKey))
        {
            app.Logger.LogWarning(
                "Dispatch:InternalApiKey is not configured, so /v1/internal/** is not mapped. No driver no-show " +
                "can be reported (US-6A.7) and fare-svc cannot read or settle the D-05 cancellation penalties " +
                "this service accrues — a passenger's Rs 50 debt is recorded and never collected.");
        }

        if (!options.ScheduledWorkerEnabled)
        {
            app.Logger.LogWarning(
                "Dispatch:ScheduledWorkerEnabled is off. Nothing materialises a booking at T-{Lead}, so every " +
                "scheduled ride stays on the Job Board past its pickup time and no passenger who booked ahead " +
                "is ever dispatched (D5' §3.7).",
                options.ScheduledLeadTime);
        }

        if (!options.DirectionalGateEnabled)
        {
            app.Logger.LogWarning(
                "Dispatch:DirectionalGateEnabled is off. Drivers can still set a Destination Filter and it still " +
                "costs them a daily use, but the DT-02 predicate never runs — every ride is offered to them " +
                "whichever way it heads, and nothing on the driver's screen says the filter is inert.");
        }

        if (!options.DispatchTimerWorkerEnabled)
        {
            app.Logger.LogWarning(
                "Dispatch:DispatchTimerWorkerEnabled is off, so no dispatch.timers row ever fires. Besides the " +
                "US-6A.11 cascade deadline and the R-15 grace, a Directional Travel filter never expires on its " +
                "own (DT-04) and no {Lead} pre-expiry reminder is sent (DT-08).",
                options.DirectionalReminderLead);
        }

        if (!options.LevelWorkerEnabled)
        {
            app.Logger.LogInformation(
                "Dispatch:LevelWorkerEnabled is off. Levels still move when GET /v1/drivers/{{id}}/level or the " +
                "Job Board gate refreshes a driver, but a level earned by ratings alone will not reach the " +
                "scoring hot path — which reads it through reputation-svc, not through those routes.");
        }
    }
}

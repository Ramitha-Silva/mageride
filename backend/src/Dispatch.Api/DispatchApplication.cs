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

        WarnAboutMissingInternalKey(app);

        return app;
    }

    /// <summary>
    /// Without ride-svc's shared secret every offer is answered 404 and no driver is ever asked.
    /// From the outside that looks like "nobody is online", so it is said once, loudly, at start-up.
    /// </summary>
    private static void WarnAboutMissingInternalKey(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DispatchOptions>>().Value;

        if (string.IsNullOrWhiteSpace(options.RideServiceInternalKey))
        {
            app.Logger.LogWarning(
                "Dispatch:RideServiceInternalKey is not configured. ride-svc answers 404 to /v1/internal/rides/** " +
                "without it, so presence works but no ride will ever be offered to anyone.");
        }
    }
}

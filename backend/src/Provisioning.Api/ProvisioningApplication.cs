using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Shared.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning;

/// <summary>
/// Composition root for provisioning-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class ProvisioningApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Redis client id.</summary>
    public const string ServiceName = "provisioning-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // T-03's `imei:{imei}` cache and T-12's pub/sub invalidation. Every use is best
            // effort — prov.tracker_bindings is the source of truth — so a Redis outage costs the
            // adapter a Postgres lookup rather than refusing to provision anything.
            UseRedis = true,

            // tracker.bound / tracker.unbound (D6' §4.3) and tracker.revoked (T-12) are written
            // into prov.outbox inside the transaction that changes the binding, and the kernel's
            // LISTEN/NOTIFY dispatcher drains them to `provisioning.events` after COMMIT
            // (E-09, R-13). A revoke that committed and then failed to publish would leave a
            // decommissioned tracker publishing until its certificate expired.
            UseKafka = true,
            UseOutbox = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins, but provisioning's own
        // outbox is the default. The kernel's defaults describe rides.outbox on `ride_outbox` →
        // `ride.events`.
        builder.Services.Configure<OutboxOptions>(outbox =>
        {
            outbox.Schema = "prov";
            outbox.Channel = "prov_outbox";
            outbox.Topic = EventTopics.ProvisioningEvents;
        });

        // Likewise for the R-14 replay log (migration 0402). AggregateIdColumn is null because a
        // bind targets a binding that does not exist yet, so there is no id column to write.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "prov";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddProvisioningServices(builder.Configuration);

        var provisioning = builder.Configuration.GetSection(ProvisioningOptions.SectionName);

        // Bound after AddProvisioningServices so the section is available; hosted separately from
        // the registrations there so a test can resolve a worker without it also ticking.
        if (provisioning.GetValue("RotationEnabled", true))
        {
            builder.Services.AddHostedService(
                services => services.GetRequiredService<Trackers.CredentialRotationWorker>());
        }

        if (provisioning.GetValue("BulkMintEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Bulk.BulkMintWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapTrackerEndpoints();
        app.MapFleetTrackerEndpoints();

        var internalApiKey = app.Services.GetRequiredService<IOptions<ProvisioningOptions>>().Value.InternalApiKey;

        if (!string.IsNullOrWhiteSpace(internalApiKey))
        {
            app.MapInternalTrackerEndpoints(internalApiKey);
        }
        else
        {
            // The tcp-adapter validates every device connect through this prefix (T-01). Without
            // it the adapter's calls 404 and it refuses every device — safe, and completely
            // silent from the adapter's side, so it is said loudly here instead.
            app.Logger.LogWarning(
                "Provisioning:InternalApiKey is not configured, so /v1/internal/trackers/** is unmapped. " +
                "The tcp-adapter cannot resolve an IMEI (T-01/T-03), the 90-day rotation cron has no " +
                "endpoint to call (US-3.5), and no CRL is published for the broker (T-12).");
        }

        // Forces the CA to load or generate at start-up rather than on the first bind: a broken
        // volume, a partial restore or a StepCa:Url somebody set by mistake should stop the
        // process, not fail the first driver who tries to provision a tracker.
        _ = app.Services.GetRequiredService<Credentials.ICertificateAuthority>();

        return app;
    }
}

using MageRide.Registry.Configuration;
using MageRide.Registry.Endpoints;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Registry;

/// <summary>
/// Composition root for registry-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class RegistryApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Redis client id.</summary>
    public const string ServiceName = "registry-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // C028: `lock:driver:{driverId}` coordinates the go-live selection with the two
            // downstream planes that also key off "one vehicle at a time" (D-03). Postgres is
            // still the authority — see VehicleService.SelectLiveAsync.
            UseRedis = true,

            // C028 lands the table and the publish together, as the C021 handoff said it would.
            // `share.revoked` (D-22) is written into registry.outbox inside the transaction that
            // revokes the grant, and the kernel's LISTEN/NOTIFY dispatcher drains it to
            // `registry.events` after COMMIT (E-09, R-13).
            UseKafka = true,
            UseOutbox = true,
        };

        // Ahead of AddMageRideDefaults for the same reason the CommandLog section is: an
        // operator's setting still wins, but registry's own outbox is the default. The kernel's
        // defaults describe rides.outbox on `ride_outbox` → `ride.events`.
        builder.Services.Configure<MageRide.Shared.Messaging.OutboxOptions>(outbox =>
        {
            outbox.Schema = "registry";
            outbox.Channel = "registry_outbox";
            outbox.Topic = MageRide.Shared.Messaging.EventTopics.RegistryEvents;
        });

        // Ahead of AddMageRideDefaults so an operator's CommandLog section still wins, but the
        // registry defaults apply when nobody sets one. The kernel's defaults describe
        // rides.command_log, which belongs to another bounded context; a registration targets no
        // aggregate that exists yet, so there is no id column to write.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "registry";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddRegistryServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapVehicleEndpoints();
        app.MapSharingEndpoints();

        var internalApiKey = app.Services.GetRequiredService<IOptions<RegistryOptions>>().Value.InternalApiKey;
        if (!string.IsNullOrWhiteSpace(internalApiKey))
        {
            app.MapInternalVehicleEndpoints(internalApiKey);
        }
        else
        {
            // fare-svc cannot bind a driver's OnePay merchant without this, and the symptom is a
            // 402 merchant-not-onboarded on somebody's fare rather than anything that points here.
            app.Logger.LogWarning(
                "Registry:InternalApiKey is not configured, so /v1/internal/vehicles/** is unmapped. " +
                "No OnePay merchant binding can be recorded against this instance (D-11).");
        }

        if (DevApprovalEnabled(app))
        {
            app.MapDevSeedEndpoints();
        }

        return app;
    }

    /// <summary>
    /// Whether the dev seed approval is mapped. Unset means Development only; the replica sets it
    /// explicitly because it runs synthetic data under the Production environment name.
    /// </summary>
    private static bool DevApprovalEnabled(WebApplication app)
    {
        var configured = app.Services.GetRequiredService<IOptions<RegistryOptions>>().Value.DevApprovalEnabled;
        var enabled = configured ?? app.Environment.IsDevelopment();

        if (enabled && !app.Environment.IsDevelopment())
        {
            // Loud, once, at start-up. A deployment that turned this on without meaning to is
            // one where any driver can approve their own vehicle without an insurance document.
            app.Logger.LogWarning(
                "Registry:DevApprovalEnabled is on outside Development: POST /v1/dev/vehicles/{{vehicleId}}/approve " +
                "is mapped and bypasses the AL-10 insurance requirement and the AL-30 onboarding steps.");
        }

        return enabled;
    }
}

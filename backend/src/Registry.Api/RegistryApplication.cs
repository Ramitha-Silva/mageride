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

        // Bound after AddRegistryServices so the section is available; hosted separately from the
        // registration above so a test can resolve the worker without it also ticking.
        if (builder.Configuration.GetSection(RegistryOptions.SectionName).GetValue("DocumentExpiryEnabled", true))
        {
            builder.Services.AddHostedService(
                services => services.GetRequiredService<Onboarding.DocumentExpiryWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapVehicleEndpoints();
        app.MapOnboardingEndpoints();
        app.MapSharingEndpoints();

        var internalApiKey = app.Services.GetRequiredService<IOptions<RegistryOptions>>().Value.InternalApiKey;
        if (!string.IsNullOrWhiteSpace(internalApiKey))
        {
            app.MapInternalVehicleEndpoints(internalApiKey);
            app.MapInternalDriverEndpoints(internalApiKey);
        }
        else
        {
            // Δ AL-57: what is lost is AL-30's recompute, not the retired D-11 bind. Without it a
            // Verification Officer's Confirm never reaches this service, so a Mode C vehicle sits at
            // pending_review for a field nobody is still questioning and can never be approved.
            // Δ AL-58: and the officer's payout-profile decision never arrives either, so no driver
            // is ever payable — their wallet accrues, the weekly sweep skips them, and the money is
            // owed rather than lost. Both halves fail silently and look like a queue nobody worked.
            app.Logger.LogWarning(
                "Registry:InternalApiKey is not configured, so /v1/internal/** is unmapped. " +
                "A confirmed onboarding field never reaches this service (AL-30), so no Mode C vehicle " +
                "can be approved; and no driver payout profile can ever be verified (AL-58), so " +
                "payout-svc's weekly sweep pays nobody.");
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

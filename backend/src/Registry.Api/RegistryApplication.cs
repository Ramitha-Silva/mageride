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

            // registry-svc has no hot path in this slice: no candidate index, no presence, no
            // cache. Postgres is the only store it touches, so a Redis dependency would be a
            // readiness probe that can fail without anything here being unable to serve.
            UseRedis = false,

            // D3' has POST /v1/vehicles emit `vehicle.registered`, but no `registry.outbox` table
            // exists in either DDL source and nothing in the walking skeleton consumes the event —
            // dispatch reads the vehicle row directly. Publishing outside a transaction to satisfy
            // the letter of it would break the very guarantee the outbox exists for (R-13), so the
            // event is not emitted here at all. C028 lands the table and the publish together;
            // recorded in the C021 handoff.
            UseKafka = false,
            UseOutbox = false,
        };

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

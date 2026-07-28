using MageRide.Ride.Configuration;
using MageRide.Ride.Endpoints;
using MageRide.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride;

/// <summary>
/// Composition root for ride-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class RideApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "ride-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // ride-svc owns rides.outbox, and the kernel's Outbox defaults already describe it:
            // schema `rides`, channel `ride_outbox`, topic `ride.events` (D7' §4.2, D6' §2.1).
            UseOutbox = true,

            // The Redis fast path in ADD §11.11 — `lock:driver-offer:{driverId}` — is
            // dispatch-svc's reservation (D5' §3.6, C023). The authoritative accept this service
            // performs is pure Postgres, so a Redis dependency here would be a readiness probe
            // that can fail while every route still works. C032 turns it on with the offer cache.
            UseRedis = false,
        };

        // The kernel's CommandLog defaults already point at rides.command_log with ride_id as the
        // aggregate column (C002/C004), so unlike iam-svc and registry-svc nothing is overridden.
        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddRideServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapRideEndpoints();

        var internalApiKey = app.Services.GetRequiredService<IOptions<RideOptions>>().Value.InternalApiKey;
        if (!string.IsNullOrWhiteSpace(internalApiKey))
        {
            app.MapInternalRideEndpoints(internalApiKey);
        }
        else
        {
            // dispatch-svc cannot move a ride to Matching or Offered without these, so a stack
            // that is missing the key looks like "no driver ever gets an offer" from the outside.
            app.Logger.LogWarning(
                "Ride:InternalApiKey is not configured, so /v1/internal/rides/** is unmapped. " +
                "dispatch-svc cannot place offers against this instance.");
        }

        return app;
    }
}

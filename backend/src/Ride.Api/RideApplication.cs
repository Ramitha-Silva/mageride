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
            // performs is pure Postgres, and so is every R-04 timer, so a Redis dependency here
            // would be a readiness probe that can fail while every route still works. It also
            // makes R-04's "the backstop fires independently of any Redis TTL" structural rather
            // than merely tested: there is no cache in this process to flush.
            UseRedis = false,
        };

        // The kernel's CommandLog defaults already point at rides.command_log with ride_id as the
        // aggregate column (C002/C004), so unlike iam-svc and registry-svc nothing is overridden.
        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddRideServices(builder.Configuration);

        var ride = builder.Configuration.GetSection(RideOptions.SectionName);

        // Hosted separately from the registrations in AddRideServices so a test can resolve a
        // worker and drive one pass without it also ticking.
        if (ride.GetValue("TimersEnabled", true))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Timers.RideTimerWorker>());
        }

        if (ride.GetValue("VehicleStatusEnabled", false))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<Mqtt.VehiclePresenceWorker>());
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapRideEndpoints();
        app.MapLocationRequestEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<RideOptions>>().Value;

        // Both hold a key that is required outside Development, so a deployment that forgot one
        // fails here rather than on a booking. Same discipline as iam-svc's PhoneHasher (C027).
        _ = app.Services.GetRequiredService<Rides.RiderPhoneHasher>();
        _ = app.Services.GetRequiredService<Rides.PackageOtpCodec>();

        if (string.IsNullOrWhiteSpace(settings.IamBaseUrl))
        {
            // The passenger surface is unaffected; proxy booking and the whole P-02 round-trip are
            // not, and both answer 503 rather than guessing at a rider's registration.
            app.Logger.LogWarning(
                "Ride:IamBaseUrl is not configured. POST /v1/rides/request with kind 'proxy' and the whole " +
                "/v1/location-requests family will answer 503 dependency-unavailable (P-03).");
        }

        if (settings.StuckStateMetricsEnabled)
        {
            // Resolved eagerly: the gauges are registered in the constructor, and a lazily-resolved
            // observer would publish nothing until something else happened to ask for it.
            _ = app.Services.GetRequiredService<Observability.StuckStateObserver>();
        }

        if (!settings.TimersEnabled)
        {
            // §11.12 makes the no-show and grace outcomes the platform's job, not a client's. With
            // the sweep off, a rider who never appears keeps a driver waiting forever and a driver
            // whose phone died keeps a passenger's ride open forever — neither of which any client
            // can fix, so it is said loudly here.
            app.Logger.LogWarning(
                "Ride:TimersEnabled is off: no ride will reach NoShowRider, NoShowDriver or the R-15/R-16 " +
                "offline-grace terminals in this process (R-04, §11.12).");
        }

        var internalApiKey = settings.InternalApiKey;
        if (!string.IsNullOrWhiteSpace(internalApiKey))
        {
            app.MapInternalRideEndpoints(internalApiKey);
        }
        else
        {
            // dispatch-svc cannot move a ride to Matching or Offered without these, so a stack
            // that is missing the key looks like "no driver ever gets an offer" from the outside;
            // and fare-svc has no way to settle a ride, so every completed ride stalls in
            // PaymentPending and no driver earning ever posts (R-05).
            app.Logger.LogWarning(
                "Ride:InternalApiKey is not configured, so /v1/internal/rides/** is unmapped. " +
                "dispatch-svc cannot place offers against this instance and fare-svc cannot settle a ride.");
        }

        return app;
    }
}

using MageRide.Ride.Configuration;
using MageRide.Ride.Mqtt;
using MageRide.Ride.Observability;
using MageRide.Ride.Persistence;
using MageRide.Ride.Rides;
using MageRide.Ride.Timers;
using MageRide.Shared.Fares;
using MageRide.Shared.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Ride;

/// <summary>Everything ride-svc owns on top of the shared kernel.</summary>
public static class RideServiceCollectionExtensions
{
    public static IServiceCollection AddRideServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RideOptions>()
            .Bind(configuration.GetSection(RideOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // The verifying half of the fareEstimateToken contract; fare-svc mints with the same key.
        services.AddMageRideFareTokens(configuration);

        services.AddSingleton<IRideRepository, RideRepository>();
        services.AddSingleton<IRideTransitionRepository, RideTransitionRepository>();
        services.AddSingleton<IRideTimerRepository, RideTimerRepository>();
        services.AddSingleton<IDriverSummaryRepository, DriverSummaryRepository>();

        // AL-16 for as long as reputation-svc (C033) does not exist. Registered against the
        // interface so the swap is one line here and nothing else — see the type's remarks.
        services.AddSingleton<IBookingEligibility, RideHistoryBookingEligibility>();

        // The audit row + timer plan + outbox rows that accompany every state change. Singleton:
        // it holds no per-request state and takes the unit of work as an argument.
        services.AddSingleton<RideStateWriter>();

        var options = configuration.GetSection(RideOptions.SectionName);

        // Registered as a concrete type too, so a test can drive one sweep deterministically
        // instead of waiting on the ticker — the shape C031's SessionSweepWorker uses.
        services.AddSingleton<RideTimerWorker>();

        if (options.GetValue("StuckStateMetricsEnabled", true))
        {
            services.AddSingleton<StuckStateObserver>();
        }

        // What a last will *means* is database work, so it is always available — dispatch-svc's
        // system-cancel path reaches the same outcome, and the R-16 windows are testable without a
        // broker. Only the subscription that carries the last will is optional.
        services.AddScoped<IVehiclePresence, VehiclePresence>();

        // The broker client is registered only when something is going to use it: a service that
        // never touches the device plane should not hold the MQTT session-token secret at all.
        if (options.GetValue("VehicleStatusEnabled", false))
        {
            services.AddMageRideMqtt(configuration);
            services.AddSingleton<VehiclePresenceWorker>();
        }

        // Scoped: each opens a unit of work per command, so its lifetime is the request's.
        services.AddScoped<IRideService, RideService>();
        services.AddScoped<IRideCancellationService, RideCancellationService>();
        services.AddScoped<IRideSettlementService, RideSettlementService>();

        return services;
    }
}

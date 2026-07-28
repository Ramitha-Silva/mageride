using MageRide.Shared.Mqtt;
using MageRide.TripState.Configuration;
using MageRide.TripState.Mqtt;
using MageRide.TripState.Persistence;
using MageRide.TripState.Sessions;
using MageRide.TripState.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.TripState;

/// <summary>Everything trip-state-svc owns on top of the shared kernel.</summary>
public static class TripStateServiceCollectionExtensions
{
    public static IServiceCollection AddTripStateServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TripStateOptions>()
            .Bind(configuration.GetSection(TripStateOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<ITripEventRepository, TripEventRepository>();
        services.AddSingleton<IRatingRepository, RatingRepository>();
        services.AddSingleton<IVehicleLookupRepository, VehicleLookupRepository>();
        services.AddSingleton<IVehiclePresenceStore, VehiclePresenceStore>();
        services.AddSingleton<IDriverSessionCache, DriverSessionCache>();

        var options = configuration.GetSection(TripStateOptions.SectionName);

        // The broker client is registered only when something is going to use it. A service that
        // never touches the device plane should not hold the session-token secret at all — the
        // reason AddMageRideMqtt is not part of AddMageRideDefaults.
        if (options.GetValue("PublishCadenceHints", false) || options.GetValue("VehicleStatusEnabled", false))
        {
            services.AddMageRideMqtt(configuration);
        }

        if (options.GetValue("PublishCadenceHints", false))
        {
            services.AddSingleton<ICadencePublisher, MqttCadencePublisher>();
        }
        else
        {
            services.AddSingleton<ICadencePublisher, DisabledCadencePublisher>();
        }

        // Registered as concrete types too, so a test can drive one pass deterministically instead
        // of waiting on a ticker — the shape C029's DocumentExpiryWorker and C030's rotation sweep
        // both use.
        services.AddSingleton<SessionSweepWorker>();
        services.AddSingleton<SessionPositionConsumer>();

        if (options.GetValue("VehicleStatusEnabled", false))
        {
            services.AddSingleton<VehicleStatusWorker>();
        }

        // Scoped: each opens a unit of work per command, so its lifetime is the request's.
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IRatingService, RatingService>();

        return services;
    }
}

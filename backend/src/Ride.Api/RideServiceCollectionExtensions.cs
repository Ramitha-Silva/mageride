using MageRide.Ride.Configuration;
using MageRide.Ride.Persistence;
using MageRide.Ride.Rides;
using MageRide.Shared.Fares;
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
        services.AddSingleton<IDriverSummaryRepository, DriverSummaryRepository>();

        // Scoped: it opens a unit of work per command, so its lifetime is the request's.
        services.AddScoped<IRideService, RideService>();

        return services;
    }
}

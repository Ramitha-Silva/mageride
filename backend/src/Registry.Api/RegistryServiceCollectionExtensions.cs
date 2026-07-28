using MageRide.Registry.Configuration;
using MageRide.Registry.Persistence;
using MageRide.Registry.Vehicles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Registry;

/// <summary>Everything registry-svc owns on top of the shared kernel.</summary>
public static class RegistryServiceCollectionExtensions
{
    public static IServiceCollection AddRegistryServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RegistryOptions>()
            .Bind(configuration.GetSection(RegistryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IVehicleRepository, VehicleRepository>();
        services.AddSingleton<IDriverProfileRepository, DriverProfileRepository>();

        // Scoped: it opens a unit of work per command, so its lifetime is the request's.
        services.AddScoped<IVehicleService, VehicleService>();

        return services;
    }
}

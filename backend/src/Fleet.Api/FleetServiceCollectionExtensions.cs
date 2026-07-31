using MageRide.Fleet.Authorization;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Documents;
using MageRide.Fleet.Organisation;
using MageRide.Fleet.Payouts;
using MageRide.Fleet.Persistence;
using MageRide.Fleet.Vehicles;

namespace MageRide.Fleet;

/// <summary>fleet-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class FleetServiceCollectionExtensions
{
    public static IServiceCollection AddFleetServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FleetOptions>()
            .Bind(configuration.GetSection(FleetOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: each holds a connection factory and a handful of SQL strings.
        services.AddSingleton<IFleetRepository, FleetRepository>();
        services.AddSingleton<IFleetMemberRepository, FleetMemberRepository>();
        services.AddSingleton<IPortalUserRepository, PortalUserRepository>();
        services.AddSingleton<IPayoutProfileRepository, PayoutProfileRepository>();
        services.AddSingleton<IPayoutDocumentRepository, PayoutDocumentRepository>();
        services.AddSingleton<IFleetVehicleRepository, FleetVehicleRepository>();

        // The scoped reader holds the connection factory and two option flags and is stateless per
        // call — each read opens its own transaction and sets the role and GUC inside it.
        services.AddSingleton<FleetScopedReader>();
        services.AddSingleton<IFleetScopedReader>(provider => provider.GetRequiredService<FleetScopedReader>());

        // Holds a root path, resolved once at construction.
        services.AddSingleton<IDocumentStore, FileSystemDocumentStore>();

        // Scoped: each takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<IFleetService, FleetService>();
        services.AddScoped<IFleetVerificationService, FleetVerificationService>();
        services.AddScoped<IPayoutProfileService, PayoutProfileService>();
        services.AddScoped<IClassificationService, ClassificationService>();

        // Scoped, because it resolves IUnitOfWorkFactory for the two reads that decide the
        // request. `AddEndpointFilter<T>` resolves the filter from the request's own scope.
        services.AddScoped<FleetAccessFilter>();

        return services;
    }
}

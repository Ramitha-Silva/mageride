using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Ingest;
using MageRide.FleetHealth.Mqtt;
using MageRide.FleetHealth.Persistence;
using MageRide.FleetHealth.Rollups;
using MageRide.Shared.Mqtt;

namespace MageRide.FleetHealth;

/// <summary>fleet-health-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class FleetHealthServiceCollectionExtensions
{
    public static IServiceCollection AddFleetHealthServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FleetHealthOptions>()
            .Bind(configuration.GetSection(FleetHealthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: both repositories hold a connection factory and a handful of SQL strings and are
        // called from background loops as well as from a request, so a scoped lifetime would mean
        // resolving a scope per flush on the ingest path.
        services.AddSingleton<IDeviceHealthRepository, DeviceHealthRepository>();
        services.AddSingleton<IFleetRollupRepository, FleetRollupRepository>();

        services.AddSingleton<IAggregateMaintainer, AggregateMaintainer>();

        // Scoped, because it opens a unit of work: the alert path writes the alert row and the outbox row
        // in one transaction (R-13) and the outbox writer is registered scoped by the kernel.
        services.AddScoped<IFleetHealthAlertService, FleetHealthAlertService>();
        services.AddScoped<IFleetHealthService, FleetHealthService>();

        // Registered rather than hosted here, so a test can resolve a worker and drive one pass without
        // it also ticking underneath the assertion. FleetHealthApplication decides which are hosted.
        services.AddSingleton<TelemetryHealthConsumer>();
        services.AddSingleton<ProvisioningEventConsumer>();
        services.AddSingleton<HealthSweepWorker>();
        services.AddSingleton<FleetHealthAlertWorker>();

        var health = configuration.GetSection(FleetHealthOptions.SectionName);

        // The MQTT session-token issuer is registered only when the device plane is on, so a deployment
        // that never touches the broker does not hold the session-token secret. Same call trip-state-svc
        // makes.
        if (health.GetValue("DevicePlaneEnabled", false))
        {
            services.AddMageRideMqtt(configuration);
            services.AddSingleton<DevicePlaneWorker>();
        }

        return services;
    }
}

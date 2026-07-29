using MageRide.Reputation.Configuration;
using MageRide.Reputation.Counters;
using MageRide.Reputation.Detection;
using MageRide.Reputation.Grpc;
using MageRide.Reputation.Messaging;
using MageRide.Reputation.Persistence;
using MageRide.Reputation.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace MageRide.Reputation;

/// <summary>reputation-svc's own registrations. The kernel supplies everything cross-cutting.</summary>
public static class ReputationServiceCollectionExtensions
{
    public static IServiceCollection AddReputationServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ReputationOptions>()
            .Bind(configuration.GetSection(ReputationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Repositories are singletons: they hold SQL and nothing else, and take the connection or
        // the unit of work per call (AL-53, and the shape every other service uses).
        services.TryAddSingleton<ICounterRepository, CounterRepository>();
        services.TryAddSingleton<IBlockStateRepository, BlockStateRepository>();
        services.TryAddSingleton<IIntakeLogRepository, IntakeLogRepository>();
        services.TryAddSingleton<IDriverLevelRepository, DriverLevelRepository>();
        services.TryAddSingleton<IFraudFlagRepository, FraudFlagRepository>();
        services.TryAddSingleton<IDetectionRepository, DetectionRepository>();
        services.TryAddSingleton<IAuditRepository, AuditRepository>();

        // The cache is registered against whichever Redis the kernel gave us — and against a null
        // implementation when it gave us none, so a service configured without Redis still answers
        // every call, from Postgres, correctly and more slowly.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)))
        {
            services.TryAddSingleton<IBlockStatusCache, BlockStatusCache>();
        }
        else
        {
            services.TryAddSingleton<IBlockStatusCache, NullBlockStatusCache>();
        }

        // Scoped: both take an IUnitOfWorkFactory and one request or one message is one unit.
        services.TryAddScoped<IReputationService, ReputationService>();
        services.TryAddScoped<IRideEventHandler, RideEventHandler>();
        services.TryAddScoped<ICollusionDetector, CollusionDetector>();

        // Registered but not hosted here — ReputationApplication decides which ones tick, so a test
        // can resolve a worker and drive one pass without it also running underneath the assertion.
        services.TryAddSingleton<RideEventConsumer>();
        services.TryAddSingleton<BlockStateExpiryWorker>();
        services.TryAddSingleton<CollusionDetectorWorker>();

        services.AddGrpc(grpc =>
        {
            // A gate that returns nothing useful on failure is worse than one that says why: the
            // caller is another service, not a browser, and D6' §8.3's resilience policy needs the
            // status code to decide whether to retry.
            grpc.EnableDetailedErrors = true;

            var apiKey = configuration.GetSection(ReputationOptions.SectionName)["InternalApiKey"];

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                grpc.Interceptors.Add<InternalKeyInterceptor>(apiKey);
            }
        });

        return services;
    }
}

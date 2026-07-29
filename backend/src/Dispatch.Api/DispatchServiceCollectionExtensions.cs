using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Eligibility;
using MageRide.Dispatch.Levels;
using MageRide.Dispatch.Messaging;
using MageRide.Dispatch.Mqtt;
using MageRide.Dispatch.Penalties;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Presence;
using MageRide.Dispatch.Redis;
using MageRide.Dispatch.Scheduling;
using MageRide.Dispatch.Timers;
using MageRide.Shared.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// See ReputationGate.cs: inside MageRide.Dispatch.* the bare `Reputation` binds to the
// MageRide.Reputation namespace before the generated gRPC class of the same name.
using ReputationClient = MageRide.Reputation.Grpc.Reputation.ReputationClient;

namespace MageRide.Dispatch;

/// <summary>Everything dispatch-svc owns on top of the shared kernel.</summary>
public static class DispatchServiceCollectionExtensions
{
    public static IServiceCollection AddDispatchServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DispatchOptions>()
            .Bind(configuration.GetSection(DispatchOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IPresenceRepository, PresenceRepository>();
        services.AddSingleton<ICandidateRepository, CandidateRepository>();
        services.AddSingleton<IOfferRepository, OfferRepository>();
        services.AddSingleton<IOfferTimerRepository, OfferTimerRepository>();
        services.AddSingleton<IDispatchTimerRepository, DispatchTimerRepository>();
        services.AddSingleton<IDailyFeeRepository, DailyFeeRepository>();
        services.AddSingleton<IScheduledRideRepository, ScheduledRideRepository>();
        services.AddSingleton<IDriverLevelRepository, DriverLevelRepository>();
        services.AddSingleton<IPenaltyRepository, PenaltyRepository>();
        services.AddSingleton<IDriverIndex, DriverIndex>();

        // Pure: everything it scores has already been fetched by the caller.
        services.AddSingleton<ICandidateScorer, CandidateScorer>();

        // Scoped: each opens a unit of work per command, so their lifetime is the request's — or,
        // for the workers, the message's.
        services.AddScoped<IPresenceService, PresenceService>();
        services.AddScoped<IVehicleStatusService, VehicleStatusService>();
        services.AddScoped<IDispatchService, DispatchService>();
        services.AddScoped<IRideEventHandler, RideEventHandler>();
        services.AddScoped<IWalletGate, WalletGate>();
        services.AddScoped<IScheduledRideService, ScheduledRideService>();
        services.AddScoped<IDriverLevelService, DriverLevelService>();
        services.AddScoped<IPenaltyService, PenaltyService>();

        services.AddHttpClient<IRideServiceClient, RideServiceClient>(RideServiceClient.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<DispatchOptions>>().Value;
            client.BaseAddress = new Uri(options.RideServiceBaseUrl);
            client.Timeout = options.RideServiceTimeout;
        });

        var dispatch = configuration.GetSection(DispatchOptions.SectionName).Get<DispatchOptions>() ?? new DispatchOptions();

        AddReputationGate(services, dispatch);

        if (dispatch.ExpiryWorkerEnabled)
        {
            services.AddHostedService<OfferExpiryWorker>();
        }

        if (dispatch.DispatchTimerWorkerEnabled)
        {
            services.AddHostedService<DispatchTimerWorker>();
        }

        if (dispatch.KeyspaceNotificationsEnabled)
        {
            services.AddHostedService<OfferKeyspaceListener>();
        }

        if (dispatch.ScheduledWorkerEnabled)
        {
            services.AddSingleton<ScheduledRideWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<ScheduledRideWorker>());
        }

        if (dispatch.LevelWorkerEnabled)
        {
            services.AddSingleton<DriverLevelWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<DriverLevelWorker>());
        }

        if (dispatch.ConsumerEnabled)
        {
            services.AddHostedService<RideEventConsumer>();
        }

        if (dispatch.PositionConsumerEnabled)
        {
            services.AddHostedService<PositionConsumer>();
        }

        if (dispatch.LastWillEnabled)
        {
            // The broker client is registered only when something is going to use it: a service
            // that never touches the device plane should not hold the MQTT session-token secret at
            // all (MageRide.Shared.Mqtt's own rule, and ride-svc does the same).
            services.AddMageRideMqtt(configuration);
            services.AddSingleton<VehicleStatusWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<VehicleStatusWorker>());
        }

        return services;
    }

    /// <summary>
    /// The D-04 gate's transport. Registered even when the gate is off, so the composition root
    /// stays one shape and turning the flag on needs no other change.
    /// </summary>
    /// <remarks>
    /// <c>AddGrpcClient</c> rather than a hand-built <c>GrpcChannel</c>: the channel then lives in
    /// <c>IHttpClientFactory</c>'s handler pool with every other outbound hop, so it is rotated,
    /// instrumented and configured the same way. <c>HTTP/2 without TLS</c> is the interim — D3' §0
    /// puts this family on mTLS and C042's mesh is what will provide it; until then the address is
    /// plain <c>http://</c> and the hop carries the shared secret
    /// <see cref="ReputationGate.InternalKeyHeader"/>.
    /// </remarks>
    private static void AddReputationGate(IServiceCollection services, DispatchOptions dispatch)
    {
        services.AddGrpcClient<ReputationClient>(client =>
            client.Address = new Uri(dispatch.ReputationGrpcAddress));

        // Singleton: the memo is per process and a per-request cache would never be hit — a
        // candidate build is one request, and the cascade's value comes from the *next* one.
        services.AddSingleton<IReputationGate, ReputationGate>();
    }
}

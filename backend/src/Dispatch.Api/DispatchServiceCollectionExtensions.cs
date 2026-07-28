using MageRide.Dispatch.Configuration;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Messaging;
using MageRide.Dispatch.Persistence;
using MageRide.Dispatch.Presence;
using MageRide.Dispatch.Redis;
using MageRide.Dispatch.Timers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
        services.AddSingleton<IDriverIndex, DriverIndex>();

        // Scoped: both open a unit of work per command, so their lifetime is the request's — or,
        // for the workers, the message's.
        services.AddScoped<IPresenceService, PresenceService>();
        services.AddScoped<IDispatchService, DispatchService>();
        services.AddScoped<IRideEventHandler, RideEventHandler>();

        services.AddHttpClient<IRideServiceClient, RideServiceClient>(RideServiceClient.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<DispatchOptions>>().Value;
            client.BaseAddress = new Uri(options.RideServiceBaseUrl);
            client.Timeout = options.RideServiceTimeout;
        });

        var dispatch = configuration.GetSection(DispatchOptions.SectionName).Get<DispatchOptions>() ?? new DispatchOptions();

        if (dispatch.ExpiryWorkerEnabled)
        {
            services.AddHostedService<OfferExpiryWorker>();
        }

        if (dispatch.KeyspaceNotificationsEnabled)
        {
            services.AddHostedService<OfferKeyspaceListener>();
        }

        if (dispatch.ConsumerEnabled)
        {
            services.AddHostedService<RideEventConsumer>();
        }

        return services;
    }
}

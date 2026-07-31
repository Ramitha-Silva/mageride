using MageRide.Voip.Configuration;
using MageRide.Voip.Messaging;
using MageRide.Voip.Persistence;
using MageRide.Voip.Signalling;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MageRide.Voip;

/// <summary>voip-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class VoipServiceCollectionExtensions
{
    public static IServiceCollection AddVoipServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<VoipOptions>()
            .Bind(configuration.GetSection(VoipOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singleton: it holds the signing key and a clock, and is read-only after construction.
        services.TryAddSingleton<ILiveKitTokenMinter, LiveKitTokenMinter>();

        // Scoped, both: they take the connection factory, so one request holds one connection.
        services.TryAddScoped<IVoipRepository, VoipRepository>();
        services.TryAddScoped<ICallService, CallService>();
        services.TryAddScoped<RideTerminalHandler>();

        services.AddLiveKitRoomService(configuration);

        return services;
    }

    /// <summary>
    /// The server-API client, when there is a server to call.
    /// </summary>
    /// <remarks>
    /// Registered by whether <c>Voip:LiveKit:ApiUrl</c> is set, the same shape registry-svc uses for
    /// <c>Registry:OcrBaseUrl</c>. Without it the no-op lands — and it says so on every teardown
    /// rather than reporting success, because a room nobody closed is a call that can outlive its
    /// ride, which is the one property D6' §6 names.
    /// </remarks>
    private static IServiceCollection AddLiveKitRoomService(
        this IServiceCollection services, IConfiguration configuration)
    {
        var apiUrl = configuration[$"{VoipOptions.SectionName}:LiveKit:ApiUrl"];

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            services.TryAddSingleton<ILiveKitRoomService, UnconfiguredLiveKitRoomService>();

            return services;
        }

        services.AddHttpClient(LiveKitRoomService.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<VoipOptions>>().Value;

                client.BaseAddress = new Uri(options.LiveKit.ApiUrl!, UriKind.Absolute);
                client.Timeout = options.LiveKit.ApiTimeout;
            });

        // No retry pipeline. The teardown runs on a Kafka consumer whose offset is not committed
        // when it fails, so the redelivery *is* the retry — and it is a better one, because it
        // survives a restart where an in-process backoff would not.
        services.TryAddSingleton<ILiveKitRoomService, LiveKitRoomService>();

        return services;
    }
}

using MageRide.PublicBff.Configuration;
using MageRide.PublicBff.Live;
using MageRide.PublicBff.Persistence;
using MageRide.PublicBff.Tracking;
using MageRide.PublicBff.Upstream;
using MageRide.Shared.Storage;

namespace MageRide.PublicBff;

/// <summary>Everything public-bff owns, registered once.</summary>
public static class PublicBffServiceCollectionExtensions
{
    public static IServiceCollection AddPublicBffServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PublicBffOptions>()
            .Bind(configuration.GetSection(PublicBffOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IShareTokenRepository, ShareTokenRepository>();
        services.AddScoped<ITrackReadRepository, TrackReadRepository>();

        services.AddSingleton<ILivePositionReader, LivePositionReader>();
        services.AddSingleton<IDeliveryCodeReader, DeliveryCodeReader>();

        services.AddScoped<ITrackTokenGate, TrackTokenGate>();
        services.AddScoped<ITrackService, TrackService>();
        services.AddScoped<ITrackStream, TrackStream>();
        services.AddScoped<IReceiptService, ReceiptService>();

        // Read-only, and only to presign: the receipt's delivery photograph lives in D-36's bucket
        // and the stored pointer must never reach a browser. This service writes no bytes anywhere.
        services.AddMageRideObjectStore(configuration);

        var options = configuration.GetSection(PublicBffOptions.SectionName).Get<PublicBffOptions>()
                      ?? new PublicBffOptions();

        services.AddScoped<IRideClient, RideClient>();
        services.AddScoped<ISafetyClient, SafetyClient>();

        AddUpstream(services, RideClient.HttpClientName, options.Ride, options.UpstreamTimeout);
        AddUpstream(services, SafetyClient.HttpClientName, options.Safety, options.UpstreamTimeout);

        return services;
    }

    /// <summary>
    /// A named client per upstream, with a base address only when one is configured.
    /// </summary>
    /// <remarks>
    /// <b>The client is registered either way and the route stays mapped either way.</b> An
    /// unconfigured upstream is a <c>503</c> on a route that still exists, still runs the token gate
    /// and still appears in the route table — because a route that vanished with a setting is a
    /// route no fence test enumerates.
    /// </remarks>
    private static void AddUpstream(
        IServiceCollection services, string name, UpstreamOptions upstream, TimeSpan timeout)
    {
        services.AddHttpClient(name, client =>
        {
            if (upstream.IsConfigured)
            {
                client.BaseAddress = new Uri(upstream.BaseUrl!.TrimEnd('/') + "/");
            }

            client.Timeout = timeout;
        });
    }
}

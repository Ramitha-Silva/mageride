using System.Net;
using MageRide.Transit.Configuration;
using MageRide.Transit.Feed;
using MageRide.Transit.Geo;
using MageRide.Transit.Gtfs;
using MageRide.Transit.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MageRide.Transit;

/// <summary>transit-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class TransitServiceCollectionExtensions
{
    public static IServiceCollection AddTransitServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TransitOptions>()
            .Bind(configuration.GetSection(TransitOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.MapsLink.AllowedHosts is { Length: > 0 },
                "Transit:MapsLink:AllowedHosts must not be empty. It is the only thing standing between "
                + "/v1/geo/parse-maps-link and an authenticated SSRF primitive: without it a pasted "
                + "http://169.254.169.254/ is fetched by the platform on the caller's behalf.")
            .ValidateOnStart();

        // Singletons: the cache holds the loaded feed for the process, and the router and the
        // repository are stateless over it. Nothing here is per request, which is the point — a
        // national feed is read once at activation and answered from memory afterwards.
        services.TryAddSingleton<IGtfsFeedRepository, GtfsFeedRepository>();
        services.TryAddSingleton<IGtfsFeedCache, GtfsFeedCache>();
        services.TryAddSingleton<ITransitRouting, TransitRouting>();
        services.TryAddSingleton<IMapsLinkResolver, MapsLinkResolver>();

        services.AddMapsLinkClient();
        services.AddGtfsLifecycle();

        return services;
    }

    /// <summary>
    /// The SCR-AP-016 half (Δ C057). Singletons for the same reason as the routing half: every one
    /// of these is stateless over the connection factory, and the two that are not — the object
    /// store's root and the validation latch — are process-wide by nature.
    /// </summary>
    private static IServiceCollection AddGtfsLifecycle(this IServiceCollection services)
    {
        services.TryAddSingleton<IGtfsFeedVersionRepository, GtfsFeedVersionRepository>();
        services.TryAddSingleton<IGtfsAuditRepository, GtfsAuditRepository>();
        services.TryAddSingleton<IGtfsObjectStore, FileSystemGtfsObjectStore>();
        services.TryAddSingleton<IGtfsValidator, GtfsValidator>();
        services.TryAddSingleton<IGtfsImporter, GtfsImporter>();
        services.TryAddSingleton<IGtfsUploadService, GtfsUploadService>();
        services.TryAddSingleton<IGtfsActivationService, GtfsActivationService>();
        services.TryAddSingleton<GtfsValidationSignal>();
        services.TryAddSingleton<GtfsDownloadLinks>();

        return services;
    }

    /// <summary>
    /// The one outbound client this service has, and the only thing it may reach.
    /// </summary>
    /// <remarks>
    /// <b><c>AllowAutoRedirect</c> is off, deliberately.</b> Automatic redirect handling would
    /// follow a shortener's chain wherever it led and report only where it ended — by which point
    /// the request to the private address has already been made. <see cref="MapsLinkResolver"/>
    /// walks the chain itself and re-checks the allowlist at every hop.
    /// </remarks>
    private static IServiceCollection AddMapsLinkClient(this IServiceCollection services)
    {
        services.AddHttpClient(MapsLinkResolver.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<TransitOptions>>().Value;

                // The resolver owns the real budget (BR-23.4's 3 s across the retry); this is the
                // backstop for a socket that never answers at all.
                client.Timeout = options.MapsLink.Timeout + TimeSpan.FromSeconds(2);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MageRide/1.0 (+https://mageride.lk)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            });

        return services;
    }
}

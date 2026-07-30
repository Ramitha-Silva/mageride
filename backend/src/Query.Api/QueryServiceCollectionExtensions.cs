using MageRide.Query.Configuration;
using MageRide.Query.Geo;
using MageRide.Query.Live;
using MageRide.Query.Destinations;
using MageRide.Query.Persistence;
using MageRide.Shared.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MageRide.Query;

/// <summary>query-svc's own registrations, kept out of the composition root.</summary>
public static class QueryServiceCollectionExtensions
{
    /// <summary>Binds <see cref="QueryOptions"/> and registers every reader this service has.</summary>
    public static IServiceCollection AddQueryServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<QueryOptions>()
            .Bind(configuration.GetSection(QueryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: every one of these is stateless over an injected connection factory or Redis
        // multiplexer, and this service owns no per-request state at all — it writes nothing.
        services.AddSingleton<IQueryConnectionFactory, QueryConnectionFactory>();
        services.AddSingleton<ILiveVehicleIndex, LiveVehicleIndex>();
        services.AddSingleton<ILiveReadRepository, LiveReadRepository>();
        services.AddSingleton<ITripRepository, TripRepository>();
        services.AddSingleton<IEarningsRepository, EarningsRepository>();
        services.AddSingleton<IPlaceRepository, PlaceRepository>();
        services.AddSingleton<EtaEstimator>();
        services.AddSingleton<INearbyService, NearbyService>();
        services.AddSingleton<IGeocoder, NominatimClient>();
        services.AddSingleton<IDestinationOptionsService, DestinationOptionsService>();

        var settings = configuration.GetSection(QueryOptions.SectionName).Get<QueryOptions>() ?? new QueryOptions();

        AddDownstream(services, NominatimClient.HttpClientName, settings.NominatimBaseUrl, client =>
        {
            // Nominatim's usage policy asks for an identifying User-Agent even on a self-hosted
            // instance, and it is what an operator greps the access log for.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.NominatimUserAgent);

            // Well inside D6' §8.3's 15 s end-to-end API budget: a geocode is one leg of a request the
            // passenger is watching a spinner for, and the retry pipeline may take two attempts.
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        AddDownstream(
            services,
            DestinationOptionsService.TransitClientName,
            settings.TransitBaseUrl,
            client => client.Timeout = TimeSpan.FromSeconds(5));

        AddDownstream(
            services,
            DestinationOptionsService.FareClientName,
            settings.FareBaseUrl,
            // Six tier quotes go out at once and the destination screen waits for the slowest, so this
            // is deliberately tighter than the others: a tier that has not priced in three seconds is
            // better omitted than shown late.
            client => client.Timeout = TimeSpan.FromSeconds(3));

        return services;
    }

    /// <summary>
    /// Registers a named client for an optional downstream.
    /// </summary>
    /// <remarks>
    /// The client is registered whether or not a base URL is configured, so resolving it never throws —
    /// the callers check configuration themselves and each has a documented degraded answer. A base
    /// address is only set when one exists; without it a relative request fails fast and is handled as
    /// an unavailable downstream.
    /// <para>
    /// <c>AddMageRideResilience</c> is D6' §8.3's retry, breaker and timeout. Safe on all three of these
    /// because every one is a <c>GET</c>: the pipeline's own remarks fence it off from non-idempotent
    /// calls.
    /// </para>
    /// </remarks>
    private static void AddDownstream(
        IServiceCollection services, string name, string? baseUrl, Action<HttpClient> configure)
    {
        services.AddHttpClient(name, client =>
            {
                if (!string.IsNullOrWhiteSpace(baseUrl))
                {
                    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                }

                configure(client);
            })
            .AddMageRideResilience();
    }
}

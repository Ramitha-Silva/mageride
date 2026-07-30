using MageRide.Content.Caching;
using MageRide.Content.Configuration;
using MageRide.Content.Persistence;
using MageRide.Content.Publishing;
using MageRide.Content.Reading;

namespace MageRide.Content;

/// <summary>content-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class ContentServiceCollectionExtensions
{
    public static IServiceCollection AddContentServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ContentOptions>()
            .Bind(configuration.GetSection(ContentOptions.SectionName))
            .Configure(options =>
            {
                // D7' §4.2 spells the TTL `Cache__Ttl` — unprefixed, in seconds — and that is what
                // `.env.app.example` ships. Honoured unless the service's own key is set, so an
                // operator following the spec is not setting a variable nothing reads.
                if (configuration.GetSection($"{ContentOptions.SectionName}:CacheTtl").Exists())
                {
                    return;
                }

                var legacy = configuration.GetSection($"{ContentOptions.LegacyCacheSection}:Ttl").Value;

                if (int.TryParse(legacy, out var seconds) && seconds > 0)
                {
                    options.CacheTtl = TimeSpan.FromSeconds(seconds);
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: every repository holds a connection factory and a handful of SQL strings, and
        // the cache loaders run outside a request scope.
        services.AddSingleton<IReferenceDataRepository, ReferenceDataRepository>();
        services.AddSingleton<ITemplateRepository, TemplateRepository>();
        services.AddSingleton<IFaqRepository, FaqRepository>();
        services.AddSingleton<IBroadcastRepository, BroadcastRepository>();

        // The cache is the service's state and is shared by every request and by the purge
        // subscriber, so it can only be a singleton.
        services.AddSingleton<ContentCache>();
        services.AddSingleton<IContentInvalidator, RedisContentInvalidator>();

        services.AddSingleton<ContentQueries>();
        services.AddSingleton<ContentPublisher>();

        // Registered rather than hosted here, so a test can resolve it without it also subscribing.
        // ContentApplication decides whether it is hosted.
        services.AddSingleton<ContentInvalidationSubscriber>();

        return services;
    }
}

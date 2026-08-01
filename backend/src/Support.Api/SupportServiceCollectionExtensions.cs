using MageRide.Support.Configuration;
using MageRide.Support.Faq;
using MageRide.Support.Persistence;
using MageRide.Support.Screenshots;
using MageRide.Support.Tickets;
using MageRide.Shared.Storage;

namespace MageRide.Support;

/// <summary>support-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class SupportServiceCollectionExtensions
{
    public static IServiceCollection AddSupportServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SupportOptions>()
            .Bind(configuration.GetSection(SupportOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: each holds a connection factory and a handful of SQL strings.
        services.AddSingleton<IFaqRepository, FaqRepository>();
        services.AddSingleton<ITicketRepository, TicketRepository>();
        services.AddSingleton<IUploadRepository, UploadRepository>();

        // The store holds a root path and the links hold a signing key; both are read-only after
        // construction, and the key is resolved once so a missing one is warned about once.
        services.AddSingleton<IScreenshotStore, FileSystemScreenshotStore>();

        // Δ D-36. `Support:ScreenshotRoot` remains the filesystem fallback's root.
        services.AddMageRideObjectStore(
            configuration, ObjectBucket.Screenshots, configuration["Support:ScreenshotRoot"]);
        services.AddSingleton<IScreenshotLinks, ScreenshotLinks>();

        // The FAQ service composes read-only repositories and holds no transaction.
        services.AddSingleton<IFaqService, FaqService>();

        // Scoped: it takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<ITicketService, TicketService>();

        return services;
    }
}

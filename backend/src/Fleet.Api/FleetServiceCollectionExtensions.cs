using MageRide.Fleet.Authorization;
using MageRide.Fleet.Bulk;
using MageRide.Fleet.Configuration;
using MageRide.Fleet.Documents;
using MageRide.Fleet.Operations;
using MageRide.Fleet.Organisation;
using MageRide.Fleet.Payouts;
using MageRide.Fleet.Persistence;
using MageRide.Fleet.Subscriptions;
using MageRide.Fleet.Vehicles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet;

/// <summary>fleet-svc's own registrations. The cross-cutting half is <c>AddMageRideDefaults</c>.</summary>
public static class FleetServiceCollectionExtensions
{
    public static IServiceCollection AddFleetServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FleetOptions>()
            .Bind(configuration.GetSection(FleetOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Singletons: each holds a connection factory and a handful of SQL strings.
        services.AddSingleton<IFleetRepository, FleetRepository>();
        services.AddSingleton<IFleetMemberRepository, FleetMemberRepository>();
        services.AddSingleton<IPortalUserRepository, PortalUserRepository>();
        services.AddSingleton<IPayoutProfileRepository, PayoutProfileRepository>();
        services.AddSingleton<IPayoutDocumentRepository, PayoutDocumentRepository>();
        services.AddSingleton<IFleetVehicleRepository, FleetVehicleRepository>();

        // C059.
        services.AddSingleton<IFleetAssignmentRepository, FleetAssignmentRepository>();
        services.AddSingleton<IVehicleDocumentRepository, VehicleDocumentRepository>();
        services.AddSingleton<IFleetScheduleRepository, FleetScheduleRepository>();
        services.AddSingleton<IFleetInsightsRepository, FleetInsightsRepository>();
        services.AddSingleton<IFleetBulkJobRepository, FleetBulkJobRepository>();

        // The scoped reader holds the connection factory and two option flags and is stateless per
        // call — each read opens its own transaction and sets the role and GUC inside it.
        services.AddSingleton<FleetScopedReader>();
        services.AddSingleton<IFleetScopedReader>(provider => provider.GetRequiredService<FleetScopedReader>());

        // Holds a root path, resolved once at construction.
        services.AddSingleton<IDocumentStore, FileSystemDocumentStore>();

        // Holds one HMAC key, resolved once at construction.
        services.AddSingleton<IErrorReportLinks, ErrorReportLinks>();

        // Scoped: each takes IUnitOfWorkFactory, which the kernel registers scoped so one request
        // holds at most one transaction at a time.
        services.AddScoped<IFleetService, FleetService>();
        services.AddScoped<IFleetVerificationService, FleetVerificationService>();
        services.AddScoped<IPayoutProfileService, PayoutProfileService>();
        services.AddScoped<IClassificationService, ClassificationService>();

        // C059.
        services.AddScoped<IFleetVehicleService, FleetVehicleService>();
        services.AddScoped<IVehicleDocumentService, VehicleDocumentService>();
        services.AddScoped<IVehicleApprovalService, VehicleApprovalService>();
        services.AddScoped<IBulkVehicleImportService, BulkVehicleImportService>();
        services.AddScoped<IAssignmentService, AssignmentService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IFleetInsightsService, FleetInsightsService>();

        // Scoped, because it resolves IUnitOfWorkFactory for the two reads that decide the
        // request. `AddEndpointFilter<T>` resolves the filter from the request's own scope.
        services.AddScoped<FleetAccessFilter>();

        AddOutboundClients(services, configuration);

        return services;
    }

    /// <summary>
    /// The three hops this service makes, each mapped only when it has somewhere to go.
    /// </summary>
    /// <remarks>
    /// <b>An unconfigured base address is a switch, not a default.</b> Without ocr-svc a document is
    /// stored and never read, and its slot holds the vehicle out of APPROVED — the honest outcome,
    /// and the one registry-svc produces for the Driver App's half of AL-50. Without
    /// provisioning-svc or subscription-svc the routes that would have needed them are not mapped at
    /// all, because a bind that silently did nothing would leave an operator believing a tracker was
    /// armed, and a subscriber roster served from nowhere would be a screen full of zeroes. Every one
    /// is announced at start-up.
    /// </remarks>
    private static void AddOutboundClients(IServiceCollection services, IConfiguration configuration)
    {
        var section = FleetOptions.SectionName;

        if (!string.IsNullOrWhiteSpace(configuration[$"{section}:{nameof(FleetOptions.OcrBaseUrl)}"]))
        {
            services.AddHttpClient(OcrVehicleDocumentExtractionClient.HttpClientName)
                .ConfigureHttpClient((provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<FleetOptions>>().Value;

                    client.BaseAddress = new Uri(options.OcrBaseUrl!, UriKind.Absolute);
                    client.Timeout = options.OcrTimeout;
                });

            // No resilience pipeline on this hop, deliberately — registry-svc's reasoning: ocr-svc
            // already retries the leg that actually fails (Gemini, D6' §8.3) and has its own on-prem
            // fallback behind it, so a retry here would re-run a whole extraction pass while an
            // operator waits on an upload.
            services.TryAddSingleton<IVehicleDocumentExtractionClient, OcrVehicleDocumentExtractionClient>();
        }

        services.TryAddSingleton<IVehicleDocumentExtractionClient, UnconfiguredVehicleDocumentExtractionClient>();

        if (!string.IsNullOrWhiteSpace(configuration[$"{section}:{nameof(FleetOptions.ProvisioningBaseUrl)}"]))
        {
            services.AddHttpClient(TrackerBindingService.HttpClientName)
                .ConfigureHttpClient((provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<FleetOptions>>().Value;

                    client.BaseAddress = new Uri(options.ProvisioningBaseUrl!, UriKind.Absolute);
                    client.Timeout = options.ProxyTimeout;
                });

            services.AddScoped<ITrackerBindingService, TrackerBindingService>();
        }

        if (!string.IsNullOrWhiteSpace(configuration[$"{section}:{nameof(FleetOptions.SubscriptionBaseUrl)}"]))
        {
            services.AddHttpClient(SubscriptionProxy.HttpClientName)
                .ConfigureHttpClient((provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<FleetOptions>>().Value;

                    client.BaseAddress = new Uri(options.SubscriptionBaseUrl!, UriKind.Absolute);
                    client.Timeout = options.ProxyTimeout;
                });

            services.AddScoped<ISubscriptionProxy, SubscriptionProxy>();
        }

        if (!string.IsNullOrWhiteSpace(configuration[$"{section}:{nameof(FleetOptions.NotificationBaseUrl)}"]))
        {
            services.AddHttpClient(ScheduleAlarmWorker.HttpClientName)
                .ConfigureHttpClient((provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<FleetOptions>>().Value;

                    client.BaseAddress = new Uri(options.NotificationBaseUrl!, UriKind.Absolute);
                    client.Timeout = options.ProxyTimeout;
                });
        }

        // The worker resolves its own scope per pass and is registered whatever the notification
        // hop's state: the sweep's *first* job is to record that a departure was missed, which the
        // Fleet Portal reads whether or not anybody's phone rang.
        services.AddSingleton<ScheduleAlarmWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<ScheduleAlarmWorker>());
    }
}

using MageRide.Registry.Configuration;
using MageRide.Registry.Observability;
using MageRide.Registry.Onboarding;
using MageRide.Registry.Persistence;
using MageRide.Registry.Sharing;
using MageRide.Registry.Vehicles;
using MageRide.Shared.Observability;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MageRide.Registry;

/// <summary>Everything registry-svc owns on top of the shared kernel.</summary>
public static class RegistryServiceCollectionExtensions
{
    /// <summary>The service's scrape-time gauges (C119). One meter, disposed with the host.</summary>
    private static ScrapedGauges Gauges(IServiceProvider services)
    {
        var gauges = new ScrapedGauges(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger<ScrapedGauges>());

        ExpiredDocumentsGauge.Publish(gauges);

        return gauges;
    }

    public static IServiceCollection AddRegistryServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RegistryOptions>()
            .Bind(configuration.GetSection(RegistryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // E-03's tracker. Registered as a concrete type as well, so a test can drive one sweep
        // deterministically instead of waiting on the ticker (the shape OfferExpiryWorker uses).
        services.AddSingleton<DocumentExpiryWorker>();

        // Δ C119 (R-20). ADD §13.3.1 row 8 as a gauge on the platform meter — "is the worker above
        // actually running", asked of the column that worker writes. Started as a hosted service so
        // the gauge exists from the first scrape; nothing else resolves it, because a scrape reads
        // the meter and not the service.
        services.AddSingleton(sp => Gauges(sp));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ScrapedGauges>());

        services.AddSingleton<IVehicleRepository, VehicleRepository>();
        services.AddSingleton<IDriverProfileRepository, DriverProfileRepository>();

        // C028.
        services.AddSingleton<IEligibilityRepository, EligibilityRepository>();
        services.AddSingleton<IShareRepository, ShareRepository>();
        services.AddSingleton<ISubscriptionRepository, SubscriptionRepository>();
        services.AddSingleton<IDriverLiveVehicleCache, DriverLiveVehicleCache>();

        // C029.
        services.AddSingleton<IDocumentRepository, DocumentRepository>();
        services.AddSingleton<IOnboardingStepRepository, OnboardingStepRepository>();
        services.AddSingleton<IDriverPayoutProfileRepository, DriverPayoutProfileRepository>();
        services.AddSingleton<IPayoutDocumentStore, PayoutDocumentStore>();

        // Δ MCS-01. The upload surface for onboarding documents — the one `docs.uploads` never had,
        // which left Profile Setup and the Mode-C wizard unreachable on a real gateway.
        services.AddSingleton<IOnboardingDocumentStore, OnboardingDocumentStore>();

        // Δ MCS-25 — mints the signed, expiring URL the profile reads carry instead of the raw
        // `s3://` / `file://` pointer, which no image loader can follow.
        services.AddSingleton<IDriverPhotoLinks, DriverPhotoLinks>();

        // Δ D-36. `Registry:PayoutDocumentRoot` stays honoured as the filesystem fallback's root so
        // a deployment that has not set `Storage:*` behaves exactly as it did, and so the rows it
        // already wrote go on resolving after it has.
        services.AddMageRideObjectStore(
            configuration, ObjectBucket.Documents, configuration["Registry:PayoutDocumentRoot"]);

        // C054. `Registry:OcrBaseUrl` is what decides which of the two lands: with it, the real
        // hop to ocr-svc; without it, the honest no-op below. TryAdd is still what registers the
        // fallback, so a test (or a future composition) can put its own client in ahead of both.
        var ocrBaseUrl = configuration[$"{RegistryOptions.SectionName}:{nameof(RegistryOptions.OcrBaseUrl)}"];

        if (!string.IsNullOrWhiteSpace(ocrBaseUrl))
        {
            services.AddHttpClient(OcrDocumentExtractionClient.HttpClientName)
                .ConfigureHttpClient((provider, client) =>
                {
                    var options = provider.GetRequiredService<
                        Microsoft.Extensions.Options.IOptions<RegistryOptions>>().Value;

                    client.BaseAddress = new Uri(options.OcrBaseUrl!, UriKind.Absolute);
                    client.Timeout = options.OcrTimeout;
                });

            // No resilience pipeline on this hop, deliberately. ocr-svc already retries the leg
            // that actually fails (Gemini, D6' §8.3) and falls back to its own on-prem engine
            // behind it, so a retry here would re-run a whole extraction pass — a second Tesseract
            // read and a second `docs.extractions` row — while a driver waits on a step save.
            services.TryAddSingleton<IDocumentExtractionClient, OcrDocumentExtractionClient>();
        }

        // Without one every document comes back unread and every document step lands
        // pending_review — the honest outcome, and the one D5' §14.1a prescribes for a document
        // that did not extract.
        services.TryAddSingleton<IDocumentExtractionClient, UnconfiguredDocumentExtractionClient>();

        // Scoped: each opens a unit of work per command, so its lifetime is the request's.
        services.AddScoped<IVehicleService, VehicleService>();
        services.AddScoped<IShareService, ShareService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<IDriverPayoutProfileService, DriverPayoutProfileService>();

        return services;
    }
}

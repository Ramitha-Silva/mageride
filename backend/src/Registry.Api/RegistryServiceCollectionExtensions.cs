using MageRide.Registry.Configuration;
using MageRide.Registry.Onboarding;
using MageRide.Registry.Persistence;
using MageRide.Registry.Sharing;
using MageRide.Registry.Vehicles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Registry;

/// <summary>Everything registry-svc owns on top of the shared kernel.</summary>
public static class RegistryServiceCollectionExtensions
{
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

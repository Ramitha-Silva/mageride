using MageRide.Provisioning.Bulk;
using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Persistence;
using MageRide.Provisioning.Trackers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MageRide.Provisioning;

/// <summary>Everything provisioning-svc owns on top of the shared kernel.</summary>
public static class ProvisioningServiceCollectionExtensions
{
    public static IServiceCollection AddProvisioningServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ProvisioningOptions>()
            .Bind(configuration.GetSection(ProvisioningOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // D7' §4.2 spells the device PKI settings as two flat sections, `StepCa:*` and `Cred:*`,
        // and the compose files already set them. Bound as one options object over both rather
        // than renamed, because the env-file names are the deployment contract.
        services.AddOptions<DevicePkiOptions>()
            .Bind(configuration.GetSection(DevicePkiOptions.StepCaSectionName))
            .Bind(configuration.GetSection(DevicePkiOptions.CredentialSectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // Singleton: it holds the CA's private keys and the loaded certificates, and creating one
        // per request would re-read and re-parse the volume on every bind.
        services.AddSingleton<ICertificateAuthority, EmbeddedStepCa>();
        services.AddSingleton<ICrlService, CrlService>();
        services.AddSingleton<IErrorReportLinks, ErrorReportLinks>();

        services.AddSingleton<ITrackerBindingRepository, TrackerBindingRepository>();
        services.AddSingleton<IDeviceCertificateRepository, DeviceCertificateRepository>();
        services.AddSingleton<IImeiSightingRepository, ImeiSightingRepository>();
        services.AddSingleton<IBulkJobRepository, BulkJobRepository>();
        services.AddSingleton<IVehicleLookupRepository, VehicleLookupRepository>();

        services.AddSingleton<ITrackerCache, TrackerCache>();

        // Registered as concrete types too, so a test can drive one sweep deterministically
        // instead of waiting on a ticker — the shape DocumentExpiryWorker (C029) uses.
        services.AddSingleton<CredentialRotationWorker>();
        services.AddSingleton<BulkMintWorker>();

        // Scoped: each opens a unit of work per command, so its lifetime is the request's.
        services.AddScoped<ITrackerService, TrackerService>();
        services.AddScoped<IBulkTrackerService, BulkTrackerService>();

        return services;
    }
}

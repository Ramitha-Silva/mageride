using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Storage;

/// <summary>
/// Writes to the bucket; reads from the bucket <em>or</em> from wherever the row says.
/// </summary>
/// <remarks>
/// <b>The read fallback is not defensive coding, it is a migration.</b> Every <c>docs.uploads</c>
/// row written before D-36 was wired holds a <c>file://</c> pointer, and those are the documents
/// behind every application currently in a Verification Officer's queue. Switching the write path
/// to S3 without keeping the old read path would make all of them unopenable on the day of the
/// deploy. New objects go to the bucket; old ones keep resolving; nothing has to be backfilled
/// before the switch is safe.
/// </remarks>
internal sealed class CompositeObjectStore(S3ObjectStore primary, FileSystemObjectStore legacy) : IObjectStore
{
    public string Description => $"{primary.Description}; legacy reads from {legacy.Description}";

    public Task<StoredObject> PutAsync(ObjectPutRequest request, CancellationToken cancellationToken) =>
        primary.PutAsync(request, cancellationToken);

    public async Task<ObjectBytes?> ReadAsync(string storageUrl, CancellationToken cancellationToken) =>
        await primary.ReadAsync(storageUrl, cancellationToken)
        ?? await legacy.ReadAsync(storageUrl, cancellationToken);

    public bool TryPresign(string storageUrl, TimeSpan ttl, out string url) =>
        primary.TryPresign(storageUrl, ttl, out url);

    public Task EnsureAsync(CancellationToken cancellationToken) =>
        primary.EnsureBucketAsync(cancellationToken);
}

/// <summary>Prepares the bucket once, on the way up.</summary>
/// <remarks>
/// A hosted service rather than a call in <c>Program</c>: it must run in every host that has a
/// bucket configured, and putting it here means a service that adds storage later cannot forget it.
/// </remarks>
internal sealed class ObjectStoreInitialiser(IObjectStore store, ILogger<ObjectStoreInitialiser> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Document storage (D-36): {Description}.", store.Description);

        return store switch
        {
            CompositeObjectStore composite => composite.EnsureAsync(cancellationToken),
            S3ObjectStore s3 => s3.EnsureBucketAsync(cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>D-36's document store, wired once for every service that touches bytes.</summary>
public static class StorageServiceCollectionExtensions
{
    /// <param name="legacyRoot">
    /// The service's own pre-D-36 document directory (<c>Fleet:DocumentRoot</c>,
    /// <c>Registry:PayoutDocumentRoot</c>, <c>Support:ScreenshotRoot</c>, <c>Ocr:Storage:Root</c>).
    /// Still honoured so a deployment that has not set <c>Storage:*</c> behaves exactly as it did,
    /// and so the rows it already wrote go on resolving after it has.
    /// </param>
    public static IServiceCollection AddMageRideObjectStore(
        this IServiceCollection services,
        IConfiguration configuration,
        ObjectBucket bucket = ObjectBucket.Documents,
        string? legacyRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ObjectStoreOptions>(configuration.GetSection(ObjectStoreOptions.SectionName));

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ObjectStoreOptions>>();

            return new FileSystemObjectStore(
                options, provider.GetRequiredService<ILogger<FileSystemObjectStore>>(), legacyRoot);
        });

        services.AddSingleton<IObjectStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ObjectStoreOptions>>().Value;
            var legacy = provider.GetRequiredService<FileSystemObjectStore>();

            if (!options.IsS3Configured(bucket))
            {
                provider.GetRequiredService<ILogger<FileSystemObjectStore>>().LogWarning(
                    "Storage:S3:Endpoint and/or the {Bucket} bucket are not configured, so documents are written "
                    + "to the local filesystem and D-36 is NOT in force: there is no server-side encryption, no "
                    + "NFR-28 expiry, and a document written by one replica cannot be read by another. Point "
                    + "Storage:S3:* at MinIO (dev, replica) or R2/S3 (production).",
                    bucket);

                return legacy;
            }

            return new CompositeObjectStore(
                new S3ObjectStore(
                    BuildClient(options),
                    provider.GetRequiredService<IOptions<ObjectStoreOptions>>(),
                    bucket,
                    provider.GetRequiredService<ILogger<S3ObjectStore>>()),
                legacy);
        });

        services.AddHostedService<ObjectStoreInitialiser>();

        return services;
    }

    private static IAmazonS3 BuildClient(ObjectStoreOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.S3.Endpoint,
            // MinIO and any single-host endpoint can only do path style: `bucket.localhost:9000`
            // does not resolve, and a virtual-host request to it fails at DNS rather than at S3.
            ForcePathStyle = options.S3.ForcePathStyle,
            AuthenticationRegion = options.S3.Region,
            // Presigned URLs are built from the config, not from ServiceURL's scheme: without this
            // a plain-HTTP endpoint (MinIO in dev and on the replica) is handed to the officer's
            // browser as `https://…`, which fails the TLS handshake against a server that speaks
            // none. The bucket API calls succeed either way, so this shows up only as broken
            // document thumbnails — the one path nothing else exercises.
            UseHttp = options.S3.Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ?? false,
        };

        // Explicit credentials when given, otherwise the ambient chain — which is what lets a
        // production deployment use an instance role and never put a secret in configuration.
        return string.IsNullOrWhiteSpace(options.S3.AccessKey) || string.IsNullOrWhiteSpace(options.S3.SecretKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.S3.AccessKey, options.S3.SecretKey), config);
    }
}

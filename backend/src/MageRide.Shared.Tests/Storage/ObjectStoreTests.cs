using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MageRide.Shared.Errors;
using MageRide.Shared.Storage;
using MageRide.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Tests.Storage;

/// <summary>
/// D-36's document store, against a real MinIO (C063 Δ).
/// </summary>
/// <remarks>
/// <para>
/// Almost every claim worth making here is a claim about S3 behaviour rather than about our code —
/// that a presigned URL an <b>unauthenticated</b> client follows returns the bytes, that a
/// lifecycle rule scoped to one prefix reads back scoped to that prefix, that encryption is
/// reported on the stored object. A fake <c>IObjectStore</c> would assert that our test double does
/// what we wrote it to do.
/// </para>
/// <para>
/// The one rule that is ours and matters most is the retention split: raw evidence expires under
/// NFR-28 and a driver's own LankaQR must not, because it is what a passenger scans to pay them on
/// every ride (AL-59).
/// </para>
/// </remarks>
[Collection<MinioCollection>]
public sealed class ObjectStoreTests(MinioFixture minio)
{
    [Fact]
    public async Task An_uploaded_document_comes_back_byte_for_byte()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var store = await StoreAsync();
        var bytes = RandomNumberGenerator.GetBytes(4096);

        var stored = await store.PutAsync(
            new ObjectPutRequest("drivers/nimal/licence.jpg", new MemoryStream(bytes), "image/jpeg",
                MaxBytes: 1_000_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("s3://", stored.StorageUrl, StringComparison.Ordinal);
        Assert.Equal(bytes.Length, stored.Length);
        Assert.Equal(SHA256.HashData(bytes), stored.Sha256);

        var read = await store.ReadAsync(stored.StorageUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(bytes, read.Bytes.ToArray());
        Assert.Equal("image/jpeg", read.ContentType);
    }

    /// <summary>
    /// AL-39's other half: a link the officer's browser can follow, that stops working.
    /// </summary>
    [Fact]
    public async Task A_presigned_url_serves_the_object_to_a_client_holding_no_credentials()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var store = await StoreAsync();
        var bytes = Encoding.UTF8.GetBytes("a bank statement");

        var stored = await store.PutAsync(
            new ObjectPutRequest("payout/proof.pdf", new MemoryStream(bytes), "application/pdf",
                MaxBytes: 1_000_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        Assert.True(store.TryPresign(stored.StorageUrl, TimeSpan.FromMinutes(5), out var url));

        Assert.StartsWith(minio.ServiceUrl, url, StringComparison.Ordinal);

        // No Authorization header, no AWS credentials — an <img> tag is what follows this.
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(new Uri(url), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // And the same object is NOT public: the signature is the credential, not a bucket ACL.
        using var unsigned = await anonymous.GetAsync(
            new Uri(url[..url.IndexOf('?', StringComparison.Ordinal)]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
    }

    [Fact]
    public async Task An_expired_presigned_url_stops_working()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var store = await StoreAsync();

        var stored = await store.PutAsync(
            new ObjectPutRequest("payout/short-lived.pdf", new MemoryStream([1, 2, 3]), "application/pdf",
                MaxBytes: 1_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        // Already in the past, so nothing has to wait for a clock.
        Assert.True(store.TryPresign(stored.StorageUrl, TimeSpan.FromSeconds(-30), out var url));

        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(new Uri(url), TestContext.Current.CancellationToken);

        // Not pinned to a status: MinIO answers 400 to an expired signature where S3 answers 403.
        // What AL-39's "short-lived" actually requires is that the bytes stop being served, and
        // asserting a provider's choice of code would make this fail on a provider swap.
        Assert.False(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// The rule that keeps drivers getting paid: NFR-28 expires evidence, not the payment QR.
    /// </summary>
    [Fact]
    public async Task Raw_evidence_expires_and_a_retained_object_never_does()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var bucket = $"mageride-retention-{Guid.CreateVersion7():N}";
        var store = await StoreAsync(bucket);

        var evidence = await store.PutAsync(
            new ObjectPutRequest("payout/statement.pdf", new MemoryStream([1]), "application/pdf",
                MaxBytes: 1_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        // AL-59. A passenger scans this on every ride; it is not evidence and must outlive 90 days.
        var lankaQr = await store.PutAsync(
            new ObjectPutRequest("payout/lankaqr.png", new MemoryStream([2]), "image/png",
                MaxBytes: 1_000, Retention: null),
            TestContext.Current.CancellationToken);

        Assert.Contains($"/{ObjectRetentionClasses.Ephemeral}/", evidence.StorageUrl, StringComparison.Ordinal);
        Assert.Contains($"/{ObjectRetentionClasses.Retained}/", lankaQr.StorageUrl, StringComparison.Ordinal);

        // The bucket enforces it, not a sweeper somebody still has to write.
        using var client = Client();

        var lifecycle = await client.GetLifecycleConfigurationAsync(
            bucket, TestContext.Current.CancellationToken);

        var rule = Assert.Single(lifecycle.Configuration.Rules);

        Assert.Equal(LifecycleRuleStatus.Enabled, rule.Status);
        Assert.Equal(90, rule.Expiration.Days);

        var prefix = Assert.IsType<LifecyclePrefixPredicate>(rule.Filter.LifecycleFilterPredicate);

        // Scoped to the ephemeral prefix. A bucket-wide rule would delete every driver's LankaQR
        // 90 days after they uploaded it and the payment rail would break silently.
        Assert.Equal($"{ObjectRetentionClasses.Ephemeral}/", prefix.Prefix);
    }

    [Fact]
    public async Task Stored_objects_are_encrypted_at_rest()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var bucket = $"mageride-sse-{Guid.CreateVersion7():N}";
        var store = await StoreAsync(bucket);

        var stored = await store.PutAsync(
            new ObjectPutRequest("licence.jpg", new MemoryStream([1, 2, 3]), "image/jpeg",
                MaxBytes: 1_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        using var client = Client();

        var metadata = await client.GetObjectMetadataAsync(
            bucket,
            stored.StorageUrl[$"s3://{bucket}/".Length..],
            TestContext.Current.CancellationToken);

        // SSE-S3, because MinIO cannot do SSE-KMS without a KES server. D-36 says SSE-KMS and
        // production sets Storage:KmsKeyId to get it; the difference is named in the options and
        // announced at start-up rather than left to an unread default.
        Assert.Equal(ServerSideEncryptionMethod.AES256, metadata.ServerSideEncryptionMethod);
    }

    [Fact]
    public async Task An_upload_over_the_ceiling_is_refused_and_stores_nothing()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var bucket = $"mageride-ceiling-{Guid.CreateVersion7():N}";
        var store = await StoreAsync(bucket);

        var error = await Assert.ThrowsAsync<MageRideException>(async () => await store.PutAsync(
            new ObjectPutRequest("big.bin", new MemoryStream(new byte[4096]), "application/octet-stream",
                MaxBytes: 1024, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken));

        Assert.Equal(MageRideErrors.PayloadTooLarge.Code, error.Error.Code);

        // Counted while streaming, so the refusal happens before anything is uploaded — nothing is
        // left in the bucket for a request that was rejected.
        using var client = Client();

        var listing = await client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = bucket }, TestContext.Current.CancellationToken);

        Assert.Empty(listing.S3Objects ?? []);
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var store = await StoreAsync();

        await Assert.ThrowsAsync<MageRideValidationException>(async () => await store.PutAsync(
            new ObjectPutRequest("empty.bin", new MemoryStream(), "application/octet-stream",
                MaxBytes: 1024, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The migration property: switching to a bucket must not orphan what is already stored.
    /// </summary>
    /// <remarks>
    /// Every <c>docs.uploads</c> row written before D-36 was wired holds a <c>file://</c> pointer,
    /// and those are the documents behind the applications currently in an officer's queue. A
    /// deployment that could no longer read them would lose the evidence for all of them on the day
    /// of the switch.
    /// </remarks>
    [Fact]
    public async Task A_document_written_before_the_bucket_existed_still_reads_afterwards()
    {
        Assert.SkipWhen(!minio.IsAvailable, minio.SkipReason ?? string.Empty);

        var root = Path.Combine(Path.GetTempPath(), $"mageride-legacy-{Guid.CreateVersion7():N}");

        var onDisk = BuildStore(new ObjectStoreOptions { LocalRoot = root });

        var legacy = await onDisk.PutAsync(
            new ObjectPutRequest("old/licence.jpg", new MemoryStream([7, 7, 7]), "image/jpeg",
                MaxBytes: 1_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("file://", legacy.StorageUrl, StringComparison.Ordinal);

        // Now the same service, with a bucket configured.
        var afterSwitch = BuildStore(new ObjectStoreOptions
        {
            S3 = Endpoint(),
            DocumentsBucket = $"mageride-migration-{Guid.CreateVersion7():N}",
            LocalRoot = root,
        });

        await EnsureAsync(afterSwitch, TestContext.Current.CancellationToken);

        var read = await afterSwitch.ReadAsync(legacy.StorageUrl, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal<byte[]>([7, 7, 7], read.Bytes.ToArray());

        // And a new document goes to the bucket.
        var fresh = await afterSwitch.PutAsync(
            new ObjectPutRequest("new/licence.jpg", new MemoryStream([8]), "image/jpeg",
                MaxBytes: 1_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("s3://", fresh.StorageUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>storage_url</c> is a value out of a table, not out of a request — and a traversal in it
    /// would be read and posted to an external model by ocr-svc.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/../../../etc/shadow")]
    public async Task A_pointer_that_climbs_out_of_the_root_is_refused(string pointer)
    {
        var store = BuildStore(new ObjectStoreOptions
        {
            LocalRoot = Path.Combine(Path.GetTempPath(), $"mageride-traversal-{Guid.CreateVersion7():N}"),
        });

        Assert.Null(await store.ReadAsync(pointer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_filesystem_store_cannot_presign_and_says_so()
    {
        var store = BuildStore(new ObjectStoreOptions
        {
            LocalRoot = Path.Combine(Path.GetTempPath(), $"mageride-nopresign-{Guid.CreateVersion7():N}"),
        });

        var stored = await store.PutAsync(
            new ObjectPutRequest("x.bin", new MemoryStream([1]), "application/octet-stream",
                MaxBytes: 1_000, Retention: TimeSpan.FromDays(90)),
            TestContext.Current.CancellationToken);

        // False rather than a made-up URL: admin-bff falls back to its HMAC-signed pointer, and the
        // difference between a wired D-36 and an unwired one stays visible.
        Assert.False(store.TryPresign(stored.StorageUrl, TimeSpan.FromMinutes(5), out _));
    }

    // -------------------------------------------------------------------------------------------

    private async Task<IObjectStore> StoreAsync(string? bucket = null)
    {
        var store = BuildStore(new ObjectStoreOptions
        {
            S3 = Endpoint(),
            DocumentsBucket = bucket ?? $"mageride-docs-{Guid.CreateVersion7():N}",
        });

        // What the hosted initialiser does on the way up: create the bucket, apply NFR-28's rule.
        // Driven through the registered IHostedService rather than by reaching for an internal
        // method, so the test exercises the same path a booting service does.
        await EnsureAsync(store, TestContext.Current.CancellationToken);

        return store;
    }

    private static Task EnsureAsync(IObjectStore store, CancellationToken cancellationToken) =>
        Initialisers.TryGetValue(store, out var initialiser)
            ? initialiser.StartAsync(cancellationToken)
            : Task.CompletedTask;

    private static readonly Dictionary<IObjectStore, IHostedService> Initialisers = [];

    /// <summary>Through the real DI extension, so the wiring is what a service actually gets.</summary>
    private static IObjectStore BuildStore(ObjectStoreOptions options)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Storage:S3:Endpoint"] = options.S3.Endpoint,
            ["Storage:DocumentsBucket"] = options.DocumentsBucket,
            ["Storage:S3:AccessKey"] = options.S3.AccessKey,
            ["Storage:S3:SecretKey"] = options.S3.SecretKey,
            ["Storage:LocalRoot"] = options.LocalRoot,
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMageRideObjectStore(configuration);

        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IObjectStore>();

        Initialisers[store] = provider.GetServices<IHostedService>().Single();

        return store;
    }

    private ObjectStoreOptions.S3Options Endpoint() => new()
    {
        Endpoint = minio.ServiceUrl,
        AccessKey = MinioFixture.AccessKey,
        SecretKey = MinioFixture.SecretKey,
    };

    private AmazonS3Client Client() => new(
        new BasicAWSCredentials(MinioFixture.AccessKey, MinioFixture.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = minio.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });

}

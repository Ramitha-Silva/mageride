using System.Globalization;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Storage;

/// <summary>
/// D-36's bucket: S3-compatible object storage with server-side encryption, presigned reads and an
/// NFR-28 lifecycle rule the bucket enforces itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bytes are buffered to a temporary file, not to memory, and only then uploaded.</b> S3
/// needs a seekable stream to sign a payload, and the alternative — reading an unbounded multipart
/// part into a <c>MemoryStream</c> — is an 8 MiB ceiling that a client can make cost 8 MiB of heap
/// per concurrent upload. The temporary file is deleted in a <c>finally</c>, including on the
/// oversize path.
/// </para>
/// <para>
/// <b>The size ceiling is counted while streaming and never read from <c>Content-Length</c>.</b> The
/// rule the three uploading services already had, kept in one place: a ceiling enforced against a
/// length the client declared is not a ceiling.
/// </para>
/// </remarks>
internal sealed class S3ObjectStore : IObjectStore
{
    private readonly IAmazonS3 _s3;
    private readonly ObjectStoreOptions _options;
    private readonly ILogger<S3ObjectStore> _logger;
    private readonly string _bucket;
    private readonly bool _useHttp;

    public S3ObjectStore(
        IAmazonS3 s3, IOptions<ObjectStoreOptions> options, ObjectBucket bucket, ILogger<S3ObjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bucket = _options.BucketFor(bucket)!;
        _useHttp = _options.S3.Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public string Description =>
        $"S3 bucket '{_bucket}' at {_options.S3.Endpoint} "
        + (string.IsNullOrWhiteSpace(_options.KmsKeyId) ? "(SSE-S3)" : "(SSE-KMS)");

    public async Task<StoredObject> PutAsync(ObjectPutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = BuildKey(request);
        var scratch = Path.Combine(Path.GetTempPath(), $"mageride-upload-{Guid.CreateVersion7():N}");

        try
        {
            long length;
            byte[] hash;

            await using (var buffer = new FileStream(
                scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                (length, hash) = await CopyBoundedAsync(
                    request.Content, buffer, request.MaxBytes, cancellationToken);
            }

            await using (var body = File.OpenRead(scratch))
            {
                var put = new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = body,
                    ContentType = request.ContentType,
                    // Belt and braces on top of the bucket's default encryption: a bucket whose
                    // default was never applied still stores this object encrypted.
                    ServerSideEncryptionMethod = string.IsNullOrWhiteSpace(_options.KmsKeyId)
                        ? ServerSideEncryptionMethod.AES256
                        : ServerSideEncryptionMethod.AWSKMS,
                };

                if (!string.IsNullOrWhiteSpace(_options.KmsKeyId))
                {
                    put.ServerSideEncryptionKeyManagementServiceKeyId = _options.KmsKeyId;
                }

                await _s3.PutObjectAsync(put, cancellationToken);
            }

            return new StoredObject($"s3://{_bucket}/{key}", hash, length);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    public async Task<ObjectBytes?> ReadAsync(string storageUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageUrl);

        if (!TryParse(storageUrl, out var bucket, out var key))
        {
            // A pointer written before D-36 was wired, or by a service still on the filesystem.
            // Not this store's object and not an error — the composite store tries the other one.
            return null;
        }

        try
        {
            using var response = await _s3.GetObjectAsync(bucket, key, cancellationToken);
            using var memory = new MemoryStream();

            await response.ResponseStream.CopyToAsync(memory, cancellationToken);

            return new ObjectBytes(
                memory.ToArray(),
                string.IsNullOrWhiteSpace(response.Headers.ContentType)
                    ? "application/octet-stream"
                    : response.Headers.ContentType);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("No object {Key} in bucket {Bucket}.", key, bucket);

            return null;
        }
    }

    public bool TryPresign(string storageUrl, TimeSpan ttl, out string url)
    {
        url = string.Empty;

        if (!TryParse(storageUrl, out var bucket, out var key))
        {
            return false;
        }

        // GetPreSignedURL is a local signing operation — no round trip — so this stays synchronous
        // and cheap enough to call per rendition while rendering a queue detail.
        url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
            // Explicit, because the presigner defaults to HTTPS and does NOT take the scheme from
            // `ServiceURL` or from `AmazonS3Config.UseHttp`. Against MinIO in dev and on the
            // replica that produced an `https://` link to a server that speaks plain HTTP: every
            // bucket API call still worked, so it surfaced only as document thumbnails that failed
            // the TLS handshake in the officer's browser.
            Protocol = _useHttp ? Protocol.HTTP : Protocol.HTTPS,
        });

        return true;
    }

    /// <summary>
    /// Creates the bucket if it is absent and applies NFR-28's expiry to the ephemeral prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule is scoped to a prefix, and that is the whole point.</b> A bucket-wide expiry
    /// would delete a driver's LankaQR image 90 days after they uploaded it — the QR a passenger
    /// scans to pay them on every ride (AL-59) — and the platform would look fine until the day the
    /// first driver's payment rail stopped working.
    /// </para>
    /// <para>
    /// <b>A failure here is logged, not thrown.</b> Where the bucket is managed by Terraform the
    /// service's credentials may legitimately hold no <c>s3:PutLifecycleConfiguration</c>, and
    /// refusing to start would take a working deployment down over a rule that is already applied.
    /// What must not happen silently is the opposite — a bucket with no rule at all — so the log is
    /// an ERROR naming exactly what was not applied.
    /// </para>
    /// </remarks>
    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buckets = await _s3.ListBucketsAsync(cancellationToken);

            // `Buckets` is null rather than empty when there are none: AWSSDK v4 stopped
            // initialising response collections, so `.Any()` on it throws on a fresh MinIO.
            if (!(buckets.Buckets ?? []).Any(bucket =>
                    string.Equals(bucket.BucketName, _bucket, StringComparison.Ordinal)))
            {
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);

                _logger.LogInformation("Created object-storage bucket {Bucket} (D-36).", _bucket);
            }

            var days = Math.Max(1, (int)Math.Ceiling(_options.RawRetention.TotalDays));

            await _s3.PutLifecycleConfigurationAsync(
                new PutLifecycleConfigurationRequest
                {
                    BucketName = _bucket,
                    Configuration = new LifecycleConfiguration
                    {
                        Rules =
                        [
                            new LifecycleRule
                            {
                                Id = "mageride-nfr28-raw-documents",
                                Status = LifecycleRuleStatus.Enabled,
                                Filter = new LifecycleFilter
                                {
                                    LifecycleFilterPredicate = new LifecyclePrefixPredicate
                                    {
                                        Prefix = $"{ObjectRetentionClasses.Ephemeral}/",
                                    },
                                },
                                Expiration = new LifecycleRuleExpiration { Days = days },
                            },
                        ],
                    },
                },
                cancellationToken);

            _logger.LogInformation(
                "Object storage ready: {Description}. Raw documents under '{Prefix}/' expire after {Days} days "
                + "(NFR-28); '{Retained}/' is never expired because it holds objects the platform keeps serving, "
                + "such as a driver's own LankaQR (AL-59).",
                Description,
                ObjectRetentionClasses.Ephemeral,
                days,
                ObjectRetentionClasses.Retained);
        }
        catch (AmazonS3Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not prepare object-storage bucket {Bucket}. If the bucket exists and its NFR-28 lifecycle "
                + "rule is managed elsewhere this is harmless; if it is not, raw identity documents are being "
                + "kept with no expiry at all.",
                _bucket);
        }
    }

    /// <summary>
    /// <c>{ephemeral|retained}/{key}</c>. The retention class is in the key because that is what an
    /// S3 lifecycle rule can actually match on.
    /// </summary>
    private static string BuildKey(ObjectPutRequest request)
    {
        var prefix = request.Retention is null
            ? ObjectRetentionClasses.Retained
            : ObjectRetentionClasses.Ephemeral;

        var key = request.Key.Replace('\\', '/').TrimStart('/');

        if (key.Length == 0 || key.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                $"'{request.Key}' is not a usable object key.", nameof(request));
        }

        return $"{prefix}/{key}";
    }

    private static bool TryParse(string storageUrl, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;

        if (!storageUrl.StartsWith("s3://", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = storageUrl["s3://".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        bucket = rest[..slash];
        key = rest[(slash + 1)..];

        return true;
    }

    internal static async Task<(long Length, byte[] Sha256)> CopyBoundedAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        using var hasher = SHA256.Create();

        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;

            if (total > maxBytes)
            {
                throw new MageRideException(
                    MageRideErrors.PayloadTooLarge,
                    string.Create(
                        CultureInfo.InvariantCulture, $"The document is at most {maxBytes} bytes."));
            }

            hasher.TransformBlock(buffer, 0, read, null, 0);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        hasher.TransformFinalBlock([], 0, 0);

        if (total == 0)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["The document is empty."],
            });
        }

        return (total, hasher.Hash!);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A scratch file in the temp directory. The OS reclaims it; failing an upload that
            // already succeeded over a locked temporary would be worse.
        }
    }
}

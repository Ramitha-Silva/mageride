namespace MageRide.Shared.Storage;

/// <summary>
/// <c>Storage:*</c> — D-36's bucket, or the filesystem stand-in when there isn't one.
/// </summary>
/// <remarks>
/// Bound once in the kernel and shared by every service that touches bytes, because "where do
/// documents live" is one answer for the platform. Per-service roots (<c>Fleet:DocumentRoot</c>,
/// <c>Registry:PayoutDocumentRoot</c>, <c>Support:ScreenshotRoot</c>, <c>Ocr:Storage:Root</c>) are
/// still honoured as the filesystem fallback's root so an existing deployment keeps resolving the
/// rows it already wrote.
/// </remarks>
public sealed class ObjectStoreOptions
{
    public const string SectionName = "Storage";

    /// <summary>The endpoint and credentials, as <c>.env.common.example</c> already declared them.</summary>
    public S3Options S3 { get; set; } = new();

    /// <summary>Driver and fleet documents: licences, insurance, bank statements, LankaQR images.</summary>
    public string? DocumentsBucket { get; set; }

    /// <summary>US-16.2 support-ticket screenshots. The one bucket D7' §4.2 names.</summary>
    public string? ScreenshotBucket { get; set; }

    /// <summary>Proof-of-delivery photographs (package rides).</summary>
    public string? ProofsBucket { get; set; }

    /// <summary>Driver and passenger profile pictures.</summary>
    public string? ProfileBucket { get; set; }

    /// <summary>
    /// The KMS key raw documents are encrypted under (D-36's "SSE-KMS").
    /// </summary>
    /// <remarks>
    /// <b>Unset does not mean unencrypted — it means SSE-S3 (AES256) with the provider's own key.</b>
    /// The difference is real and worth naming: SSE-KMS gives a key you control, can rotate and can
    /// audit access to; SSE-S3 gives encryption at rest and nothing else. MinIO cannot do SSE-KMS
    /// without a KES server, so dev and the replica run on SSE-S3 and production sets this. The
    /// service says which one it got at start-up rather than letting a compliance claim rest on an
    /// unread default.
    /// </remarks>
    public string? KmsKeyId { get; set; }

    /// <summary>NFR-28: how long raw evidence lives before the bucket deletes it.</summary>
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Create the bucket and apply the NFR-28 lifecycle rule at start-up.
    /// </summary>
    /// <remarks>
    /// On, because a dev stack and the replica both start with an empty MinIO and a deployment that
    /// silently wrote to a bucket with no expiry rule would be one keeping identity documents for
    /// ever. Turn it off where the bucket is managed by Terraform and the service's credentials
    /// have no <c>s3:PutLifecycleConfiguration</c> — the failure is then logged, not fatal.
    /// </remarks>
    public bool EnsureBucket { get; set; } = true;

    /// <summary>How long a presigned document URL is good for (AL-39's "short-lived").</summary>
    public TimeSpan UrlTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Where the filesystem stand-in puts things when there is no <c>Storage:S3:Endpoint</c>.
    /// </summary>
    /// <remarks>
    /// Unset falls back to the calling service's own legacy root, and then to a temp directory —
    /// which a restart can take an officer's evidence with, and which is warned about.
    /// </remarks>
    public string? LocalRoot { get; set; }

    /// <summary>The bucket a given content class lives in, or null when none is configured.</summary>
    public string? BucketFor(ObjectBucket bucket) => bucket switch
    {
        ObjectBucket.Screenshots => ScreenshotBucket,
        ObjectBucket.Proofs => ProofsBucket,
        ObjectBucket.Profile => ProfileBucket,
        _ => DocumentsBucket,
    };

    public bool IsS3Configured(ObjectBucket bucket) =>
        !string.IsNullOrWhiteSpace(S3.Endpoint) && !string.IsNullOrWhiteSpace(BucketFor(bucket));

    /// <summary><c>Storage:S3:*</c>.</summary>
    public sealed class S3Options
    {
        /// <summary>
        /// The S3 endpoint. <b>Unset ⇒ the filesystem stand-in</b>, and D-36 is not in force.
        /// </summary>
        /// <remarks>
        /// MinIO in dev and on the replica (<c>http://minio:9000</c>), Cloudflare R2 / Wasabi / S3
        /// in production. Warned about at start-up when absent, because a deployment running on
        /// local disk looks exactly like one running on a bucket until a second replica is added
        /// and half the documents 404.
        /// </remarks>
        public string? Endpoint { get; set; }

        public string? AccessKey { get; set; }

        public string? SecretKey { get; set; }

        /// <summary><c>us-east-1</c> unless something says otherwise. R2 wants <c>auto</c>.</summary>
        public string Region { get; set; } = "us-east-1";

        /// <summary>
        /// Path-style addressing (<c>host/bucket/key</c>) rather than virtual-host style.
        /// </summary>
        /// <remarks>
        /// On by default because that is what MinIO serves out of the box and what a single-host
        /// dev endpoint can do at all — <c>bucket.minio:9000</c> does not resolve. Off for S3 proper.
        /// </remarks>
        public bool ForcePathStyle { get; set; } = true;
    }
}

/// <summary>
/// The four content classes <c>.env.common.example</c> gives their own buckets.
/// </summary>
/// <remarks>
/// Separate buckets rather than one with four prefixes because they have genuinely different
/// policies: a profile picture is not raw evidence and has no NFR-28 deadline, a proof-of-delivery
/// photograph belongs to a completed ride, and D7' §4.2 names <c>Storage__ScreenshotBucket</c> on
/// its own. Within a bucket the retention class is still a key prefix — see
/// <see cref="ObjectRetentionClasses"/> — because that is what one lifecycle rule can match.
/// </remarks>
public enum ObjectBucket
{
    Documents,
    Screenshots,
    Proofs,
    Profile,
}

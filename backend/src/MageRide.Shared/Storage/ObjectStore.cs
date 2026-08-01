namespace MageRide.Shared.Storage;

/// <summary>What an upload became: where it lives, what it hashes to, and how big it was.</summary>
/// <param name="StorageUrl">
/// The value that goes in <c>docs.uploads.storage_url</c> / <c>registry.documents.file_url</c>.
/// <c>s3://bucket/key</c> when the bytes are in the object store, <c>file://…</c> when they are on
/// a disk. <b>The scheme is load-bearing</b>: it is how a reader knows which store to ask, which is
/// what lets rows written before D-36 was wired go on resolving afterwards.
/// </param>
public sealed record StoredObject(string StorageUrl, byte[] Sha256, long Length);

/// <summary>The bytes of one stored object, and what they claim to be.</summary>
public sealed record ObjectBytes(ReadOnlyMemory<byte> Bytes, string ContentType);

/// <summary>
/// One upload, as the caller describes it before anything has been written.
/// </summary>
/// <param name="Key">
/// The object key <em>within</em> its retention class — no leading slash, no <c>..</c>. Callers
/// build it from ids they minted, never from a client-supplied filename.
/// </param>
/// <param name="Retention">
/// How long NFR-28 says these bytes may live, or <see langword="null"/> for "this object is not raw
/// evidence and must not be swept". <b>Null is not a default and choosing it is a decision</b> — see
/// <see cref="ObjectRetentionClasses"/>.
/// </param>
public sealed record ObjectPutRequest(
    string Key,
    Stream Content,
    string ContentType,
    long MaxBytes,
    TimeSpan? Retention);

/// <summary>
/// The two key prefixes, which are what makes NFR-28 enforceable by the bucket itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bucket-wide expiry rule would be wrong, and dangerously so.</b> NFR-28's 90 days is about
/// <em>raw identity documents</em> — a licence photograph, a bank statement — and deleting those on
/// a deadline is the point. But the same table holds objects that must outlive it: a driver's own
/// bank-app LankaQR (AL-59) is what a passenger scans to pay them on <b>every ride</b>, and a fleet
/// owner's is rendered on the Mode B pay sheet. Expiring those 90 days after upload would silently
/// break the payment rail for every driver who had not re-uploaded.
/// </para>
/// <para>
/// So the retention class is in the key, one S3 lifecycle rule is scoped to
/// <see cref="Ephemeral"/>, and an object under <see cref="Retained"/> is never touched by it. The
/// database's <c>docs.uploads.auto_delete_at</c> says the same thing in the same place it always
/// did; this makes the bucket agree rather than leaving the deadline to a sweeper nobody wrote.
/// </para>
/// </remarks>
public static class ObjectRetentionClasses
{
    /// <summary>Raw evidence. Deleted by the bucket's own lifecycle rule (NFR-28).</summary>
    public const string Ephemeral = "ephemeral";

    /// <summary>Objects the platform keeps serving. Never covered by the expiry rule.</summary>
    public const string Retained = "retained";
}

/// <summary>
/// D-36's document store: where a raw upload's bytes live, and how a browser is briefly let at them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One seam for five services.</b> registry-svc, fleet-svc and support-svc write bytes; ocr-svc
/// reads them; admin-bff hands a browser a short-lived link to them. Before this each had its own
/// filesystem stand-in and its own note saying an object-storage client did not exist yet — five
/// copies of one decision, which is how two of them start disagreeing about where a document is.
/// </para>
/// <para>
/// <b>Presigning is optional and the caller must cope.</b> A filesystem store cannot mint a URL that
/// means anything to a browser, so <see cref="TryPresign"/> answers <see langword="false"/> and
/// admin-bff falls back to the HMAC-signed pointer it used before. That is the difference between a
/// deployment where D-36 is wired and one where it is not — and it is visible rather than
/// pretended away.
/// </para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>A short name for what is behind this seam, for start-up logs and health checks.</summary>
    string Description { get; }

    /// <summary>
    /// Streams an upload in, counting as it goes. Throws <c>MageRideErrors.PayloadTooLarge</c> past
    /// <see cref="ObjectPutRequest.MaxBytes"/> and leaves nothing behind when it does.
    /// </summary>
    Task<StoredObject> PutAsync(ObjectPutRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The bytes behind a stored pointer, or <see langword="null"/> when they cannot be read.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, and every caller depends on it: a document that cannot be read
    /// makes an extraction fail, and an extraction that fails leaves the step saved and a
    /// Verification Officer to look at it. A throw here would turn a missing file into a 500 on a
    /// driver's onboarding.
    /// </remarks>
    Task<ObjectBytes?> ReadAsync(string storageUrl, CancellationToken cancellationToken);

    /// <summary>
    /// A short-lived URL a browser can follow straight to the object, if this store can mint one.
    /// </summary>
    bool TryPresign(string storageUrl, TimeSpan ttl, out string url);
}

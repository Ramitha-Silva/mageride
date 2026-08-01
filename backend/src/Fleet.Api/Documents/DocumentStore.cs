using MageRide.Fleet.Configuration;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Documents;

/// <summary>What the bytes of one payout document became.</summary>
public sealed record StoredDocument(string StorageUrl, byte[] Sha256, long Bytes);

/// <summary>
/// Where the bytes of a payout document go.
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ D-36: this is now the kernel's <see cref="IObjectStore"/>.</b> Server-side encrypted,
/// presignable and expired by the bucket's own lifecycle rule. Unconfigured it falls back to the
/// filesystem under <c>Fleet:DocumentRoot</c> and announces it, so a deployment that has not set
/// <c>Storage:*</c> behaves exactly as it did. The seam this interface was built to be is now
/// filled; it stays as the service's own vocabulary over it.
/// </para>
/// <para>
/// <b>A LankaQR is not raw evidence and is never expired.</b> AL-49 renders a fleet owner's own QR
/// on the Mode B pay sheet, so it is live payment infrastructure rather than a document somebody
/// checked once. Expiring it under NFR-28 with the bank statement would break that rail 90 days
/// after upload, silently.
/// </para>
/// </remarks>
public interface IDocumentStore
{
    Task<StoredDocument> WriteAsync(
        Guid uploadId, string kind, Stream content, CancellationToken cancellationToken);

    /// <summary>Whether NFR-28's deadline applies to this kind, or it must outlive it.</summary>
    TimeSpan? RetentionFor(string kind);
}

/// <inheritdoc cref="IDocumentStore"/>
internal sealed class ObjectDocumentStore(IObjectStore objects, IOptions<FleetOptions> options) : IDocumentStore
{
    /// <summary>
    /// AL-49's QR slot. Kept in step with <c>PayoutDocumentKinds</c>; the value is what the upload
    /// route already writes to <c>docs.uploads.kind</c>.
    /// </summary>
    private const string LankaQrKind = "lankaqr_code";

    private readonly FleetOptions _options = options.Value;

    public TimeSpan? RetentionFor(string kind) =>
        string.Equals(kind, LankaQrKind, StringComparison.Ordinal) ? null : _options.DocumentRetention;

    public async Task<StoredDocument> WriteAsync(
        Guid uploadId, string kind, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var stored = await objects.PutAsync(
            new ObjectPutRequest(
                $"fleet/documents/{uploadId:N}-{kind}",
                content,
                "application/octet-stream",
                _options.DocumentMaxBytes,
                RetentionFor(kind)),
            cancellationToken);

        return new StoredDocument(stored.StorageUrl, stored.Sha256, stored.Length);
    }
}

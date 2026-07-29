using System.Globalization;
using System.Security.Cryptography;
using MageRide.Ride.Configuration;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>Where a proof photo went, and what it hashes to.</summary>
public sealed record StoredProofPhoto(string StorageUrl, byte[] Sha256, long Bytes);

/// <summary>
/// Puts the P-10 delivery photo somewhere durable and returns the pointer
/// <c>rides.proof_artifacts.storage_url</c> keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface because the bytes do not belong here.</b> D-36 puts every uploaded image on
/// SSE-KMS object storage and Postgres holds a pointer (<c>docs.uploads</c> is the same shape). No
/// service in this build has an S3 client — the dev compose runs MinIO and nothing talks to it yet —
/// so the implementation below writes to a configured directory. That is a deployment concern, not a
/// domain one: the endpoint, the digest, the artifact row and the state change are the delivery
/// proof, and the object store is one method away. Raised in the C037 handoff.
/// </para>
/// <para>
/// <b>The digest is over the bytes as written</b>, not over what the client claimed. It is the
/// tamper evidence a COD dispute is settled with (P-14, §11.14), so it has to describe the file that
/// actually exists.
/// </para>
/// </remarks>
public interface IProofPhotoStore
{
    Task<StoredProofPhoto> SaveAsync(
        Guid rideId, Guid artifactId, string? fileName, Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// The filesystem implementation. One file per artifact under <c>Ride:ProofPhotoRoot</c>.
/// </summary>
/// <remarks>
/// A pod's filesystem is ephemeral, and this is said out loud at start-up rather than discovered
/// during a dispute six weeks later: with no object store configured the platform keeps the digest,
/// the artifact row and the state change — everything the receipt and the audit are built from — and
/// may lose the image itself on a restart.
/// </remarks>
public sealed class FileSystemProofPhotoStore : IProofPhotoStore
{
    /// <summary>What a delivery photo is named when the client sends no filename.</summary>
    private const string DefaultExtension = ".jpg";

    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".heic", ".webp"];

    private readonly string _root;

    public FileSystemProofPhotoStore(IOptions<RideOptions> options, ILogger<FileSystemProofPhotoStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _root = string.IsNullOrWhiteSpace(options.Value.ProofPhotoRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride", "proof-artifacts")
            : options.Value.ProofPhotoRoot;

        Directory.CreateDirectory(_root);

        logger.LogInformation(
            "Delivery proof photos (P-10) are written to {Root}. This is not object storage: D-36 puts them on " +
            "SSE-KMS buckets, so a pod restart can lose the image while rides.proof_artifacts keeps its digest.",
            _root);
    }

    public async Task<StoredProofPhoto> SaveAsync(
        Guid rideId, Guid artifactId, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var extension = ResolveExtension(fileName);

        // Foldered by ride, so an operator handed a ride id can find every artifact of it without
        // an index, and named by the artifact id, which is what the 201 returns.
        var directory = Path.Combine(_root, rideId.ToString("D"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, artifactId.ToString("D") + extension);

        byte[] digest;
        long written;

        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            // Hashed on the way through rather than by re-reading the file: one pass over an
            // upload, and the digest describes the same bytes the write produced even if something
            // replaces the file afterwards.
            using var hasher = SHA256.Create();
            await using var hashing = new CryptoStream(file, hasher, CryptoStreamMode.Write, leaveOpen: true);

            await content.CopyToAsync(hashing, cancellationToken);
            await hashing.FlushFinalBlockAsync(cancellationToken);

            digest = hasher.Hash!;
            written = file.Length;
        }

        var url = new UriBuilder("file", string.Empty) { Path = path }.Uri.ToString();

        return new StoredProofPhoto(url, digest, written);
    }

    /// <summary>
    /// The extension, from a closed list. Anything else — including a filename with a path in it —
    /// becomes the default, so a client cannot choose where the file lands or what it is called.
    /// </summary>
    private static string ResolveExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DefaultExtension;
        }

        var extension = Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();

        return AllowedExtensions.Contains(extension, StringComparer.Ordinal) ? extension : DefaultExtension;
    }
}

/// <summary>Guards on an upload that are the endpoint's rather than the store's.</summary>
internal static class ProofPhotoUpload
{
    /// <summary>
    /// Refuses an upload larger than the configured ceiling — <c>413 payload-too-large</c>, which
    /// <c>uploadPackageProofPhoto</c> declares.
    /// </summary>
    public static void RequireWithinLimit(long? length, long limitBytes)
    {
        if (length is { } bytes && bytes > limitBytes)
        {
            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The photo is {bytes} bytes; the limit is {limitBytes}."));
        }
    }
}

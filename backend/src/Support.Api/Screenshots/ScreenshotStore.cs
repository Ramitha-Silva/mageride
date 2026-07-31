using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;
using MageRide.Support.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Support.Screenshots;

/// <summary>The <c>docs.uploads.kind</c> this service writes, and the only one it serves.</summary>
public static class SupportUploadKinds
{
    /// <summary>US-16.2's attachment. Named in migration 1309's comment on <c>docs.uploads.kind</c>.</summary>
    public const string Screenshot = "support_screenshot";
}

/// <summary>Where a screenshot went, and what it hashes to.</summary>
public sealed record StoredScreenshot(string StorageUrl, byte[] Sha256, long Bytes);

/// <summary>
/// Puts a US-16.2 screenshot somewhere durable and returns the pointer <c>docs.uploads.storage_url</c>
/// keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface because the bytes do not belong here</b> — the same seam ride-svc's
/// <c>IProofPhotoStore</c> and subscription-svc's <c>ITransferSlipStore</c> open, and for the same
/// reason: D-36 puts every uploaded image on SSE-KMS object storage with Postgres holding a pointer,
/// D7' §4.2 names <c>Storage__ScreenshotBucket</c> for this service, and no service in this build has
/// an S3 client (C125). The implementation below writes to a configured directory, which is a
/// deployment concern rather than a domain one.
/// </para>
/// <para>
/// <b>The digest is over the bytes as written</b>, not over what the client claimed, because a
/// screenshot is evidence in a dispute and the hash has to describe the file that actually exists.
/// </para>
/// </remarks>
public interface IScreenshotStore
{
    Task<StoredScreenshot> SaveAsync(
        Guid uploadId, string? fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stored screenshot for serving, or <see langword="null"/> when it is somewhere this
    /// process cannot read — an object-store URL, which the route redirects to instead.
    /// </summary>
    (Stream Content, string ContentType)? Open(string storageUrl);
}

/// <summary>The filesystem implementation. One file per upload under <c>Support:ScreenshotRoot</c>.</summary>
/// <remarks>
/// A pod's filesystem is ephemeral, and this is said out loud at start-up rather than discovered
/// during a dispute: with no object store configured the platform keeps the ticket, its thread and
/// the digest — everything the complaint and its answer are built from — and may lose the image on a
/// restart.
/// </remarks>
public sealed class FileSystemScreenshotStore : IScreenshotStore
{
    private const string DefaultExtension = ".jpg";

    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".heic", ".webp"];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".heic"] = "image/heic",
        [".webp"] = "image/webp",
    };

    private readonly string _root;

    public FileSystemScreenshotStore(
        IOptions<SupportOptions> options, ILogger<FileSystemScreenshotStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _root = string.IsNullOrWhiteSpace(options.Value.ScreenshotRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride", "support-screenshots")
            : options.Value.ScreenshotRoot;

        Directory.CreateDirectory(_root);

        logger.LogInformation(
            "Support screenshots (US-16.2) are written to {Root}. This is not object storage: D-36 puts them on "
            + "SSE-KMS buckets (D7' §4.2 Storage__ScreenshotBucket), so a pod restart can lose the image while the "
            + "ticket and its thread survive.",
            _root);
    }

    public async Task<StoredScreenshot> SaveAsync(
        Guid uploadId, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = Path.Combine(_root, uploadId.ToString("D") + ResolveExtension(fileName));

        byte[] digest;
        long written;

        await using (var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            // Hashed on the way through rather than by re-reading the file: one pass over the
            // upload, and the digest describes the same bytes the write produced even if something
            // replaces the file afterwards.
            using var hasher = SHA256.Create();
            await using var hashing = new CryptoStream(file, hasher, CryptoStreamMode.Write, leaveOpen: true);

            await content.CopyToAsync(hashing, cancellationToken);
            await hashing.FlushFinalBlockAsync(cancellationToken);

            digest = hasher.Hash!;
            written = file.Length;
        }

        return new StoredScreenshot(
            new UriBuilder("file", string.Empty) { Path = path }.Uri.ToString(), digest, written);
    }

    public (Stream Content, string ContentType)? Open(string storageUrl)
    {
        if (!Uri.TryCreate(storageUrl, UriKind.Absolute, out var uri) || !uri.IsFile || !File.Exists(uri.LocalPath))
        {
            return null;
        }

        var extension = Path.GetExtension(uri.LocalPath).ToLowerInvariant();
        var contentType = ContentTypes.TryGetValue(extension, out var known) ? known : "application/octet-stream";

        return (File.OpenRead(uri.LocalPath), contentType);
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

/// <summary>
/// Signs and verifies the expiring URL <c>TicketDetail.screenshotUrl</c> carries.
/// </summary>
/// <remarks>
/// <para>
/// The contract calls it "a short-lived signed object-storage URL", which in a deployment with a
/// bucket is a pre-signed S3 link. There is none in front of this service, so the service serves the
/// bytes and the link carries an HMAC instead. The properties the contract wanted are the ones that
/// matter: the link is unguessable, it expires, and following it needs no bearer token — which is
/// what lets a ticket detail put it straight into an image view.
/// </para>
/// <para>
/// <b>A bearer would be the wrong credential here even if it were convenient.</b> An image loader
/// does not carry one, and putting an access token in a query string puts it in every proxy log the
/// request passes through.
/// </para>
/// </remarks>
public interface IScreenshotLinks
{
    /// <summary>The relative, signed URL for one upload.</summary>
    string Create(Guid uploadId);

    /// <summary>Whether a presented signature is one this service issued and has not expired.</summary>
    bool Verify(Guid uploadId, string? expires, string? signature);
}

/// <inheritdoc cref="IScreenshotLinks"/>
public sealed class ScreenshotLinks : IScreenshotLinks
{
    private readonly SupportOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public ScreenshotLinks(IOptions<SupportOptions> options, TimeProvider clock, ILogger<ScreenshotLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.FileLinkSigningKey))
        {
            // A generated key is correct for one instance and wrong for several: a link minted by
            // replica A does not verify on replica B, and the user sees a broken image on a ticket
            // the server rendered a second ago.
            _key = RandomNumberGenerator.GetBytes(32);

            logger.LogWarning(
                "Support:FileLinkSigningKey is not configured, so TicketDetail.screenshotUrl is signed with a key "
                + "generated for this process. Those links will not verify on another replica or survive a restart.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(_options.FileLinkSigningKey);
        }
    }

    public string Create(Guid uploadId)
    {
        var expires = (_clock.GetUtcNow() + _options.FileLinkTtl).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        return $"/v1/support/screenshots/{uploadId}?expires={expires}&signature={Sign(uploadId, expires)}";
    }

    public bool Verify(Guid uploadId, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(expires) || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix)
            || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= _clock.GetUtcNow())
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            presented, Convert.FromHexString(Sign(uploadId, expires)));
    }

    private string Sign(Guid uploadId, string expires) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{SupportUploadKinds.Screenshot}|{uploadId}|{expires}")));
}

/// <summary>Guards on an upload that are the endpoint's rather than the store's.</summary>
internal static class ScreenshotUpload
{
    /// <summary>
    /// Refuses an upload larger than the configured ceiling — <c>413 payload-too-large</c>, which
    /// <c>uploadSupportScreenshot</c> declares.
    /// </summary>
    public static void RequireWithinLimit(long? length, long limitBytes)
    {
        if (length is { } bytes && bytes > limitBytes)
        {
            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The screenshot is {bytes} bytes; the limit is {limitBytes}."));
        }
    }
}

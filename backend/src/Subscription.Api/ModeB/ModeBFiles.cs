using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MageRide.Shared.Errors;
using MageRide.Subscriptions.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.ModeB;

/// <summary>The two kinds of image the pay sheet and the owner's queue render.</summary>
public static class ModeBFileKinds
{
    /// <summary>The owner's bank-app LankaQR code, from the verified payout profile (AL-49).</summary>
    public const string LankaQr = "lankaqr";

    /// <summary>The passenger's online-transfer screenshot (US-23.4).</summary>
    public const string Slip = "slips";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { LankaQr, Slip };
}

/// <summary>
/// Signs and verifies the expiring URLs <c>payTo.lankaqrImageUrl</c> and
/// <c>SubscriptionPayment.slipUrl</c> carry.
/// </summary>
/// <remarks>
/// <para>
/// D3' calls both "a signed URL", which in a deployment with object storage is a pre-signed S3 link.
/// There is no bucket in front of this service — D-36's is C125's — so the service serves the bytes
/// and the link carries an HMAC instead. The properties D3' wanted are the ones that matter: the link
/// is unguessable, it expires, and following it needs no bearer token, which is what lets the pay
/// sheet put it straight into an image view.
/// </para>
/// <para>
/// <b>The kind is inside the signature.</b> Without it a link to a passenger's transfer slip could be
/// re-pointed at a payout profile — or the reverse — by editing one path segment, and both are
/// somebody's private document.
/// </para>
/// <para>
/// <b>A bearer token would be the wrong credential here even if it were convenient.</b> These URLs
/// are handed to an image loader that does not carry one, and putting the access token in a query
/// string is putting it in every proxy log the request passes through.
/// </para>
/// </remarks>
public interface IModeBFileLinks
{
    /// <summary>The relative, signed URL for one document.</summary>
    string Create(string kind, Guid id);

    /// <summary>Whether a presented signature is one this service issued and has not expired.</summary>
    bool Verify(string kind, Guid id, string? expires, string? signature);
}

/// <inheritdoc cref="IModeBFileLinks"/>
public sealed class ModeBFileLinks : IModeBFileLinks
{
    private readonly SubscriptionOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    public ModeBFileLinks(
        IOptions<SubscriptionOptions> options, TimeProvider clock, ILogger<ModeBFileLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _clock = clock ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.FileLinkSigningKey))
        {
            // A generated key is correct for one instance and wrong for several: a link minted by
            // replica A does not verify on replica B, and the passenger sees a broken QR image on a
            // pay sheet the server produced a second ago.
            _key = RandomNumberGenerator.GetBytes(32);

            logger.LogWarning(
                "Subscription:FileLinkSigningKey is not configured, so the signed URLs on payTo.lankaqrImageUrl "
                + "and SubscriptionPayment.slipUrl are signed with a key generated for this process. They will "
                + "not verify on another replica or survive a restart.");
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(_options.FileLinkSigningKey);
        }
    }

    public string Create(string kind, Guid id)
    {
        RequireKnownKind(kind);

        var expires = (_clock.GetUtcNow() + _options.FileLinkTtl).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        return $"/v1/mode-b/files/{kind}/{id}?expires={expires}&signature={Sign(kind, id, expires)}";
    }

    public bool Verify(string kind, Guid id, string? expires, string? signature)
    {
        if (!ModeBFileKinds.All.Contains(kind)
            || string.IsNullOrWhiteSpace(expires)
            || string.IsNullOrWhiteSpace(signature))
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

        return CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(Sign(kind, id, expires)));
    }

    private static void RequireKnownKind(string kind)
    {
        if (!ModeBFileKinds.All.Contains(kind))
        {
            throw new ArgumentException($"'{kind}' is not a Mode B file kind.", nameof(kind));
        }
    }

    private string Sign(string kind, Guid id, string expires) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{kind}|{id}|{expires}")));
}

/// <summary>Where a transfer slip went.</summary>
public sealed record StoredSlip(string StorageUrl, long Bytes);

/// <summary>
/// Puts the US-23.4 transfer screenshot somewhere durable and returns the pointer
/// <c>subscription.payments.slip_url</c> keeps.
/// </summary>
/// <remarks>
/// <b>An interface because the bytes do not belong here</b> — the same seam ride-svc's
/// <c>IProofPhotoStore</c> opens, and for the same reason: D-36 puts every uploaded image on SSE-KMS
/// object storage with Postgres holding a pointer, and no service in this build has an S3 client yet.
/// The implementation below writes to a configured directory, which is a deployment concern rather
/// than a domain one.
/// </remarks>
public interface ITransferSlipStore
{
    Task<StoredSlip> SaveAsync(Guid paymentId, string? fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stored document for serving, or <see langword="null"/> when it is somewhere this
    /// process cannot read — an object-store URL, which the route redirects to instead.
    /// </summary>
    (Stream Content, string ContentType)? Open(string storageUrl);
}

/// <summary>The filesystem implementation. One file per payment under <c>Subscription:SlipRoot</c>.</summary>
/// <remarks>
/// A pod's filesystem is ephemeral, and this is said out loud at start-up rather than discovered
/// during a dispute: with no object store configured the platform keeps the payment row, its status
/// and who confirmed it — everything the ledger view is built from — and may lose the screenshot on a
/// restart.
/// </remarks>
public sealed class FileSystemTransferSlipStore : ITransferSlipStore
{
    private const string DefaultExtension = ".jpg";

    private static readonly string[] AllowedExtensions = [".jpg", ".jpeg", ".png", ".heic", ".webp", ".pdf"];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".heic"] = "image/heic",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
    };

    private readonly string _root;

    public FileSystemTransferSlipStore(
        IOptions<SubscriptionOptions> options, ILogger<FileSystemTransferSlipStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _root = string.IsNullOrWhiteSpace(options.Value.SlipRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride", "subscription-slips")
            : options.Value.SlipRoot;

        Directory.CreateDirectory(_root);

        logger.LogInformation(
            "Mode B transfer slips (US-23.4) are written to {Root}. This is not object storage: D-36 puts them "
            + "on SSE-KMS buckets, so a pod restart can lose the screenshot while subscription.payments keeps "
            + "the row the owner confirmed.",
            _root);
    }

    public async Task<StoredSlip> SaveAsync(
        Guid paymentId, string? fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var extension = ResolveExtension(fileName);
        var path = Path.Combine(_root, paymentId.ToString("D") + extension);

        long written;

        await using (var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await content.CopyToAsync(file, cancellationToken);
            written = file.Length;
        }

        return new StoredSlip(new UriBuilder("file", string.Empty) { Path = path }.Uri.ToString(), written);
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

/// <summary>Guards on an upload that are the endpoint's rather than the store's.</summary>
internal static class TransferSlipUpload
{
    /// <summary>
    /// Refuses an upload larger than the configured ceiling — <c>413 payload-too-large</c>, which
    /// <c>uploadTransferSlip</c> declares.
    /// </summary>
    public static void RequireWithinLimit(long? length, long limitBytes)
    {
        if (length is { } bytes && bytes > limitBytes)
        {
            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                string.Create(CultureInfo.InvariantCulture, $"The slip is {bytes} bytes; the limit is {limitBytes}."));
        }
    }
}

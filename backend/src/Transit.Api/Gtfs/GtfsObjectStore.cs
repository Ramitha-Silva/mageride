using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using MageRide.Shared.Errors;
using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Transit.Gtfs;

/// <summary>Where an uploaded zip went, and what it hashes to.</summary>
public sealed record StoredFeedZip(string StorageKey, string Sha256, long Bytes);

/// <summary>
/// Keeps the original GTFS zips (BR-32.3, ≥ 12 months).
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface because the bytes do not belong here.</b> D-36 and BR-32.3 put them on SSE
/// object storage with a signed-URL download; no service in this build has an S3 client — the dev
/// compose runs MinIO and nothing talks to it — so the implementation below writes to a
/// configured directory. Same interim as ride-svc's <c>IProofPhotoStore</c> (C037), for the same
/// reason: the version row, the digest, the report and the swap are the lifecycle, and the bucket
/// is one method away.
/// </para>
/// <para>
/// <b>Nothing here deletes.</b> BR-32.3's retention floor is met by never removing a stored zip:
/// rollback is a re-import from the archived version's <c>storage_key</c>, so a zip that has been
/// collected is a version that can no longer be rolled back to. Expiring them past 12 months is a
/// bucket lifecycle policy (D7'), not a code path — a service that could delete a feed is a
/// service that can lose one.
/// </para>
/// </remarks>
public interface IGtfsObjectStore
{
    /// <summary>
    /// Streams an upload to storage, hashing as it goes.
    /// </summary>
    /// <remarks>
    /// The digest is over the bytes <b>as written</b>, never over what the client claimed, because
    /// it is the BR-32.1 dedupe key and a `409` has to describe the file that actually exists.
    /// </remarks>
    /// <exception cref="MageRideException">
    /// <c>payload-too-large</c> once <paramref name="maxBytes"/> is exceeded. The partial object is
    /// removed before the throw.
    /// </exception>
    Task<StoredFeedZip> PutAsync(Guid feedVersionId, Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Opens a stored zip for reading, or null when the object is gone.</summary>
    Task<Stream?> OpenAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Removes an object. Used only to undo a write the request then refused.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}

/// <summary>The filesystem implementation: one file per feed version under <c>Transit:Gtfs:StorageRoot</c>.</summary>
/// <remarks>
/// A pod's filesystem is ephemeral, and this is said out loud at start-up rather than discovered
/// during a rollback six weeks later: with no object store configured, the platform keeps every
/// version row, its report and its counts — everything SCR-AP-016 renders — and may lose the zip
/// that makes the version re-activatable.
/// </remarks>
public sealed class FileSystemGtfsObjectStore : IGtfsObjectStore
{
    private const int BufferSize = 128 * 1024;

    private readonly string _root;

    public FileSystemGtfsObjectStore(IOptions<TransitOptions> options, ILogger<FileSystemGtfsObjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _root = string.IsNullOrWhiteSpace(options.Value.Gtfs.StorageRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride", "gtfs-feeds")
            : options.Value.Gtfs.StorageRoot;

        Directory.CreateDirectory(_root);

        logger.LogInformation(
            "GTFS feed archives are written to {Root}. This is not object storage: BR-32.3 keeps the original zips "
            + "on an SSE bucket for at least 12 months, so on this deployment a restart can lose the file a rollback "
            + "would re-import from.",
            _root);
    }

    public async Task<StoredFeedZip> PutAsync(
        Guid feedVersionId, Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var key = StorageKey(feedVersionId);
        var path = Resolve(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] digest;
        long written = 0;

        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                using var hasher = SHA256.Create();
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

                try
                {
                    int read;

                    while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        written += read;

                        // Checked as it streams, not from Content-Length: a chunked upload
                        // declares no length, and a declared one is the client's claim about a
                        // body it is still sending.
                        if (written > maxBytes)
                        {
                            throw new MageRideException(
                                MageRideErrors.PayloadTooLarge,
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"The upload is larger than the {maxBytes}-byte limit (BR-32.1: 200 MB)."));
                        }

                        hasher.TransformBlock(buffer, 0, read, null, 0);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                hasher.TransformFinalBlock([], 0, 0);
                digest = hasher.Hash!;
            }
        }
        catch
        {
            await DeleteAsync(key, CancellationToken.None);
            throw;
        }

        return new StoredFeedZip(key, Convert.ToHexStringLower(digest), written);
    }

    public Task<Stream?> OpenAsync(string storageKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var path = Resolve(storageKey);

        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true)
            : null;

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var path = Resolve(storageKey);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>The key stored in <c>transit.gtfs_feed_versions.storage_key</c>.</summary>
    /// <remarks>
    /// Named by the feed version id rather than by the uploaded filename: two operators uploading
    /// <c>gtfs.zip</c> a month apart are two versions, and a client must not be able to choose
    /// where its bytes land or what they are called.
    /// </remarks>
    private static string StorageKey(Guid feedVersionId) => $"gtfs/{feedVersionId:D}.zip";

    /// <summary>
    /// Resolves a key under the root, refusing anything that would escape it.
    /// </summary>
    /// <remarks>
    /// The keys this service writes are generated, but the ones it <em>reads</em> come out of a
    /// database column, and a column is not a trust boundary — a stored <c>../../etc/passwd</c>
    /// would otherwise be served to an admin over the download route.
    /// </remarks>
    private string Resolve(string storageKey)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storageKey));
        var root = Path.GetFullPath(_root);

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new MageRideException(
                MageRideErrors.InternalError, "The stored object key does not resolve inside the feed archive root.");
        }

        return full;
    }
}

/// <summary>
/// Mints and checks the short-lived signed download links the 302 points at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The signature is the credential.</b> `GET …/versions/{id}/download` answers a redirect and a
/// browser follows it without the bearer token that authorised the redirect, which is exactly why
/// object storage uses presigned URLs in the first place. The signed route is therefore anonymous
/// and the HMAC is what authorises it — scoped to one feed version and one expiry, so a link
/// pasted into a ticket stops working.
/// </para>
/// <para>
/// The key is required outside Development. In Development an unset key mints a per-process one:
/// links then stop working across a restart, which is a visible failure rather than a service
/// quietly serving feeds to anyone who guesses a URL.
/// </para>
/// </remarks>
public sealed class GtfsDownloadLinks
{
    private readonly byte[] _key;
    private readonly TimeSpan _ttl;
    private readonly string? _publicBaseUrl;
    private readonly TimeProvider _clock;

    public GtfsDownloadLinks(
        IOptions<TransitOptions> options,
        TimeProvider clock,
        IHostEnvironment environment,
        ILogger<GtfsDownloadLinks> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value.Gtfs;

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ttl = settings.DownloadUrlTtl;
        _publicBaseUrl = string.IsNullOrWhiteSpace(settings.PublicBaseUrl) ? null : settings.PublicBaseUrl.TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(settings.DownloadSigningKey))
        {
            _key = Decode(settings.DownloadSigningKey);
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Transit:Gtfs:DownloadSigningKey is required outside Development. It is the only credential on "
                + "GET /v1/admin/transit/gtfs/objects/{feedVersionId}, which serves the original feed zip.");
        }

        _key = RandomNumberGenerator.GetBytes(32);

        logger.LogWarning(
            "Transit:Gtfs:DownloadSigningKey is unset; a per-process key was generated. GTFS download links will "
            + "stop working on restart and will not verify across replicas. Development only.");
    }

    /// <summary>Query parameter carrying the link's expiry, as Unix seconds.</summary>
    public const string ExpiryParameter = "exp";

    /// <summary>Query parameter carrying the signature.</summary>
    public const string SignatureParameter = "sig";

    /// <summary>The absolute URL the download route redirects to.</summary>
    public string Create(Guid feedVersionId, string requestScheme, string requestHost)
    {
        var expiry = _clock.GetUtcNow().Add(_ttl).ToUnixTimeSeconds();
        var origin = _publicBaseUrl ?? $"{requestScheme}://{requestHost}";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{origin}/v1/admin/transit/gtfs/objects/{feedVersionId:D}?{ExpiryParameter}={expiry}&{SignatureParameter}={Sign(feedVersionId, expiry)}");
    }

    /// <summary>Whether a presented signature is this service's, for this version, and unexpired.</summary>
    public bool IsValid(Guid feedVersionId, string? expiry, string? signature)
    {
        if (!long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt) ||
            string.IsNullOrEmpty(signature))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresAt) <= _clock.GetUtcNow())
        {
            return false;
        }

        var expected = System.Text.Encoding.ASCII.GetBytes(Sign(feedVersionId, expiresAt));
        var presented = System.Text.Encoding.ASCII.GetBytes(signature);

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    private string Sign(Guid feedVersionId, long expiry)
    {
        // Newline-separated, so no value can be shifted into the next field: a version id is
        // fixed-width, but signing a concatenation is how a scheme stops being one.
        var payload = System.Text.Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{feedVersionId:D}\n{expiry}"));

        return Base64Url.EncodeToString(HMACSHA256.HashData(_key, payload));
    }

    private static byte[] Decode(string key)
    {
        try
        {
            return Convert.FromBase64String(key);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Transit:Gtfs:DownloadSigningKey must be base64.", exception);
        }
    }
}

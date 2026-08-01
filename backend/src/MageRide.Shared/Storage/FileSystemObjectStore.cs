using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Shared.Storage;

/// <summary>
/// The filesystem stand-in for D-36's bucket, and the reader for everything written before it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is still here on purpose, and it is not only a fallback.</b> Every <c>docs.uploads</c> row
/// written before the bucket existed holds a <c>file://</c> pointer, and those documents are the
/// ones a Verification Officer is looking at today. A deployment that switched to S3 and could no
/// longer read them would lose the evidence behind every pending application on the platform, so
/// the composite store keeps this one for reads whatever the configuration says.
/// </para>
/// <para>
/// <b>A pointer is a value from a row this service does not own.</b> It is resolved under a root and
/// one that escapes it is refused — <c>../../etc/passwd</c> is a <c>docs.uploads</c> insert away
/// from being read and posted to an external model. ocr-svc's own store made this argument first;
/// it is kept verbatim because the threat did not change.
/// </para>
/// </remarks>
internal sealed class FileSystemObjectStore : IObjectStore
{
    private readonly ObjectStoreOptions _options;
    private readonly ILogger<FileSystemObjectStore> _logger;
    private readonly string _root;

    public FileSystemObjectStore(
        IOptions<ObjectStoreOptions> options, ILogger<FileSystemObjectStore> logger, string? legacyRoot = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _root = FirstConfigured(_options.LocalRoot, legacyRoot)
                ?? Path.Combine(Path.GetTempPath(), "mageride-documents");

        Directory.CreateDirectory(_root);
    }

    public string Description => $"local filesystem at {_root}";

    public async Task<StoredObject> PutAsync(ObjectPutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var relative = Sanitise(request);
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long length;
        byte[] hash;

        try
        {
            await using var file = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

            (length, hash) = await S3ObjectStore.CopyBoundedAsync(
                request.Content, file, request.MaxBytes, cancellationToken);
        }
        catch
        {
            // The partial file goes with the refusal: bytes on disk for a request that was refused
            // have no row, no deadline and nobody who knows they are there.
            TryDelete(path);
            throw;
        }

        return new StoredObject(new Uri(Path.GetFullPath(path)).AbsoluteUri, hash, length);
    }

    public async Task<ObjectBytes?> ReadAsync(string storageUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageUrl);

        if (storageUrl.StartsWith("s3://", StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryResolve(storageUrl, out var path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            _logger.LogWarning("No document at {Path}.", path);

            return null;
        }

        return new ObjectBytes(
            await File.ReadAllBytesAsync(path, cancellationToken), ContentTypeOf(path));
    }

    /// <summary>
    /// Always false. A filesystem path is not something a browser can be redirected to, and
    /// pretending otherwise would hand an officer a link that silently 404s.
    /// </summary>
    public bool TryPresign(string storageUrl, TimeSpan ttl, out string url)
    {
        url = string.Empty;

        return false;
    }

    /// <summary>Refuses anything that would leave the root, and keeps the retention class in the path.</summary>
    private static string Sanitise(ObjectPutRequest request)
    {
        var prefix = request.Retention is null
            ? ObjectRetentionClasses.Retained
            : ObjectRetentionClasses.Ephemeral;

        var key = request.Key.Replace('\\', '/').TrimStart('/');

        if (key.Length == 0 || key.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"'{request.Key}' is not a usable object key.", nameof(request));
        }

        return $"{prefix}/{key}";
    }

    private bool TryResolve(string storageUrl, out string path)
    {
        path = string.Empty;

        var candidate = storageUrl;

        if (Uri.TryCreate(storageUrl, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                // Not this store's, and deliberately not fetched: an arbitrary URL out of a table
                // is an SSRF primitive, and the one service that ever wanted this (ocr-svc) gates
                // it behind its own explicit switch.
                return false;
            }

            if (uri.IsFile)
            {
                candidate = uri.LocalPath;
            }
        }

        var full = Path.GetFullPath(
            Path.IsPathRooted(candidate) ? candidate : Path.Combine(_root, candidate.TrimStart('/')));

        var root = Path.GetFullPath(_root);

        // An absolute path from an older row may legitimately sit outside the configured root — the
        // root moved, or the row was written by another service. It is allowed only when it is
        // *rooted*, never when it is a relative pointer that climbed out with `..`.
        if (!Path.IsPathRooted(candidate)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, root, StringComparison.Ordinal))
        {
            _logger.LogError(
                "A stored document pointer resolved outside {Root} and was refused. This is a value from "
                + "docs.uploads, not from a request, so it means something wrote a traversal into the table.",
                root);

            return false;
        }

        path = full;

        return true;
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };

    private static string? FirstConfigured(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

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
            // The caller is already being told why its upload failed; a locked partial file is the
            // sweeper's problem rather than this request's.
        }
    }
}

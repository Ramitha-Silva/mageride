using MageRide.Ocr.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Storage;

/// <summary>The bytes of one raw upload, and what they are.</summary>
public sealed record RawDocument(ReadOnlyMemory<byte> Bytes, string ContentType);

/// <summary>
/// Reads a raw document out of object storage (D-36's SSE-KMS bucket).
/// </summary>
/// <remarks>
/// <para>
/// <b>One method, because the implementation behind it is a deployment concern.</b> D-36 puts raw
/// documents on an SSE-KMS bucket with signed-URL access and a 90-day deletion (NFR-28); no service
/// in this build has an S3 client (C125), and support-svc's screenshot store is the same seam with
/// the same note. What ocr-svc owes regardless of where the bytes sit is that they are read
/// <em>here</em> — registry-svc resolves the id and never touches the file, which is what keeps an
/// unredacted image on this side of the perimeter — and that the row carries a deletion deadline.
/// </para>
/// <para>
/// <b>Nothing here throws for a document it cannot read.</b> A missing file, an oversized one and a
/// path that escapes the root all answer <see langword="null"/>: the extraction then fails, the
/// onboarding step still saves, and a Verification Officer takes it.
/// </para>
/// </remarks>
public interface IRawDocumentStore
{
    Task<RawDocument?> ReadAsync(string storageUrl, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// The filesystem stand-in for the bucket. A <c>storage_url</c> is treated as a path relative to
/// <see cref="OcrOptions.StorageOptions.Root"/>, and one that resolves outside it is refused —
/// the value comes from a row this service does not own, and <c>../../etc/passwd</c> is a
/// <c>docs.uploads</c> insert away from being read and posted to an external model.
/// </remarks>
public sealed class FileSystemRawDocumentStore : IRawDocumentStore
{
    private readonly OcrOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<FileSystemRawDocumentStore> _logger;

    /// <summary>The named client used only when <c>AllowHttpSources</c> is on.</summary>
    public const string HttpClientName = "document-storage";

    public FileSystemRawDocumentStore(
        IOptions<OcrOptions> options, IHttpClientFactory clients, ILogger<FileSystemRawDocumentStore> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RawDocument?> ReadAsync(string storageUrl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageUrl);

        if (Uri.TryCreate(storageUrl, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            return await ReadOverHttpAsync(absolute, cancellationToken);
        }

        return await ReadFromDiskAsync(storageUrl, cancellationToken);
    }

    private async Task<RawDocument?> ReadFromDiskAsync(string storageUrl, CancellationToken cancellationToken)
    {
        var root = _options.Storage.Root;

        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogError(
                "Ocr:Storage:Root is not configured, so no document can be read and nothing can be extracted "
                + "(D-36's bucket, until C125 lands an object-storage client).");

            return null;
        }

        var fullRoot = Path.GetFullPath(root);
        var relative = storageUrl.TrimStart('/');

        // file:// paths are written by some producers; take the path and treat it the same way.
        if (Uri.TryCreate(storageUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            relative = uri.LocalPath.TrimStart('/');
        }

        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));

        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(path, fullRoot, StringComparison.Ordinal))
        {
            _logger.LogError(
                "A docs.uploads.storage_url resolved outside Ocr:Storage:Root and was refused. "
                + "storage_url is written by another service and is not a path this one will follow anywhere.");

            return null;
        }

        var file = new FileInfo(path);

        if (!file.Exists)
        {
            _logger.LogWarning("There is no document at {Path}; the extraction cannot run.", path);

            return null;
        }

        if (file.Length > _options.Storage.MaxBytes)
        {
            _logger.LogWarning(
                "A document of {Bytes} bytes exceeds Ocr:Storage:MaxBytes ({Max}) and was refused before decoding.",
                file.Length, _options.Storage.MaxBytes);

            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);

            return new RawDocument(bytes, ContentTypes.FromBytes(bytes, path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "A document at {Path} could not be read.", path);

            return null;
        }
    }

    private async Task<RawDocument?> ReadOverHttpAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!_options.Storage.AllowHttpSources)
        {
            _logger.LogError(
                "A docs.uploads.storage_url named {Scheme}://{Host} and Ocr:Storage:AllowHttpSources is off. "
                + "A service that fetches any URL written into a table it does not own is one row away from "
                + "reading the cluster's metadata endpoint.",
                uri.Scheme, uri.Host);

            return null;
        }

        try
        {
            var client = _clients.CreateClient(HttpClientName);

            using var response = await client.GetAsync(uri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Object storage answered {Status} for a document.", (int)response.StatusCode);

                return null;
            }

            if (response.Content.Headers.ContentLength > _options.Storage.MaxBytes)
            {
                _logger.LogWarning("A document exceeded Ocr:Storage:MaxBytes and was refused before download.");

                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            if (bytes.Length > _options.Storage.MaxBytes)
            {
                return null;
            }

            return new RawDocument(
                bytes,
                ContentTypes.FromBytes(bytes, response.Content.Headers.ContentType?.MediaType ?? uri.AbsolutePath));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Object storage could not be reached for a document.");

            return null;
        }
    }
}

/// <summary>
/// What an upload actually is, decided from its bytes rather than from its name.
/// </summary>
/// <remarks>
/// The extension on a <c>storage_url</c> is whatever the uploading client called the file. The
/// content type chooses the temporary file's suffix for Tesseract and the re-encode for the
/// redacted copy, and a JPEG named <c>.png</c> would have both of them wrong.
/// </remarks>
internal static class ContentTypes
{
    public static string FromBytes(ReadOnlySpan<byte> bytes, string hint)
    {
        if (bytes.Length >= 12)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                return "image/jpeg";
            }

            if (bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..])
            {
                return "image/png";
            }

            if (bytes is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..])
            {
                return "image/webp";
            }

            if (bytes is [(byte)'%', (byte)'P', (byte)'D', (byte)'F', ..])
            {
                return "application/pdf";
            }

            if (bytes is [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..])
            {
                return "image/tiff";
            }
        }

        return hint.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
    }
}

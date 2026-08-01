using MageRide.Ocr.Configuration;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Storage;

/// <summary>The bytes of one raw upload, and what they are.</summary>
public sealed record RawDocument(ReadOnlyMemory<byte> Bytes, string ContentType);

/// <summary>
/// Reads a raw document out of object storage (D-36's SSE-KMS bucket).
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ D-36: the implementation is the kernel's <c>IObjectStore</c>.</b> The same bucket the three
/// uploading services write to, so this service reads exactly what they wrote rather than hoping a
/// path resolves the same way in two containers. What ocr-svc owes regardless of where the bytes sit
/// is that they are read
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
/// <para>
/// Delegates to the kernel store, which resolves an <c>s3://</c> pointer against D-36's bucket and
/// a <c>file://</c> one against the filesystem — so a document written before the bucket existed is
/// still extractable afterwards, and the traversal refusal is made in one place for the platform.
/// </para>
/// <para>
/// <b>An <c>http(s)</c> pointer stays this service's own decision, behind its own switch.</b>
/// Fetching an arbitrary URL out of a table is an SSRF primitive and the kernel store refuses them
/// outright; <c>Ocr:Storage:AllowHttpSources</c> is what re-enables it here.
/// </para>
/// <para>
/// <b>Nothing here throws for a document it cannot read.</b> A missing object and an oversized one
/// both answer <see langword="null"/>: the extraction then fails, the onboarding step still saves,
/// and a Verification Officer takes it.
/// </para>
/// </remarks>
public sealed class FileSystemRawDocumentStore : IRawDocumentStore
{
    private readonly IObjectStore _objects;
    private readonly OcrOptions _options;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<FileSystemRawDocumentStore> _logger;

    /// <summary>The named client used only when <c>AllowHttpSources</c> is on.</summary>
    public const string HttpClientName = "document-storage";

    public FileSystemRawDocumentStore(
        IObjectStore objects,
        IOptions<OcrOptions> options,
        IHttpClientFactory clients,
        ILogger<FileSystemRawDocumentStore> logger)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
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

        var bytes = await _objects.ReadAsync(storageUrl, cancellationToken);

        if (bytes is null)
        {
            return null;
        }

        if (bytes.Bytes.Length > _options.Storage.MaxBytes)
        {
            _logger.LogWarning(
                "Document at {StorageUrl} is {Length} bytes, over the {Max} ceiling, and was not read.",
                storageUrl,
                bytes.Bytes.Length,
                _options.Storage.MaxBytes);

            return null;
        }

        return new RawDocument(bytes.Bytes, bytes.ContentType);
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

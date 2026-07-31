using System.Security.Cryptography;
using MageRide.Fleet.Configuration;
using MageRide.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Documents;

/// <summary>What the bytes of one payout document became.</summary>
public sealed record StoredDocument(string StorageUrl, byte[] Sha256, long Bytes);

/// <summary>
/// Where the bytes of a payout document go.
/// </summary>
/// <remarks>
/// <b>One method, because the destination is a deployment concern.</b> D-36 puts every uploaded
/// document on SSE-KMS object storage and no service in this build has an S3 client (C125), so the
/// filesystem implementation below is what runs and this interface is the seam that replaces it.
/// The <c>docs.uploads</c> row is written either way and the profile links the <b>id</b>, never a
/// URL — so swapping the store is one class and no migration. Same arrangement as support-svc's
/// <c>IScreenshotStore</c>.
/// </remarks>
public interface IDocumentStore
{
    Task<StoredDocument> WriteAsync(
        Guid uploadId, string kind, Stream content, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDocumentStore"/>
internal sealed class FileSystemDocumentStore : IDocumentStore
{
    private readonly FleetOptions _options;
    private readonly string _root;

    public FileSystemDocumentStore(IOptions<FleetOptions> options, ILogger<FileSystemDocumentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;

        _root = string.IsNullOrWhiteSpace(_options.DocumentRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride-fleet-payout-docs")
            : _options.DocumentRoot;

        Directory.CreateDirectory(_root);
    }

    public async Task<StoredDocument> WriteAsync(
        Guid uploadId, string kind, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        // The id names the file, and the id is minted by the caller before the row exists: the
        // bytes are written first, so a crash between the two leaves an orphan file — which
        // NFR-28's deadline sweeps — rather than a profile pointing at nothing, which is a
        // document the Verification Officer is told exists and cannot open.
        var path = Path.Combine(_root, $"{uploadId:N}-{kind}");

        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);

        using var hasher = SHA256.Create();

        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;

        // Streamed and counted rather than buffered into memory and measured: `Content-Length` on
        // a multipart part is whatever the client said, and an 8 MiB ceiling enforced against a
        // claimed length is not a ceiling at all.
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;

            if (total > _options.DocumentMaxBytes)
            {
                // The partial file is removed before the throw: keeping it would leave bytes on
                // disk for a request that was refused, with no row and no deletion deadline.
                file.Close();
                TryDelete(path);

                throw new MageRideException(
                    MageRideErrors.PayloadTooLarge,
                    $"A payout document is at most {_options.DocumentMaxBytes} bytes.");
            }

            hasher.TransformBlock(buffer, 0, read, null, 0);
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        hasher.TransformFinalBlock([], 0, 0);

        if (total == 0)
        {
            file.Close();
            TryDelete(path);

            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["The document is empty."],
            });
        }

        await file.FlushAsync(cancellationToken);

        // A `file://` URL rather than a bare path, so the column reads as a location whatever the
        // store is and the S3 swap changes the scheme rather than the meaning.
        return new StoredDocument(new Uri(Path.GetFullPath(path)).AbsoluteUri, hasher.Hash!, total);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The sweeper's problem, not this request's: the caller is already being told why its
            // upload was refused and a second failure here would replace that with an I/O error.
        }
    }
}

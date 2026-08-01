using MageRide.Registry.Configuration;
using MageRide.Registry.Domain;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Dapper;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Vehicles;

/// <summary>
/// Writes a payout document's bytes and its <c>docs.uploads</c> row (AL-58, AL-59).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not object storage.</b> D-36's SSE-KMS bucket is C125's and no client exists yet, so the bytes
/// land under <c>Registry:PayoutDocumentRoot</c> and <c>docs.uploads.storage_url</c> records where.
/// The signed URL an officer opens them by is admin-bff's (C063) — this service holds no signing
/// key, exactly as fleet-svc holds none for the same slots.
/// </para>
/// <para>
/// <b>The bytes are written before the row.</b> A crash between them leaves an orphan file, which
/// NFR-28's deadline sweeps; the other order leaves a profile pointing at a document the officer is
/// told exists and cannot open.
/// </para>
/// <para>
/// <b><c>captured_via</c> is left NULL.</b> AL-43's provenance is about onboarding *photographs*,
/// where a gallery pick is a fraud signal. A bank statement is exported from a banking app, and
/// recording <c>gallery</c> would put a fraud signal on every payout profile on the platform —
/// fleet-svc's decision, kept.
/// </para>
/// </remarks>
public interface IPayoutDocumentStore
{
    Task<Guid> WriteAsync(Guid driverId, string kind, Stream content, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPayoutDocumentStore"/>
internal sealed class PayoutDocumentStore : IPayoutDocumentStore
{
    private readonly INpgsqlConnectionFactory _connections;
    private readonly RegistryOptions _options;
    private readonly string _root;

    public PayoutDocumentStore(
        INpgsqlConnectionFactory connections,
        IOptions<RegistryOptions> options,
        ILogger<PayoutDocumentStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _options = options.Value;

        _root = string.IsNullOrWhiteSpace(_options.PayoutDocumentRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride-driver-payout-docs")
            : _options.PayoutDocumentRoot;

        Directory.CreateDirectory(_root);

        if (string.IsNullOrWhiteSpace(_options.PayoutDocumentRoot))
        {
            logger.LogWarning(
                "Registry:PayoutDocumentRoot is not configured, so payout documents are written under {Root}. "
                + "That is a temporary directory: a restart can take an officer's evidence with it.",
                _root);
        }
    }

    public async Task<Guid> WriteAsync(
        Guid driverId, string kind, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!DriverPayoutDocumentKinds.All.Contains(kind))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = [$"kind must be one of {string.Join(", ", DriverPayoutDocumentKinds.All.Order(StringComparer.Ordinal))}."],
            });
        }

        var uploadId = Guid.CreateVersion7();
        var path = Path.Combine(_root, $"{uploadId:N}.bin");

        // Counted while streaming, not read from Content-Length: a ceiling enforced against a length
        // the client declared is not a ceiling (fleet-svc's rule).
        long written;

        await using (var file = File.Create(path))
        {
            written = await CopyBoundedAsync(content, file, _options.PayoutDocumentMaxBytes, cancellationToken);
        }

        if (written < 0)
        {
            File.Delete(path);

            throw new MageRideException(
                MageRideErrors.PayloadTooLarge,
                $"A payout document must be at most {_options.PayoutDocumentMaxBytes} bytes.");
        }

        await using var connection = await _connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO docs.uploads (id, owner_id, storage_url, kind, auto_delete_at)
            VALUES (@Id, @OwnerId, @StorageUrl, @Kind, now() + @Retention);
            """,
            new
            {
                Id = uploadId,
                OwnerId = driverId,
                StorageUrl = path,
                Kind = kind,
                Retention = _options.PayoutDocumentRetention,
            },
            cancellationToken: cancellationToken));

        return uploadId;
    }

    /// <summary>Copies up to <paramref name="limit"/> bytes; returns -1 when the source is longer.</summary>
    private static async Task<long> CopyBoundedAsync(
        Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;

            if (total > limit)
            {
                return -1;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total;
    }
}

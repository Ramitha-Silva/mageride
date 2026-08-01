using MageRide.Registry.Configuration;
using MageRide.Registry.Domain;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Storage;
using Dapper;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Vehicles;

/// <summary>
/// Writes a payout document's bytes and its <c>docs.uploads</c> row (AL-58, AL-59).
/// </summary>
/// <remarks>
/// <para>
/// <b>D-36's bucket, through the kernel's <see cref="IObjectStore"/>.</b> Server-side encrypted,
/// presignable, and expired by the bucket's own lifecycle rule. Unconfigured it falls back to the
/// filesystem under <c>Registry:PayoutDocumentRoot</c> and says so at start-up, so a deployment
/// that has not set <c>Storage:*</c> behaves exactly as it did before.
/// </para>
/// <para>
/// <b>The bytes are written before the row.</b> A crash between them leaves an orphan object, which
/// NFR-28's expiry reclaims; the other order leaves a profile pointing at a document the officer is
/// told exists and cannot open.
/// </para>
/// <para>
/// <b>A LankaQR is NOT raw evidence and is never expired.</b> AL-59 makes a driver's own bank-app QR
/// what a passenger scans to pay them on <em>every ride</em> — it is live payment infrastructure,
/// not a document somebody checked once. Expiring it under NFR-28 alongside the bank statement
/// would have broken that rail for every driver 90 days after they uploaded it, silently and one
/// driver at a time. So the QR is stored <c>retained</c> with no <c>auto_delete_at</c>, and only
/// the proof of account carries a deadline.
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
    Task<Guid> WriteAsync(
        Guid driverId, string kind, Stream content, string contentType, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPayoutDocumentStore"/>
internal sealed class PayoutDocumentStore(
    INpgsqlConnectionFactory connections,
    IObjectStore objects,
    IOptions<RegistryOptions> options) : IPayoutDocumentStore
{
    private readonly RegistryOptions _options = options.Value;

    public async Task<Guid> WriteAsync(
        Guid driverId, string kind, Stream content, string contentType, CancellationToken cancellationToken)
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

        // Null retention on the QR is the load-bearing part — see the type's remarks. The store
        // turns it into the `retained/` key prefix, which the NFR-28 lifecycle rule does not match.
        TimeSpan? retention = kind == DriverPayoutDocumentKinds.LankaqrCode
            ? null
            : _options.PayoutDocumentRetention;

        var stored = await objects.PutAsync(
            new ObjectPutRequest(
                $"registry/driver-payout/{driverId:N}/{uploadId:N}",
                content,
                contentType,
                _options.PayoutDocumentMaxBytes,
                retention),
            cancellationToken);

        await using var connection = await connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO docs.uploads (id, owner_id, storage_url, sha256, kind, auto_delete_at)
            VALUES (@Id, @OwnerId, @StorageUrl, @Sha256, @Kind,
                    CASE WHEN @Retention::interval IS NULL THEN NULL ELSE now() + @Retention END);
            """,
            new
            {
                Id = uploadId,
                OwnerId = driverId,
                StorageUrl = stored.StorageUrl,
                Sha256 = stored.Sha256,
                Kind = kind,
                Retention = retention,
            },
            cancellationToken: cancellationToken));

        return uploadId;
    }
}

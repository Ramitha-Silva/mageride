using System.Security.Cryptography;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Configuration;
using MageRide.AdminBff.Domain;
using MageRide.AdminBff.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using MageRide.Shared.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.AdminBff.Pdpa;

/// <summary>A request as the subject or the queue sees it, with the download resolved if there is one.</summary>
public sealed record PdpaRequestView(
    PdpaRequestRow Request,
    IReadOnlyList<StatutoryHold> Holds,
    string? DownloadUrl,
    DateTimeOffset? DownloadExpiresAt);

/// <summary>
/// E-06's export and erasure workflow (US-1.8, ADD §6 admin-bff, `pdpa` schema §16).
/// </summary>
public interface IPdpaService
{
    /// <summary>The data subject's own request. 409 while one of the same kind is open.</summary>
    Task<(PdpaRequestRow Request, IReadOnlyList<StatutoryHold> Holds)> RequestAsync(
        Guid userId, string kind, CancellationToken cancellationToken);

    /// <summary>Status and, for a fulfilled export, a short-lived signed download.</summary>
    Task<PdpaRequestView> StatusAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>The admin queue — open requests by deadline, or the decided history.</summary>
    Task<IReadOnlyList<PdpaRequestView>> QueueAsync(string? status, int limit, CancellationToken cancellationToken);

    /// <summary>Delivers an export, or carries out an erasure honouring the statutory hold list.</summary>
    Task<PdpaRequestView> FulfilAsync(
        Guid requestId, string? outcome, string? holdReason, string? artifactUrl, Guid actorId,
        CancellationToken cancellationToken);

    /// <summary>Refuses a request outright, with the reason the subject is shown.</summary>
    Task<PdpaRequestView> RejectAsync(
        Guid requestId, string reason, Guid actorId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPdpaService"/>
/// <remarks>
/// <para>
/// <b>An erasure is a soft anonymisation and the hold list is what bounds it.</b> Two kinds of hold,
/// and conflating them is the mistake <see cref="StatutoryHolds"/> exists to prevent: a
/// <em>blocking</em> hold is a live operation anonymising the account would break (a passenger
/// mid-ride, an open dispute, an unsettled payment, money still in a wallet) and answers 409; a
/// <em>retention</em> hold is a record a statute requires be kept (the financial ledger, the audit
/// trail) and is what turns <c>Fulfilled</c> into <c>FulfilledHold</c>. Treating them alike would
/// either refuse every erasure for ever — every account has a ledger — or anonymise somebody who is
/// in a car right now.
/// </para>
/// <para>
/// <b><c>audit.events</c> is never touched, and the fulfilment writes to it.</b> A right-to-erasure
/// that deleted the record of the erasure would leave the platform unable to prove it complied, and
/// D-35's log is append-only by design. The retention is declared on the request rather than being
/// silent, because a subject is entitled to know what was kept and under what basis.
/// </para>
/// <para>
/// <b>The decision and its audit row commit together.</b> <c>FlushAsync(unitOfWork, …)</c> just
/// before <c>CommitAsync</c>, the rule this platform is written under: an anonymisation whose audit
/// row was lost by the crash somebody would later want explained is the worst possible outcome for
/// exactly this workflow.
/// </para>
/// <para>
/// <b>The archive is written before the transaction opens.</b> Assembling a ZIP over fourteen
/// datasets and pushing it to object storage inside a transaction would hold a Postgres write
/// transaction open across a network round trip to a bucket. The failure mode of the other order is
/// an orphaned object nobody references, which the bucket's own lifecycle rule sweeps; the failure
/// mode of holding the transaction is a lock on <c>pdpa.requests</c> for as long as the bucket is
/// slow.
/// </para>
/// </remarks>
internal sealed class PdpaService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IPdpaRepository pdpa,
    IPdpaArtifactLinks links,
    IObjectStore objects,
    IAdminAuditContext audit,
    IOptions<AdminBffOptions> options,
    TimeProvider clock,
    ILogger<PdpaService> logger) : IPdpaService
{
    /// <summary>
    /// How long an export archive may live in the bucket.
    /// </summary>
    /// <remarks>
    /// <b>Any non-null value puts the object under the <c>ephemeral/</c> prefix</b>, which is what
    /// the D-36 bucket's one lifecycle rule matches — the store uses the retention to choose the
    /// prefix and the bucket enforces <c>Storage:RawRetention</c>. That is the right class for this:
    /// an export archive is a copy of everything the platform holds about one person, and it must
    /// not be a permanent second copy. It is emphatically not <c>Retained</c>, which is for objects
    /// the platform keeps *serving* — a driver's LankaQR, scanned on every ride.
    /// </remarks>
    private static readonly TimeSpan ArchiveRetention = TimeSpan.FromDays(30);

    /// <summary>A ceiling, not a working limit — an archive far larger than this is a bug upstream.</summary>
    private const long MaxArchiveBytes = 256L * 1024 * 1024;

    private readonly AdminBffOptions.PdpaOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Pdpa;

    // ---------------------------------------------------------------------------------------
    // The data subject's half
    // ---------------------------------------------------------------------------------------

    public async Task<(PdpaRequestRow Request, IReadOnlyList<StatutoryHold> Holds)> RequestAsync(
        Guid userId, string kind, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // A second request while one is open is a 409, not a second row — iam-svc's DELETE
        // /v1/users/me makes the same call for the same reason: two 30-day clocks against one
        // obligation leave whichever is not fulfilled permanently overdue in the SLA queue.
        var open = await pdpa.FindOpenAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, kind, cancellationToken);

        if (open is not null)
        {
            await unitOfWork.RollbackAsync(cancellationToken);

            throw new MageRideException(
                MageRideErrors.Conflict,
                $"A {kind} request is already open for this account (due {open.DueBy:yyyy-MM-dd}).");
        }

        var request = await pdpa.InsertAsync(
            unitOfWork.Connection, unitOfWork.Transaction, userId, kind, cancellationToken);

        // Computed inside the same transaction that inserts the request, so the holds the subject is
        // told about are the holds as at the instant their clock started. They are a preview and not
        // a promise — a ride that ends tomorrow lifts one — which is exactly why they are recomputed
        // at fulfilment rather than stored now.
        var holds = string.Equals(kind, PdpaKinds.Erasure, StringComparison.Ordinal)
            ? await pdpa.HoldsAsync(unitOfWork.Connection, unitOfWork.Transaction, userId, cancellationToken)
            : [];

        audit.Record(
            request.Id,
            after: new
            {
                kind,
                status = request.Status,
                dueBy = request.DueBy,
                subjectId = userId,
                holdReasons = holds.Select(hold => hold.Code),
            },
            action: AdminAuditActions.PdpaRequested,
            entityType: AdminAuditActions.PdpaRequestEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "PDPA {Kind} request {RequestId} opened for {UserId}, due {DueBy}. Holds at request time: {Holds}.",
            kind, request.Id, userId, request.DueBy, holds.Count == 0 ? "none" : string.Join(", ", holds.Select(h => h.Code)));

        return (request, holds);
    }

    public async Task<PdpaRequestView> StatusAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var request = await pdpa.FindAsync(requestId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, "No such PDPA request.");

        return await ViewAsync(request, holds: [], cancellationToken);
    }

    // ---------------------------------------------------------------------------------------
    // The operator's half
    // ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PdpaRequestView>> QueueAsync(
        string? status, int limit, CancellationToken cancellationToken)
    {
        if (status is not null && !PdpaAllStatuses.Contains(status, StringComparer.Ordinal))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = [$"status is one of: {string.Join(", ", PdpaAllStatuses)}."],
            });
        }

        var rows = await pdpa.QueueAsync(status, limit, cancellationToken);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var views = new List<PdpaRequestView>(rows.Count);

        foreach (var row in rows)
        {
            // The queue carries the live hold list for an open erasure, because that is the whole of
            // what the operator has to decide about — pressing Fulfil on a request whose subject is
            // mid-ride is refused, and being shown that before pressing is the difference between a
            // queue and a lottery. A decided request's holds are already recorded on the row.
            var holds = string.Equals(row.Kind, PdpaKinds.Erasure, StringComparison.Ordinal)
                        && PdpaStatuses.IsOpen(row.Status)
                ? await pdpa.HoldsAsync(connection, null, row.UserId, cancellationToken)
                : [];

            views.Add(await ViewAsync(row, holds, cancellationToken));
        }

        return views;
    }

    public async Task<PdpaRequestView> FulfilAsync(
        Guid requestId, string? outcome, string? holdReason, string? artifactUrl, Guid actorId,
        CancellationToken cancellationToken)
    {
        var declared = outcome?.Trim();

        if (declared is not (null or PdpaStatuses.Fulfilled or PdpaStatuses.FulfilledHold))
        {
            throw Invalid("outcome", $"outcome is {PdpaStatuses.Fulfilled} or {PdpaStatuses.FulfilledHold}.");
        }

        var request = await pdpa.FindAsync(requestId, cancellationToken)
                      ?? throw new MageRideException(MageRideErrors.NotFound, "No such PDPA request.");

        RequireOpen(request);

        var now = clock.GetUtcNow();

        // Both halves that touch the outside world happen before the transaction: the export's ZIP
        // goes to object storage, and nothing else does. See the class remark.
        var stored = string.Equals(request.Kind, PdpaKinds.Export, StringComparison.Ordinal)
            ? await StoreExportAsync(request, artifactUrl, now, cancellationToken)
            : null;

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        // Re-read under the row lock: two operators working one queue must not both fulfil one
        // obligation, and the second must be told the first already did.
        var locked = await pdpa.LockAsync(unitOfWork, requestId, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, "No such PDPA request.");

        RequireOpen(locked);

        var holds = await pdpa.HoldsAsync(
            unitOfWork.Connection, unitOfWork.Transaction, locked.UserId, cancellationToken);

        ErasureOutcome? erased = null;

        if (string.Equals(locked.Kind, PdpaKinds.Erasure, StringComparison.Ordinal))
        {
            var blocking = holds.Where(hold => hold.Blocking).ToArray();

            if (blocking.Length > 0)
            {
                await unitOfWork.RollbackAsync(cancellationToken);

                // 409 rather than a partial fulfilment. A blocking hold is a live operation, not a
                // statute: it lifts on its own, and recording the obligation as met while a
                // passenger is still in a car would be a false compliance claim.
                throw new MageRideException(
                    MageRideErrors.Conflict,
                    "This erasure is held by a live record and cannot be fulfilled yet: "
                    + string.Join(", ", blocking.Select(hold => $"{hold.Code} ({hold.Count})"))
                    + ". Fulfil it once they close, or reject it with a reason.");
            }

            erased = await pdpa.AnonymiseAsync(unitOfWork, locked.UserId, now, cancellationToken);
        }

        var retained = holds.Where(hold => !hold.Blocking).ToArray();

        // The outcome is derived unless the operator overrode it, and the derivation is the honest
        // one: anything retained under a statute IS a FulfilledHold, whatever the button said.
        var status = declared ?? (retained.Length > 0 ? PdpaStatuses.FulfilledHold : PdpaStatuses.Fulfilled);

        if (string.Equals(status, PdpaStatuses.Fulfilled, StringComparison.Ordinal) && retained.Length > 0)
        {
            status = PdpaStatuses.FulfilledHold;
        }

        var reason = string.Equals(status, PdpaStatuses.FulfilledHold, StringComparison.Ordinal)
            // ck_pdpa_requests_hold demands one. The operator's words win where they gave any; the
            // derived list is what the holds actually were, which is the falsifiable version.
            ? holdReason?.Trim() is { Length: > 0 } supplied
                ? supplied
                : string.Join(",", retained.Select(hold => hold.Code))
            : null;

        if (string.Equals(status, PdpaStatuses.FulfilledHold, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(reason))
        {
            throw Invalid("holdReason", $"{PdpaStatuses.FulfilledHold} must say what was retained and why.");
        }

        var decided = await pdpa.DecideAsync(
            unitOfWork, requestId, status, actorId, reason, decisionReason: null, now, cancellationToken);

        if (stored is not null)
        {
            await pdpa.AddArtifactAsync(
                unitOfWork,
                requestId,
                PdpaArtifactKinds.ExportZip,
                stored.StorageUrl,
                stored.Sha256,
                now,
                cancellationToken);
        }
        else if (erased is not null)
        {
            // The compliance record of an erasure: what was removed and what a statute kept. Stored
            // as the artifact's pointer rather than as bytes — there is nothing to download, and a
            // second object in the bucket for one line of text would be a file nobody reads.
            await pdpa.AddArtifactAsync(
                unitOfWork,
                requestId,
                PdpaArtifactKinds.ErasureLog,
                $"pdpa:erasure:{requestId:N}",
                sha256: null,
                now,
                cancellationToken);
        }

        audit.Record(
            requestId,
            before: new { status = locked.Status, kind = locked.Kind, subjectId = locked.UserId },
            after: new
            {
                status,
                subjectId = locked.UserId,
                holdReason = reason,
                retained = retained.Select(hold => new { hold.Code, hold.Count }),
                artifact = stored?.StorageUrl,
                erasure = erased is null
                    ? null
                    : new
                    {
                        emergencyContacts = erased.EmergencyContacts,
                        savedAddresses = erased.SavedAddresses,
                        phoneLookups = erased.PhoneLookups,
                        sessionsRevoked = erased.SessionsRevoked,
                        driverProfile = erased.DriverProfile,
                    },
            },
            action: AdminAuditActions.PdpaFulfilled,
            entityType: AdminAuditActions.PdpaRequestEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "PDPA {Kind} request {RequestId} for {UserId} fulfilled as {Status} by {ActorId}. Retained: {Retained}.",
            locked.Kind, requestId, locked.UserId, status, actorId,
            retained.Length == 0 ? "nothing" : string.Join(", ", retained.Select(hold => hold.Code)));

        return await ViewAsync(decided, retained, cancellationToken);
    }

    public async Task<PdpaRequestView> RejectAsync(
        Guid requestId, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        var trimmed = reason?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw Invalid("reason", "reason is required — it is shown to the person who asked (E-06).");
        }

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var locked = await pdpa.LockAsync(unitOfWork, requestId, cancellationToken)
                     ?? throw new MageRideException(MageRideErrors.NotFound, "No such PDPA request.");

        RequireOpen(locked);

        var decided = await pdpa.DecideAsync(
            unitOfWork, requestId, PdpaStatuses.Rejected, actorId, holdReason: null, trimmed, now, cancellationToken);

        audit.Record(
            requestId,
            before: new { status = locked.Status, kind = locked.Kind, subjectId = locked.UserId },
            after: new { status = PdpaStatuses.Rejected, subjectId = locked.UserId, reason = trimmed },
            action: AdminAuditActions.PdpaRejected,
            entityType: AdminAuditActions.PdpaRequestEntity);

        await audit.FlushAsync(unitOfWork, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "PDPA {Kind} request {RequestId} for {UserId} rejected by {ActorId}: {Reason}",
            locked.Kind, requestId, locked.UserId, actorId, trimmed);

        return await ViewAsync(decided, holds: [], cancellationToken);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Assembles and stores the archive, or records the pointer the operator supplied.</summary>
    private async Task<StoredObject?> StoreExportAsync(
        PdpaRequestRow request, string? artifactUrl, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (artifactUrl?.Trim() is { Length: > 0 } supplied)
        {
            // An operator who has produced the archive out of band records the pointer; the platform
            // does not have to have been the one that assembled it. There is no hash, because this
            // process never saw the bytes and inventing one would make a later "this is not what you
            // sent me" answerable with a number nobody computed.
            return new StoredObject(supplied, [], 0);
        }

        var archive = await PdpaExport.BuildAsync(
            pdpa, request.Id, request.UserId, _options.MaxRowsPerDataset, now, cancellationToken);

        using var content = new MemoryStream(archive.Bytes, writable: false);

        var stored = await objects.PutAsync(
            new ObjectPutRequest(
                links.KeyFor(request.Id),
                content,
                "application/zip",
                MaxArchiveBytes,
                ArchiveRetention),
            cancellationToken);

        logger.LogInformation(
            "PDPA export {RequestId} assembled: {Bytes} bytes over {Datasets} datasets ({Records} records"
            + "{Truncated}), stored at {StorageUrl}.",
            request.Id,
            stored.Length,
            archive.Counts.Count,
            archive.Counts.Values.Sum(),
            archive.Truncated ? ", truncated" : string.Empty,
            stored.StorageUrl);

        // The store hashes what it wrote. Recorded on the artifact so a later dispute about what was
        // delivered has an answer that does not depend on the bytes still being there.
        return stored.Sha256.Length > 0
            ? stored
            : stored with { Sha256 = SHA256.HashData(archive.Bytes) };
    }

    private async Task<PdpaRequestView> ViewAsync(
        PdpaRequestRow request, IReadOnlyList<StatutoryHold> holds, CancellationToken cancellationToken)
    {
        var artifact = await pdpa.FindArtifactAsync(request.Id, cancellationToken);

        // Only an export has something to download, and only once it has been fulfilled. An erasure
        // log is a compliance record with no bytes behind it and no reader outside this platform.
        if (artifact is null
            || !string.Equals(artifact.Kind, PdpaArtifactKinds.ExportZip, StringComparison.Ordinal))
        {
            return new PdpaRequestView(request, holds, null, null);
        }

        var (url, expiresAt) = links.Signed(request.Id, artifact.StorageUrl);

        return new PdpaRequestView(request, holds, url, expiresAt);
    }

    /// <remarks>
    /// A decided request is a 409 rather than an idempotent no-op, and that is deliberate for both
    /// verbs: fulfilling an already-rejected erasure would anonymise an account whose owner was told
    /// their request was refused, and rejecting an already-fulfilled one would record a refusal of
    /// something that has already happened. Neither is a double click worth being permissive about.
    /// </remarks>
    private static void RequireOpen(PdpaRequestRow request)
    {
        if (!PdpaStatuses.IsOpen(request.Status))
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"This request was already decided ({request.Status}) and cannot be decided again.");
        }
    }

    private static readonly string[] PdpaAllStatuses =
    [
        PdpaStatuses.Received,
        PdpaStatuses.InProgress,
        PdpaStatuses.FulfilledHold,
        PdpaStatuses.Fulfilled,
        PdpaStatuses.Rejected,
    ];

    private static MageRideValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

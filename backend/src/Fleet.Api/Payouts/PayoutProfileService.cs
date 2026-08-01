using MageRide.Fleet.Configuration;
using MageRide.Fleet.Documents;
using MageRide.Fleet.Domain;
using MageRide.Fleet.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Payouts;

/// <summary>The bank details an owner submits (AL-49, SCR-FP-002a).</summary>
public sealed record PayoutProfileDraft(string Bank, string Branch, string AccountNo, string AccountHolderName);

/// <summary>What an upload produced.</summary>
public sealed record AttachedPayoutDocument(Guid DocId, string Kind, PayoutProfile Profile);

/// <summary>
/// The organisation's bank &amp; payout profile — the one thing Mode B money depends on (BR-31.1).
/// </summary>
public interface IPayoutProfileService
{
    /// <summary>The version the owner is editing. Throws 404 when the org has never submitted one.</summary>
    Task<PayoutProfile> ReadAsync(Guid fleetId, CancellationToken cancellationToken);

    /// <summary>Creates or edits the profile; the result is always <c>pending_verification</c>.</summary>
    Task<PayoutProfile> UpsertAsync(Guid fleetId, PayoutProfileDraft draft, CancellationToken cancellationToken);

    Task<AttachedPayoutDocument> AttachDocumentAsync(
        Guid fleetId, Guid ownerId, string kind, Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// <inheritdoc cref="IPayoutProfileService"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>Every edit re-enters verification, and no edit ever redirects money.</b> BR-31.1 in two
/// halves, and the second is the one that costs something to get right: an edit to a
/// <c>verified</c> profile <em>inserts</em> a new pending version and leaves the incumbent
/// verified, so subscription-svc's <c>payTo</c> keeps rendering the account an officer approved
/// until another officer approves the replacement. Overwriting in place would silently redirect
/// every Paid subscriber's next transfer to whatever the owner typed.
/// </para>
/// <para>
/// <b>A document is an edit.</b> BR-31.1 says "any edit re-enters <c>pending_verification</c>" and
/// does not carve out the evidence: replacing the bank statement behind a verified profile is
/// exactly the change an officer would want to see again. So an upload against a verified profile
/// forks a new pending version carrying the other slot forward, the same as a bank-detail edit.
/// </para>
/// </remarks>
internal sealed class PayoutProfileService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IFleetScopedReader scopedReader,
    IPayoutProfileRepository profiles,
    IPayoutDocumentRepository documents,
    IDocumentStore store,
    IOptions<FleetOptions> options,
    ILogger<PayoutProfileService> logger) : IPayoutProfileService
{
    private readonly FleetOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<PayoutProfile> ReadAsync(Guid fleetId, CancellationToken cancellationToken) =>
        await scopedReader.ReadAsync(
            fleetId,
            async (connection, transaction) =>
                await profiles.FindCurrentAsync(connection, transaction, fleetId, cancellationToken)
                ?? throw new MageRideException(
                    FleetErrors.PayoutProfileNotFound,
                    "This organisation has not submitted a bank and payout profile yet."),
            cancellationToken);

    public async Task<PayoutProfile> UpsertAsync(
        Guid fleetId, PayoutProfileDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var current = await profiles.FindCurrentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken);

        PayoutProfile saved;

        if (current is not null && current.IsPending)
        {
            // In place. Nothing is collecting against a pending version and nobody has decided on
            // it, so a correction is a correction — inserting instead would put a second
            // application for one organisation on the officer's queue for every digit fixed.
            saved = await profiles.UpdatePendingAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                current.Id,
                draft.Bank,
                draft.Branch,
                draft.AccountNo,
                draft.AccountHolderName,
                cancellationToken);
        }
        else
        {
            // A new version. The evidence is carried forward so the owner need not re-photograph a
            // passbook to correct a branch name — and so the officer has something to look at.
            saved = await profiles.InsertPendingAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                fleetId,
                draft.Bank,
                draft.Branch,
                draft.AccountNo,
                draft.AccountHolderName,
                current?.ProofUploadId,
                current?.LankaqrUploadId,
                cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        if (current?.IsVerified == true)
        {
            logger.LogInformation(
                "Fleet {FleetId} edited a verified payout profile; version {NewProfileId} is pending verification "
                + "while {VerifiedProfileId} stays verified and keeps collecting (BR-31.1).",
                fleetId,
                saved.Id,
                current.Id);
        }

        return saved;
    }

    public async Task<AttachedPayoutDocument> AttachDocumentAsync(
        Guid fleetId, Guid ownerId, string kind, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!PayoutDocumentKinds.All.Contains(kind))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = [$"kind must be one of {string.Join(", ", PayoutDocumentKinds.All.Order(StringComparer.Ordinal))}."],
            });
        }

        // The id is minted here because it names the file: the bytes are written before the row,
        // so a crash between them leaves an orphan the NFR-28 sweeper takes, rather than a profile
        // pointing at a document the officer cannot open.
        var uploadId = Guid.CreateVersion7();
        var stored = await store.WriteAsync(uploadId, kind, content, cancellationToken);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var current = await profiles.FindCurrentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, fleetId, cancellationToken)
            ?? throw new MageRideException(
                FleetErrors.PayoutProfileNotFound,
                "Submit the bank details first; a document is attached to a profile, not to an organisation.");

        var upload = await documents.CreateAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ownerId,
            stored.StorageUrl,
            stored.Sha256,
            kind,
            // NULL for a LankaQR (AL-49): it is rendered on the pay sheet rather than checked
            // once, so NFR-28's deadline must not reach it. See IDocumentStore.RetentionFor.
            store.RetentionFor(kind),
            cancellationToken);

        var isLankaQr = PayoutDocumentKinds.IsLankaQr(kind);

        // Attaching to a verified version would change the evidence behind an approved account
        // without an officer seeing it, so the fork happens first and the document lands on the
        // new pending version — "any edit re-enters pending_verification" (BR-31.1).
        var target = current.IsVerified
            ? await profiles.InsertPendingAsync(
                unitOfWork.Connection,
                unitOfWork.Transaction,
                fleetId,
                current.Bank,
                current.Branch,
                current.AccountNo,
                current.AccountHolderName,
                isLankaQr ? current.ProofUploadId : null,
                isLankaQr ? null : current.LankaqrUploadId,
                cancellationToken)
            : current;

        var profile = await profiles.AttachDocumentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, target.Id, upload.Id, isLankaQr, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        return new AttachedPayoutDocument(upload.Id, kind, profile);
    }
}

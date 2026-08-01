using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;

namespace MageRide.Registry.Vehicles;

/// <summary>What a driver submitted on SCR-DA-022a.</summary>
public sealed record DriverPayoutDraft(string Bank, string Branch, string AccountNo, string AccountHolderName);

/// <summary>
/// The driver's bank &amp; payout profile (AL-58, AL-59) — where their swept earnings go.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces D-11's merchant binding and is a different kind of thing.</b> That was a
/// platform-side identifier fare-svc needed; this is the driver's own bank account, which they
/// enter and a Verification Officer approves. OnePay has one merchant account per merchant, so the
/// per-driver sub-account D-11 assumed never existed (AL-57).
/// </para>
/// <para>
/// <b>Every rule here is fleet-svc's, deliberately.</b> AL-58 mirrors `registry.fleet_payout_profiles`
/// column for column so an operator reading a fleet's account and a driver's reads the same shape,
/// the officer decides both through the same AL-39 queue, and payout-svc's and subscription-svc's
/// "find the one verified row" reads are the same query with a different owner column.
/// </para>
/// </remarks>
public interface IDriverPayoutProfileService
{
    Task<DriverPayoutProfile> ReadAsync(Guid driverId, CancellationToken cancellationToken);

    Task<DriverPayoutProfile> UpsertAsync(Guid driverId, DriverPayoutDraft draft, CancellationToken cancellationToken);

    /// <summary>Attaches an uploaded document to the driver's pending version.</summary>
    Task<DriverPayoutProfile> AttachAsync(
        Guid driverId, Guid uploadId, string kind, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverPayoutProfileService"/>
internal sealed class DriverPayoutProfileService(
    IUnitOfWorkFactory unitOfWorkFactory,
    INpgsqlConnectionFactory connections,
    IDriverPayoutProfileRepository profiles,
    ILogger<DriverPayoutProfileService> logger) : IDriverPayoutProfileService
{
    /// <summary><c>registry.yaml</c>'s maxLengths on the four fields.</summary>
    private const int MaxBank = 120;
    private const int MaxAccountNo = 40;
    private const int MaxHolderName = 200;

    public async Task<DriverPayoutProfile> ReadAsync(Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await profiles.FindCurrentAsync(connection, null, driverId, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.PayoutProfileNotFound,
                "This driver has not submitted a bank and payout profile yet, so nothing can be paid out to "
                + "them. Earnings accrue on their wallet and are never lost (AL-58).");
    }

    /// <remarks>
    /// <b>BR-31.1 in two halves, and the second is the expensive one.</b> An edit to a *pending*
    /// version updates in place — nothing is being paid against it and nobody has decided on it, so
    /// a correction is a correction, and inserting instead would put a second application for one
    /// driver on the officer's queue for every digit fixed. An edit to a *verified* version INSERTs
    /// and leaves the incumbent verified and payable, so Sunday's sweep still goes to the account an
    /// officer approved rather than to one nobody has seen.
    /// </remarks>
    public async Task<DriverPayoutProfile> UpsertAsync(
        Guid driverId, DriverPayoutDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        Validate(draft);

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var current = await profiles.FindCurrentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, cancellationToken);

        DriverPayoutProfile? saved = null;

        if (current is { IsPending: true })
        {
            saved = await profiles.UpdatePendingAsync(
                unitOfWork, current.Id, draft.Bank, draft.Branch, draft.AccountNo,
                draft.AccountHolderName, cancellationToken);
        }

        // Null covers both "there was no version" and "the officer decided on it between the read
        // and the write" — and the answer to each is the same: a new pending version.
        saved ??= await profiles.InsertPendingAsync(
            unitOfWork, driverId, draft.Bank, draft.Branch, draft.AccountNo, draft.AccountHolderName,
            current?.ProofUploadId, current?.LankaqrUploadId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        if (current?.IsVerified == true)
        {
            logger.LogInformation(
                "Driver {DriverId} edited a verified payout profile; version {ProfileId} is pending and the "
                + "incumbent keeps receiving payouts until an officer approves the change (BR-31.1).",
                driverId,
                saved.Id);
        }

        return saved;
    }

    /// <remarks>
    /// <b>A document is an edit too.</b> Uploading against a verified profile forks a new pending
    /// version carrying the other slot forward — replacing the bank statement behind an approved
    /// account is exactly the change an officer would want to see again.
    /// </remarks>
    public async Task<DriverPayoutProfile> AttachAsync(
        Guid driverId, Guid uploadId, string kind, CancellationToken cancellationToken)
    {
        if (!DriverPayoutDocumentKinds.All.Contains(kind))
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = [$"kind must be one of {string.Join(", ", DriverPayoutDocumentKinds.All.Order(StringComparer.Ordinal))}."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var current = await profiles.FindCurrentAsync(
            unitOfWork.Connection, unitOfWork.Transaction, driverId, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.PayoutProfileNotFound,
                "Submit the bank details first — a document with no account to attach it to is evidence of "
                + "nothing.");

        var target = current.IsPending
            ? current
            : await profiles.InsertPendingAsync(
                unitOfWork, driverId, current.Bank, current.Branch, current.AccountNo,
                current.AccountHolderName, current.ProofUploadId, current.LankaqrUploadId, cancellationToken);

        var saved = await profiles.AttachDocumentAsync(unitOfWork, target.Id, uploadId, kind, cancellationToken)
            ?? throw new MageRideException(
                MageRideErrors.Conflict,
                "This payout profile was decided while the document was uploading. Read it back and try again.");

        await unitOfWork.CommitAsync(cancellationToken);

        return saved;
    }

    private static void Validate(DriverPayoutDraft draft)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        Require(errors, nameof(draft.Bank), draft.Bank, MaxBank);
        Require(errors, nameof(draft.Branch), draft.Branch, MaxBank);
        Require(errors, nameof(draft.AccountNo), draft.AccountNo, MaxAccountNo);
        Require(errors, nameof(draft.AccountHolderName), draft.AccountHolderName, MaxHolderName);

        if (errors.Count > 0)
        {
            throw new MageRideValidationException(errors);
        }
    }

    private static void Require(
        IDictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        var name = char.ToLowerInvariant(field[0]) + field[1..];

        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            errors[name] = [$"{name} is required and must be at most {maxLength} characters."];
        }
    }
}

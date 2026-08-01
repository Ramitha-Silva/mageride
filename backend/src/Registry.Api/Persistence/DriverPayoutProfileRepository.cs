using Dapper;
using MageRide.Registry.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Registry.Persistence;

/// <summary>
/// <c>registry.driver_payout_profiles</c> — where a driver's swept earnings go (AL-58, AL-59).
/// </summary>
/// <remarks>
/// <para>
/// <b>Versioned, never updated in place once verified.</b> Migration 0316 mirrors
/// <c>registry.fleet_payout_profiles</c> exactly and every rule 0313 argued applies here for the
/// same reason: an edit to a verified profile INSERTs a new pending row and leaves the incumbent
/// verified, so a driver who mistypes an account number on Friday is still paid on Sunday against
/// the account an officer approved.
/// </para>
/// <para>
/// <b>This service writes the profile; it does not decide it.</b> The Verification Officer does,
/// through admin-bff's AL-39 queue (C063) — whose routes are subject-agnostic and already take a
/// driver id. Nothing here sets <c>verified</c>.
/// </para>
/// </remarks>
public interface IDriverPayoutProfileRepository
{
    /// <summary>The version a driver is looking at: the newest that is not superseded.</summary>
    Task<DriverPayoutProfile?> FindCurrentAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid driverId, CancellationToken cancellationToken);

    /// <summary>A new pending version, carrying the evidence of the one it replaces.</summary>
    Task<DriverPayoutProfile> InsertPendingAsync(
        IUnitOfWork unitOfWork,
        Guid driverId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        Guid? proofUploadId,
        Guid? lankaqrUploadId,
        CancellationToken cancellationToken);

    /// <summary>Corrects a version nobody has decided on yet. Null when it has since been decided.</summary>
    Task<DriverPayoutProfile?> UpdatePendingAsync(
        IUnitOfWork unitOfWork,
        Guid profileId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        CancellationToken cancellationToken);

    /// <summary>Points a pending version at an uploaded document.</summary>
    Task<DriverPayoutProfile?> AttachDocumentAsync(
        IUnitOfWork unitOfWork, Guid profileId, Guid uploadId, string kind, CancellationToken cancellationToken);

    /// <summary>
    /// The Verification Officer's approval: supersede the incumbent, then verify the replacement.
    /// </summary>
    /// <remarks>
    /// Both statements, one transaction, <b>in this order</b>. <c>ux_driver_payout_verified</c>
    /// admits one verified row per driver, so verifying first fails on the index — which migration
    /// 0316 says out loud on the index's own comment rather than leaving to a 23505. Exactly
    /// fleet-svc's <c>VerifyAsync</c>, for the table C028 mirrored column for column.
    /// </remarks>
    Task<DriverPayoutProfile?> VerifyAsync(
        IUnitOfWork unitOfWork, Guid driverId, Guid officerId, CancellationToken cancellationToken);

    /// <summary>Refuses the pending version and records why. Never touches the incumbent.</summary>
    Task<DriverPayoutProfile?> RejectAsync(
        IUnitOfWork unitOfWork, Guid driverId, Guid officerId, string reason, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDriverPayoutProfileRepository"/>
internal sealed class DriverPayoutProfileRepository : IDriverPayoutProfileRepository
{
    private const string Columns =
        """
        id AS Id, driver_id AS DriverId, bank AS Bank, branch AS Branch,
        account_no AS AccountNo, account_holder_name AS AccountHolderName,
        proof_upload_id AS ProofUploadId, lankaqr_upload_id AS LankaqrUploadId,
        status AS Status, rejection_reason AS RejectionReason,
        verified_by AS VerifiedBy, verified_at AS VerifiedAt
        """;

    /// <remarks>
    /// <c>superseded</c> is excluded: it is the incumbent an approved edit displaced, and showing a
    /// driver the account they used to be paid into would be showing them the wrong answer.
    /// </remarks>
    public async Task<DriverPayoutProfile?> FindCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.QuerySingleOrDefaultAsync<DriverPayoutProfile>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM registry.driver_payout_profiles
              WHERE driver_id = @DriverId AND status <> 'superseded'
              ORDER BY created_at DESC LIMIT 1;
             """,
            new { DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<DriverPayoutProfile> InsertPendingAsync(
        IUnitOfWork unitOfWork,
        Guid driverId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        Guid? proofUploadId,
        Guid? lankaqrUploadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        // The upload ids are carried forward from the version being replaced: a driver correcting a
        // branch name should not have to re-photograph their passbook, and the officer needs
        // evidence attached to the version they are deciding on.
        return await unitOfWork.Connection.QuerySingleAsync<DriverPayoutProfile>(new CommandDefinition(
            $"""
             INSERT INTO registry.driver_payout_profiles
               (driver_id, bank, branch, account_no, account_holder_name, proof_upload_id, lankaqr_upload_id)
             VALUES (@DriverId, @Bank, @Branch, @AccountNo, @AccountHolderName, @ProofUploadId, @LankaqrUploadId)
             RETURNING {Columns};
             """,
            new
            {
                DriverId = driverId,
                Bank = bank,
                Branch = branch,
                AccountNo = accountNo,
                AccountHolderName = accountHolderName,
                ProofUploadId = proofUploadId,
                LankaqrUploadId = lankaqrUploadId,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// Guarded on the status rather than trusting the read that chose this path: between the two an
    /// officer may have decided, and rewriting a verified row's account number in place is the one
    /// thing BR-31.1 exists to prevent. Null means the caller re-reads and inserts a version.
    /// </remarks>
    public async Task<DriverPayoutProfile?> UpdatePendingAsync(
        IUnitOfWork unitOfWork,
        Guid profileId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<DriverPayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.driver_payout_profiles
                SET bank = @Bank, branch = @Branch, account_no = @AccountNo,
                    account_holder_name = @AccountHolderName, rejection_reason = NULL
              WHERE id = @ProfileId AND status = 'pending_verification'
             RETURNING {Columns};
             """,
            new
            {
                ProfileId = profileId,
                Bank = bank,
                Branch = branch,
                AccountNo = accountNo,
                AccountHolderName = accountHolderName,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>bank_statement</c> and <c>passbook_first_page</c> share one column: BR-31.1 asks for one
    /// <em>or</em> the other, and uploading a passbook after a statement replacing it is what
    /// somebody correcting a blurred photograph expects.
    /// </remarks>
    public async Task<DriverPayoutProfile?> AttachDocumentAsync(
        IUnitOfWork unitOfWork, Guid profileId, Guid uploadId, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var column = kind == DriverPayoutDocumentKinds.LankaqrCode ? "lankaqr_upload_id" : "proof_upload_id";

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<DriverPayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.driver_payout_profiles
                SET {column} = @UploadId
              WHERE id = @ProfileId AND status = 'pending_verification'
             RETURNING {Columns};
             """,
            new { ProfileId = profileId, UploadId = uploadId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <para>
    /// <b>Supersede, then verify.</b> The reverse order is refused by
    /// <c>ux_driver_payout_verified</c> the moment a driver approves a second account, which is the
    /// ordinary case rather than an exotic one — a driver changing banks.
    /// </para>
    /// <para>
    /// <b>Approving twice must not re-stamp anything.</b> The verify is guarded on
    /// <c>pending_verification</c>, so a second Approve with nothing pending returns null and the
    /// caller reads that as "already decided" — <c>verified_at</c> and <c>verified_by</c> keep
    /// saying when the decision was actually made and who actually made it. fleet-svc's rule, kept.
    /// </para>
    /// </remarks>
    public async Task<DriverPayoutProfile?> VerifyAsync(
        IUnitOfWork unitOfWork, Guid driverId, Guid officerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.driver_payout_profiles
               SET status = 'superseded'
             WHERE driver_id = @DriverId
               AND status = 'verified'
               AND EXISTS (SELECT 1 FROM registry.driver_payout_profiles pending
                            WHERE pending.driver_id = @DriverId
                              AND pending.status = 'pending_verification');
            """,
            new { DriverId = driverId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<DriverPayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.driver_payout_profiles
                SET status = 'verified', verified_by = @OfficerId, verified_at = now(),
                    rejection_reason = NULL
              WHERE driver_id = @DriverId AND status = 'pending_verification'
             RETURNING {Columns};
             """,
            new { DriverId = driverId, OfficerId = officerId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <b>A rejection never disturbs the incumbent</b> — fleet-svc's rule, and it is about somebody's
    /// wages. A mismatched account-holder name is a reason to refuse the *edit*, not a reason to
    /// stop paying a driver into details an officer already approved. So the predicate names the
    /// pending row only, and the verified one is not in scope of this statement at all.
    /// </remarks>
    public async Task<DriverPayoutProfile?> RejectAsync(
        IUnitOfWork unitOfWork, Guid driverId, Guid officerId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        return await unitOfWork.Connection.QuerySingleOrDefaultAsync<DriverPayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.driver_payout_profiles
                SET status = 'rejected', rejection_reason = @Reason, verified_by = @OfficerId
              WHERE driver_id = @DriverId AND status = 'pending_verification'
             RETURNING {Columns};
             """,
            new { DriverId = driverId, OfficerId = officerId, Reason = reason },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }
}

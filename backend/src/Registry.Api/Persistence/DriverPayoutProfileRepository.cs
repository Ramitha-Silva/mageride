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
}

/// <inheritdoc cref="IDriverPayoutProfileRepository"/>
internal sealed class DriverPayoutProfileRepository : IDriverPayoutProfileRepository
{
    private const string Columns =
        """
        id AS Id, driver_id AS DriverId, bank AS Bank, branch AS Branch,
        account_no AS AccountNo, account_holder_name AS AccountHolderName,
        proof_upload_id AS ProofUploadId, lankaqr_upload_id AS LankaqrUploadId,
        status AS Status, rejection_reason AS RejectionReason, verified_at AS VerifiedAt
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
}

using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>registry.fleet_payout_profiles</c> — the versioned bank profile Mode B money depends on
/// (AL-49, BR-31.1; migrations 0301 and 0313).
/// </summary>
/// <remarks>
/// <para>
/// <b>The table is a version history, not a record per org.</b> BR-31.1: "any edit re-enters
/// <c>pending_verification</c> as a new versioned row — the passenger pay sheet always renders the
/// latest verified row, never unverified edits." So an edit to a <c>verified</c> profile
/// <em>inserts</em>, leaving the incumbent verified and collecting, and the officer's approval is
/// what finally moves the old row to <c>superseded</c>.
/// </para>
/// <para>
/// <b>An edit to a profile that is still pending updates in place.</b> No verified snapshot is at
/// risk, nobody is collecting against the pending row, and inserting instead would put a second
/// application for the same organisation on the officer's queue every time the owner corrected a
/// digit. A version marks a verification decision, not a keystroke.
/// </para>
/// </remarks>
public interface IPayoutProfileRepository
{
    /// <summary>
    /// The version the owner is looking at: the newest row that is not <c>superseded</c>.
    /// </summary>
    /// <remarks>
    /// Not "the verified one". An owner who has just edited a verified profile must see
    /// <c>pending_verification</c> — that is the state of their application — while
    /// subscription-svc goes on reading the verified row for <c>payTo</c>. The two answers differ
    /// on purpose, and that is the whole of BR-31.1's "keep collecting against the last verified
    /// snapshot".
    /// </remarks>
    Task<PayoutProfile?> FindCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken);

    /// <summary>The row subscription-svc's pay sheet reads (<c>payTo</c>), or none.</summary>
    Task<PayoutProfile?> FindVerifiedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken);

    Task<PayoutProfile> InsertPendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        Guid? proofUploadId,
        Guid? lankaqrUploadId,
        CancellationToken cancellationToken);

    /// <summary>Rewrites the pending version in place, clearing any earlier rejection.</summary>
    Task<PayoutProfile> UpdatePendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        CancellationToken cancellationToken);

    /// <summary>Attaches an uploaded document to a version (AL-49's two evidence slots).</summary>
    Task<PayoutProfile> AttachDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        Guid uploadId,
        bool isLankaQr,
        CancellationToken cancellationToken);

    /// <summary>
    /// The officer's approval: supersede the incumbent, then verify the replacement.
    /// </summary>
    /// <remarks>
    /// Both statements, one transaction, in this order. <c>ux_payout_profile_verified</c> admits a
    /// single verified row per org, so verifying first would fail on the index — which migration
    /// 0313 says out loud on the index's own comment rather than leaving to a 23505.
    /// </remarks>
    Task<PayoutProfile?> VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid profileId,
        Guid officerId,
        CancellationToken cancellationToken);

    Task<PayoutProfile?> RejectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        Guid officerId,
        string reason,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPayoutProfileRepository"/>
internal sealed class PayoutProfileRepository : IPayoutProfileRepository
{
    private const string Columns = """
        id, fleet_id, bank, branch, account_no, account_holder_name,
        proof_upload_id, lankaqr_upload_id, status, rejection_reason,
        verified_by, verified_at, created_at, updated_at
        """;

    public async Task<PayoutProfile?> FindCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `created_at DESC, id DESC`: two versions can share a millisecond if an edit and its
        // document upload land together, and the id tie-break keeps the answer stable rather than
        // whichever row the plan happened to reach first.
        return await connection.QuerySingleOrDefaultAsync<PayoutProfile>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM registry.fleet_payout_profiles
              WHERE fleet_id = @FleetId AND status <> 'superseded'
              ORDER BY created_at DESC, id DESC
              LIMIT 1;
             """,
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile?> FindVerifiedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // No ORDER BY and no LIMIT: ux_payout_profile_verified makes at most one row match, and
        // writing it as "the newest verified" would suggest there could be more than one.
        return await connection.QuerySingleOrDefaultAsync<PayoutProfile>(new CommandDefinition(
            $"SELECT {Columns} FROM registry.fleet_payout_profiles WHERE fleet_id = @FleetId AND status = 'verified';",
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile> InsertPendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        Guid? proofUploadId,
        Guid? lankaqrUploadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The upload ids are carried forward by the caller from the version being replaced: an
        // owner correcting a branch name should not have to re-photograph their passbook, and the
        // officer needs evidence attached to the version they are deciding on.
        return await connection.QuerySingleAsync<PayoutProfile>(new CommandDefinition(
            $"""
             INSERT INTO registry.fleet_payout_profiles
               (fleet_id, bank, branch, account_no, account_holder_name, proof_upload_id, lankaqr_upload_id)
             VALUES (@FleetId, @Bank, @Branch, @AccountNo, @AccountHolderName, @ProofUploadId, @LankaqrUploadId)
             RETURNING {Columns};
             """,
            new
            {
                FleetId = fleetId,
                Bank = bank,
                Branch = branch,
                AccountNo = accountNo,
                AccountHolderName = accountHolderName,
                ProofUploadId = proofUploadId,
                LankaqrUploadId = lankaqrUploadId,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile> UpdatePendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        string bank,
        string branch,
        string accountNo,
        string accountHolderName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Guarded on the status rather than trusting the read that chose this path: between the
        // two an officer may have decided, and rewriting a verified row's account number in place
        // is the one thing BR-31.1 exists to prevent. Zero rows means the caller re-reads and
        // inserts a new version instead.
        return await connection.QuerySingleAsync<PayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.fleet_payout_profiles
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
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile> AttachDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        Guid uploadId,
        bool isLankaQr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // One statement for both slots. `bank_statement` and `passbook_first_page` share
        // proof_upload_id because BR-31.1 asks for one or the other, not both — uploading a
        // passbook after a statement replaces it, which is what an owner correcting a blurred
        // photograph expects.
        return await connection.QuerySingleAsync<PayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.fleet_payout_profiles
                SET proof_upload_id   = CASE WHEN @IsLankaQr THEN proof_upload_id   ELSE @UploadId END,
                    lankaqr_upload_id = CASE WHEN @IsLankaQr THEN @UploadId         ELSE lankaqr_upload_id END
              WHERE id = @ProfileId
             RETURNING {Columns};
             """,
            new { ProfileId = profileId, UploadId = uploadId, IsLankaQr = isLankaQr },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile?> VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid profileId,
        Guid officerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE registry.fleet_payout_profiles
               SET status = 'superseded'
             WHERE fleet_id = @FleetId AND status = 'verified' AND id <> @ProfileId;
            """,
            new { FleetId = fleetId, ProfileId = profileId },
            transaction,
            cancellationToken: cancellationToken));

        // `verified_at` is now(), not a value from this process: it is the instant the money
        // routing changed, and every other timestamp on the row comes from the database.
        return await connection.QuerySingleOrDefaultAsync<PayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.fleet_payout_profiles
                SET status = 'verified', verified_by = @OfficerId, verified_at = now(), rejection_reason = NULL
              WHERE id = @ProfileId AND fleet_id = @FleetId AND status = 'pending_verification'
             RETURNING {Columns};
             """,
            new { ProfileId = profileId, FleetId = fleetId, OfficerId = officerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutProfile?> RejectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid profileId,
        Guid officerId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // A rejection never touches an incumbent verified row. BR-31.1's mismatched
        // account-holder name is a reason to refuse the *edit*, not a reason to stop the
        // organisation collecting against details an officer already approved.
        return await connection.QuerySingleOrDefaultAsync<PayoutProfile>(new CommandDefinition(
            $"""
             UPDATE registry.fleet_payout_profiles
                SET status = 'rejected', rejection_reason = @Reason, verified_by = @OfficerId, verified_at = now()
              WHERE id = @ProfileId AND status = 'pending_verification'
             RETURNING {Columns};
             """,
            new { ProfileId = profileId, OfficerId = officerId, Reason = reason },
            transaction,
            cancellationToken: cancellationToken));
    }
}

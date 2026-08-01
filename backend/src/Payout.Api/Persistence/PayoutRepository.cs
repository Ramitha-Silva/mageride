using Dapper;
using MageRide.Payout.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Payout.Persistence;

/// <summary>
/// <c>billing.payout_batches</c>, <c>billing.payouts</c>, and the two reads that decide who is swept.
/// </summary>
/// <remarks>
/// <b>Every write here is idempotent by index, not by check-then-act.</b> A batch collides on
/// <c>run_date</c>, an instruction on <c>ux_payouts_batch_driver</c>, and a bank result on
/// <c>ux_payouts_provider_ref</c> — so a re-run, a second replica and a redelivered callback all
/// resolve to the same rows without a lease anywhere. That is what lets the runner have no lock:
/// a lock would protect operations that are already idempotent and would introduce a way for
/// payouts to stop entirely when its holder dies badly.
/// </remarks>
internal interface IPayoutRepository
{
    /// <summary>Opens the batch for a Colombo business date, or returns the one already there.</summary>
    /// <returns>The batch, and whether this call created it.</returns>
    Task<(PayoutBatch Batch, bool Created)> OpenBatchAsync(
        DateOnly runDate, DateTimeOffset tzAt, CancellationToken cancellationToken);

    /// <summary>
    /// Drivers with a <c>verified</c> payout profile and something left to sweep.
    /// </summary>
    /// <remarks>
    /// The join <em>is</em> the AL-58 fence: a driver with no verified profile cannot appear, so
    /// they are never swept and their balance is never touched. Ordered by id so two replicas
    /// running the same sweep walk the same list and collide on the same rows.
    /// </remarks>
    Task<IReadOnlyList<EligibleDriver>> EligibleDriversAsync(
        Guid batchId, long retainMinor, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Records one instruction. <see langword="false"/> when the batch already had one for the driver.
    /// </summary>
    Task<bool> InsertInstructionAsync(
        Guid payoutId,
        Guid batchId,
        Guid driverId,
        Guid payoutProfileId,
        long amountMinor,
        Guid journalEntryId,
        CancellationToken cancellationToken);

    Task CompleteBatchAsync(Guid batchId, string status, CancellationToken cancellationToken);

    Task<PayoutInstruction?> FindAsync(Guid payoutId, CancellationToken cancellationToken);

    /// <summary>Claims an instruction for submission. False when somebody else already moved it.</summary>
    Task<bool> MarkSubmittedAsync(Guid payoutId, string providerReference, CancellationToken cancellationToken);

    /// <summary>
    /// Records a terminal outcome, guarded on the state it was resolved from.
    /// </summary>
    /// <returns><see langword="false"/> when the row had already left that state — a redelivery.</returns>
    Task<bool> SettleAsync(
        Guid payoutId, string from, string to, string? failureReason, CancellationToken cancellationToken);

    Task<IReadOnlyList<PayoutInstruction>> ListForDriverAsync(
        Guid driverId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<PayoutInstruction>> ListAsync(
        Guid? batchId, string? status, Guid? driverId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<PayoutBatch>> ListBatchesAsync(int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPayoutRepository"/>
internal sealed class PayoutRepository(INpgsqlConnectionFactory connections) : IPayoutRepository
{
    private const string BatchColumns =
        """
        id AS Id, run_date AS RunDate, tz_at AS TzAt, status AS Status,
        instruction_count AS InstructionCount, total_minor AS TotalMinor,
        started_at AS StartedAt, completed_at AS CompletedAt
        """;

    private const string InstructionColumns =
        """
        p.id AS Id, p.batch_id AS BatchId, p.driver_id AS DriverId,
        p.payout_profile_id AS PayoutProfileId, p.amount_minor AS AmountMinor,
        p.status AS Status, p.failure_reason AS FailureReason,
        p.provider_reference AS ProviderReference, p.journal_entry_id AS JournalEntryId,
        -- Never the whole account number: an operator reconciling a bank statement needs to
        -- recognise the account, not to be handed it (D-36's spirit, applied to a read model).
        ('****' || right(pr.account_no, 4)) AS AccountNoMasked,
        p.created_at AS CreatedAt, p.updated_at AS UpdatedAt
        """;

    private const string InstructionFrom =
        """
          FROM billing.payouts p
          JOIN registry.driver_payout_profiles pr ON pr.id = p.payout_profile_id
        """;

    public async Task<(PayoutBatch Batch, bool Created)> OpenBatchAsync(
        DateOnly runDate, DateTimeOffset tzAt, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // INSERT … ON CONFLICT DO NOTHING then read: `run_date` UNIQUE is the arbiter, so two
        // replicas waking in the same minute cannot open two batches for one Colombo day.
        // RETURNING alone would give the loser of the race nothing.
        var created = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO billing.payout_batches (run_date, tz_at)
            VALUES (@RunDate, @TzAt)
            ON CONFLICT (run_date) DO NOTHING;
            """,
            new { RunDate = runDate, TzAt = tzAt },
            cancellationToken: cancellationToken));

        var batch = await connection.QuerySingleAsync<PayoutBatch>(new CommandDefinition(
            $"SELECT {BatchColumns} FROM billing.payout_batches WHERE run_date = @RunDate;",
            new { RunDate = runDate },
            cancellationToken: cancellationToken));

        return (batch, created > 0);
    }

    public async Task<IReadOnlyList<EligibleDriver>> EligibleDriversAsync(
        Guid batchId, long retainMinor, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<EligibleDriver>(new CommandDefinition(
            """
            SELECT a.owner_id           AS DriverId,
                   pr.id                AS PayoutProfileId,
                   (a.balance_minor - @RetainMinor) AS BalanceMinor,
                   pr.account_no        AS AccountNo
              FROM billing.accounts a
              JOIN registry.driver_payout_profiles pr
                ON pr.driver_id = a.owner_id AND pr.status = 'verified'
             WHERE a.owner_type = 'driver'
               AND a.currency = 'LKR'
               AND a.balance_minor > @RetainMinor
               -- Already swept in this batch: a re-run walks the same list and skips what landed.
               AND NOT EXISTS (SELECT 1 FROM billing.payouts p
                                WHERE p.batch_id = @BatchId AND p.driver_id = a.owner_id)
             ORDER BY a.owner_id
             LIMIT @Limit;
            """,
            new { BatchId = batchId, RetainMinor = retainMinor, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<bool> InsertInstructionAsync(
        Guid payoutId,
        Guid batchId,
        Guid driverId,
        Guid payoutProfileId,
        long amountMinor,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO billing.payouts
                (id, batch_id, driver_id, payout_profile_id, amount_minor, journal_entry_id)
            VALUES (@Id, @BatchId, @DriverId, @ProfileId, @AmountMinor, @EntryId)
            ON CONFLICT (batch_id, driver_id) DO NOTHING;
            """,
            new
            {
                Id = payoutId,
                BatchId = batchId,
                DriverId = driverId,
                ProfileId = payoutProfileId,
                AmountMinor = amountMinor,
                EntryId = journalEntryId,
            },
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    /// <remarks>
    /// The totals are recomputed from the rows rather than accumulated by the runner: a sweep that
    /// resumed after a crash would otherwise report only what its second half moved.
    /// </remarks>
    public async Task CompleteBatchAsync(Guid batchId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE billing.payout_batches b
               SET status = @Status,
                   completed_at = now(),
                   instruction_count = totals.count,
                   total_minor = totals.sum
              FROM (SELECT count(*)::int AS count, coalesce(sum(amount_minor), 0) AS sum
                      FROM billing.payouts WHERE batch_id = @Id) totals
             WHERE b.id = @Id;
            """,
            new { Id = batchId, Status = status },
            cancellationToken: cancellationToken));
    }

    public async Task<PayoutInstruction?> FindAsync(Guid payoutId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<PayoutInstruction>(new CommandDefinition(
            $"SELECT {InstructionColumns} {InstructionFrom} WHERE p.id = @Id;",
            new { Id = payoutId },
            cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>ux_payouts_provider_ref</c> is what makes a redelivered submission a no-op rather than a
    /// second reference on one instruction. The guarded <c>WHERE status = 'PENDING'</c> is the
    /// claim: two replicas submitting the same row, one wins.
    /// </remarks>
    public async Task<bool> MarkSubmittedAsync(
        Guid payoutId, string providerReference, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE billing.payouts
               SET status = 'SUBMITTED', provider_reference = @Reference
             WHERE id = @Id AND status = 'PENDING';
            """,
            new { Id = payoutId, Reference = providerReference },
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    public async Task<bool> SettleAsync(
        Guid payoutId, string from, string to, string? failureReason, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE billing.payouts
               SET status = @To, failure_reason = @Reason
             WHERE id = @Id AND status = @From;
            """,
            new { Id = payoutId, From = from, To = to, Reason = failureReason },
            cancellationToken: cancellationToken));

        return rows > 0;
    }

    public async Task<IReadOnlyList<PayoutInstruction>> ListForDriverAsync(
        Guid driverId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PayoutInstruction>(new CommandDefinition(
            $"""
             SELECT {InstructionColumns} {InstructionFrom}
              WHERE p.driver_id = @DriverId
              ORDER BY p.created_at DESC
              LIMIT @Limit;
             """,
            new { DriverId = driverId, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<PayoutInstruction>> ListAsync(
        Guid? batchId, string? status, Guid? driverId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PayoutInstruction>(new CommandDefinition(
            $"""
             SELECT {InstructionColumns} {InstructionFrom}
              WHERE (@BatchId::uuid  IS NULL OR p.batch_id  = @BatchId)
                AND (@Status::text   IS NULL OR p.status    = @Status)
                AND (@DriverId::uuid IS NULL OR p.driver_id = @DriverId)
              ORDER BY p.created_at DESC
              LIMIT @Limit;
             """,
            new { BatchId = batchId, Status = status, DriverId = driverId, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<PayoutBatch>> ListBatchesAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<PayoutBatch>(new CommandDefinition(
            $"SELECT {BatchColumns} FROM billing.payout_batches ORDER BY run_date DESC LIMIT @Limit;",
            new { Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

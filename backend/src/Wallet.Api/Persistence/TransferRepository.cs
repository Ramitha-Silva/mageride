using Dapper;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Wallet.Persistence;

/// <summary><c>billing.credit_transfers.status</c> (migration 1105).</summary>
internal static class TransferStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

/// <summary>
/// <c>billing.credit_transfers.direction</c>: who started it.
/// </summary>
/// <remarks>
/// <c>DIRECT</c> is a proactive send (US-9A.12) and is <c>APPROVED</c> the moment it is created — the
/// sender is the one acting, so there is nobody left to approve. <c>REQUESTED</c> starts
/// <c>PENDING</c> and waits on the holder (US-9.10 → US-9.13).
/// </remarks>
internal static class TransferDirections
{
    public const string Requested = "REQUESTED";
    public const string Direct = "DIRECT";
}

/// <summary>One credit transfer, with the counterparty's name for the history screen.</summary>
internal sealed record TransferRow(
    Guid Id,
    Guid SenderDriverId,
    Guid RecipientDriverId,
    long AmountMinor,
    string Currency,
    string Direction,
    string Status,
    Guid? JournalEntryId,
    DateTimeOffset CreatedAt,
    string? SenderName,
    string? RecipientName);

/// <summary><c>billing.credit_transfers</c> — the AL-01 driver-to-driver ledger.</summary>
internal interface ITransferRepository
{
    /// <summary>
    /// Creates a transfer row inside the caller's transaction (a <c>DIRECT</c> send, already approved).
    /// </summary>
    Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        Guid senderDriverId,
        Guid recipientDriverId,
        long amountMinor,
        string direction,
        string status,
        Guid? journalEntryId,
        CancellationToken cancellationToken);

    /// <summary>Creates a <c>PENDING</c> request. Its own transaction — no money moves (US-9.10).</summary>
    Task<TransferRow> CreateRequestAsync(
        Guid senderDriverId, Guid recipientDriverId, long amountMinor, CancellationToken cancellationToken);

    Task<TransferRow?> ReadAsync(Guid transferId, CancellationToken cancellationToken);

    /// <summary>
    /// Claims a pending request for approval, inside the ledger's transaction: returns false when it
    /// was no longer <c>PENDING</c>.
    /// </summary>
    Task<bool> TryApproveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid transferId,
        Guid journalEntryId,
        CancellationToken cancellationToken);

    /// <summary>Rejects a pending request (US-9.12). Nothing is posted.</summary>
    Task<bool> TryRejectAsync(Guid transferId, CancellationToken cancellationToken);

    /// <summary>One page of a driver's transfers, either direction, newest first (US-9A.11).</summary>
    Task<IReadOnlyList<TransferRow>> ReadForDriverAsync(
        Guid driverId,
        string direction,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The holder's approval inbox (US-9A.10).</summary>
    Task<IReadOnlyList<TransferRow>> ReadPendingForHolderAsync(
        Guid holderDriverId,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITransferRepository"/>
internal sealed class TransferRepository(INpgsqlConnectionFactory connections) : ITransferRepository
{
    /// <remarks>
    /// The two names come from <c>registry.driver_profiles</c> with <c>iam.users.first_name</c> as the
    /// fallback: US-9A.11's history line says who, and a driver who has not completed Profile Setup
    /// still has an account name. Neither is a PII disclosure beyond what the transfer itself is — you
    /// can only see rows you are a party to.
    /// </remarks>
    private const string Columns =
        """
        t.id, t.sender_driver_id, t.recipient_driver_id, t.amount_minor, t.currency,
        t.direction, t.status, t.journal_entry_id, t.created_at,
        coalesce(sp.display_name, su.first_name) AS sender_name,
        coalesce(rp.display_name, ru.first_name) AS recipient_name
        """;

    private const string Joins =
        """
        FROM billing.credit_transfers t
        LEFT JOIN iam.users su ON su.id = t.sender_driver_id
        LEFT JOIN iam.users ru ON ru.id = t.recipient_driver_id
        LEFT JOIN registry.driver_profiles sp ON sp.driver_id = t.sender_driver_id
        LEFT JOIN registry.driver_profiles rp ON rp.driver_id = t.recipient_driver_id
        """;

    public Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        Guid senderDriverId,
        Guid recipientDriverId,
        long amountMinor,
        string direction,
        string status,
        Guid? journalEntryId,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.credit_transfers
                      (id, sender_driver_id, recipient_driver_id, amount_minor, direction, status,
                       journal_entry_id)
                VALUES (@Id, @SenderDriverId, @RecipientDriverId, @AmountMinor, @Direction, @Status,
                        @JournalEntryId);
                """,
                new
                {
                    Id = id,
                    SenderDriverId = senderDriverId,
                    RecipientDriverId = recipientDriverId,
                    AmountMinor = amountMinor,
                    Direction = direction,
                    Status = status,
                    JournalEntryId = journalEntryId,
                },
                transaction,
                cancellationToken: cancellationToken));

    public async Task<TransferRow> CreateRequestAsync(
        Guid senderDriverId, Guid recipientDriverId, long amountMinor, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var id = await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO billing.credit_transfers
                      (sender_driver_id, recipient_driver_id, amount_minor, direction, status)
                VALUES (@SenderDriverId, @RecipientDriverId, @AmountMinor, 'REQUESTED', 'PENDING')
                RETURNING id;
                """,
                new
                {
                    SenderDriverId = senderDriverId,
                    RecipientDriverId = recipientDriverId,
                    AmountMinor = amountMinor,
                },
                cancellationToken: cancellationToken));

        return await ReadAsync(id, cancellationToken)
               ?? throw new InvalidOperationException($"Credit transfer {id} vanished after insert.");
    }

    public async Task<TransferRow?> ReadAsync(Guid transferId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<TransferRow?>(
            new CommandDefinition(
                $"SELECT {Columns} {Joins} WHERE t.id = @TransferId;",
                new { TransferId = transferId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// The <c>status = 'PENDING'</c> predicate is the claim, so two taps on Approve post one entry and
    /// the second reports a conflict rather than moving money twice.
    /// </remarks>
    public async Task<bool> TryApproveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid transferId,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.credit_transfers
                   SET status = 'APPROVED', journal_entry_id = @JournalEntryId
                 WHERE id = @TransferId AND status = 'PENDING';
                """,
                new { TransferId = transferId, JournalEntryId = journalEntryId },
                transaction,
                cancellationToken: cancellationToken));

        return rows == 1;
    }

    public async Task<bool> TryRejectAsync(Guid transferId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.credit_transfers
                   SET status = 'REJECTED'
                 WHERE id = @TransferId AND status = 'PENDING';
                """,
                new { TransferId = transferId },
                cancellationToken: cancellationToken));

        return rows == 1;
    }

    public async Task<IReadOnlyList<TransferRow>> ReadForDriverAsync(
        Guid driverId,
        string direction,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // One statement for all three directions: `sent`, `received` and `all` differ only in which
        // side of the row the caller has to be on, and two SQL strings would be two plans to keep in
        // step. Keyset on (created_at, id) — a batch of transfers can share a microsecond.
        var rows = await connection.QueryAsync<TransferRow>(
            new CommandDefinition(
                $"""
                SELECT {Columns} {Joins}
                 WHERE ((@Direction = 'all' AND (t.sender_driver_id = @DriverId OR t.recipient_driver_id = @DriverId))
                     OR (@Direction = 'sent' AND t.sender_driver_id = @DriverId)
                     OR (@Direction = 'received' AND t.recipient_driver_id = @DriverId))
                   AND (@Before::timestamptz IS NULL
                        OR t.created_at < @Before
                        OR (t.created_at = @Before AND t.id < @BeforeId))
                 ORDER BY t.created_at DESC, t.id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    DriverId = driverId,
                    Direction = direction,
                    Before = before,
                    BeforeId = beforeId ?? Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<TransferRow>> ReadPendingForHolderAsync(
        Guid holderDriverId,
        DateTimeOffset? before,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // `ix_credit_transfers_pending` (1105) is exactly this query: the holder's inbox.
        var rows = await connection.QueryAsync<TransferRow>(
            new CommandDefinition(
                $"""
                SELECT {Columns} {Joins}
                 WHERE t.sender_driver_id = @HolderDriverId AND t.status = 'PENDING'
                   AND (@Before::timestamptz IS NULL
                        OR t.created_at < @Before
                        OR (t.created_at = @Before AND t.id < @BeforeId))
                 ORDER BY t.created_at DESC, t.id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    HolderDriverId = holderDriverId,
                    Before = before,
                    BeforeId = beforeId ?? Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}

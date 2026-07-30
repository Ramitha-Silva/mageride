using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using MageRide.Wallet.Ledger;
using Npgsql;

namespace MageRide.Wallet.Persistence;

/// <summary>An entry read back for a replay, with the per-account legs it posted.</summary>
internal sealed record ExistingEntry(Guid EntryId, string Kind, IReadOnlyList<PostedLeg> Legs);

/// <summary>One line of the driver's wallet history (US-9A.19).</summary>
internal sealed record WalletTransactionRow(
    long Id,
    Guid EntryId,
    string Kind,
    long AmountMinor,
    long BalanceAfterMinor,
    string? Description,
    DateTimeOffset Ts);

/// <summary>
/// <c>billing.journal_entries</c>, <c>journal_postings</c>, <c>wallet_transactions</c> and this
/// plane's outbox.
/// </summary>
internal interface ILedgerRepository
{
    /// <summary>
    /// Claims the idempotency key and returns the new entry id, or <see langword="null"/> when the key
    /// is already in use.
    /// </summary>
    Task<Guid?> TryCreateEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string kind,
        string idempotencyKey,
        string? description,
        CancellationToken cancellationToken);

    Task AddPostingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid entryId,
        Guid accountId,
        long amountMinor,
        CancellationToken cancellationToken);

    Task AddTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid entryId,
        string kind,
        long amountMinor,
        long balanceAfterMinor,
        string? description,
        CancellationToken cancellationToken);

    /// <summary>Queues one event for the kernel's LISTEN/NOTIFY dispatcher (E-09, R-13).</summary>
    Task AddOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid aggregateId,
        string eventType,
        object payload,
        CancellationToken cancellationToken);

    /// <summary>The entry a previous attempt with this key wrote, or <see langword="null"/>.</summary>
    Task<ExistingEntry?> ReadEntryByKeyAsync(
        NpgsqlConnection connection, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>One page of an account's history, newest first.</summary>
    Task<IReadOnlyList<WalletTransactionRow>> ReadTransactionsAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset? before,
        long? beforeId,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILedgerRepository"/>
internal sealed class LedgerRepository(INpgsqlConnectionFactory connections) : ILedgerRepository
{
    public Task<Guid?> TryCreateEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string kind,
        string idempotencyKey,
        string? description,
        CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                """
                INSERT INTO billing.journal_entries (kind, idempotency_key, description)
                VALUES (@Kind, @IdempotencyKey, @Description)
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING id;
                """,
                new { Kind = kind, IdempotencyKey = idempotencyKey, Description = description },
                transaction,
                cancellationToken: cancellationToken));

    public Task AddPostingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid entryId,
        Guid accountId,
        long amountMinor,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.journal_postings (entry_id, account_id, amount_minor)
                VALUES (@EntryId, @AccountId, @AmountMinor);
                """,
                new { EntryId = entryId, AccountId = accountId, AmountMinor = amountMinor },
                transaction,
                cancellationToken: cancellationToken));

    /// <remarks>
    /// <c>ON CONFLICT (account_id, entry_id) DO NOTHING</c> against <c>ux_wallet_tx_account_entry</c>:
    /// C005 added that index because the ledger event stream is at-least-once, and the same reasoning
    /// applies to a retried write from inside this service — a second history line for one entry would
    /// make a driver's statement disagree with their balance.
    /// </remarks>
    public Task AddTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        Guid entryId,
        string kind,
        long amountMinor,
        long balanceAfterMinor,
        string? description,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.wallet_transactions
                      (account_id, entry_id, kind, amount_minor, balance_after_minor, description)
                VALUES (@AccountId, @EntryId, @Kind, @AmountMinor, @BalanceAfterMinor, @Description)
                ON CONFLICT (account_id, entry_id) DO NOTHING;
                """,
                new
                {
                    AccountId = accountId,
                    EntryId = entryId,
                    Kind = kind,
                    AmountMinor = amountMinor,
                    BalanceAfterMinor = balanceAfterMinor,
                    Description = description,
                },
                transaction,
                cancellationToken: cancellationToken));

    public Task AddOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid aggregateId,
        string eventType,
        object payload,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.outbox (aggregate_id, event_type, payload)
                VALUES (@AggregateId, @EventType, @Payload::jsonb);
                """,
                new
                {
                    AggregateId = aggregateId,
                    EventType = eventType,
                    Payload = JsonSerializer.Serialize(payload, MageRideJson.Options),
                },
                transaction,
                cancellationToken: cancellationToken));

    public async Task<ExistingEntry?> ReadEntryByKeyAsync(
        NpgsqlConnection connection, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<(
            Guid EntryId, string Kind, Guid AccountId, Guid? OwnerId, long AmountMinor, long? BalanceAfterMinor)>(
            new CommandDefinition(
                """
                SELECT e.id AS entry_id,
                       e.kind,
                       p.account_id,
                       a.owner_id,
                       sum(p.amount_minor)::bigint AS amount_minor,   -- sum(bigint) is numeric
                       max(t.balance_after_minor) AS balance_after_minor
                  FROM billing.journal_entries e
                  JOIN billing.journal_postings p ON p.entry_id = e.id
                  JOIN billing.accounts a ON a.id = p.account_id
                  LEFT JOIN billing.wallet_transactions t
                         ON t.entry_id = e.id AND t.account_id = p.account_id
                 WHERE e.idempotency_key = @IdempotencyKey
                 GROUP BY e.id, e.kind, p.account_id, a.owner_id;
                """,
                new { IdempotencyKey = idempotencyKey },
                cancellationToken: cancellationToken));

        var materialised = rows.ToArray();

        if (materialised.Length == 0)
        {
            return null;
        }

        return new ExistingEntry(
            materialised[0].EntryId,
            materialised[0].Kind,
            [
                .. materialised.Select(row => new PostedLeg(
                    row.AccountId,
                    row.OwnerId,
                    row.AmountMinor,
                    // A platform-side leg has no history line, so no balance was projected for it. The
                    // replaying caller only ever asks about its own wallet.
                    row.BalanceAfterMinor ?? 0)),
            ]);
    }

    /// <remarks>
    /// Keyset on <c>(ts, id)</c>. Two entries can land in the same microsecond — a transfer posts both
    /// legs at once and a batch settlement posts many — and a cursor on the timestamp alone would skip
    /// or repeat rows. The same rule query-svc's trip history follows.
    /// </remarks>
    public async Task<IReadOnlyList<WalletTransactionRow>> ReadTransactionsAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset? before,
        long? beforeId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<WalletTransactionRow>(
            new CommandDefinition(
                """
                SELECT id, entry_id, kind, amount_minor, balance_after_minor, description, ts
                  FROM billing.wallet_transactions
                 WHERE account_id = @AccountId
                   AND (@From::timestamptz IS NULL OR ts >= @From)
                   AND (@To::timestamptz   IS NULL OR ts <  @To)
                   AND (@Before::timestamptz IS NULL
                        OR ts < @Before
                        OR (ts = @Before AND id < @BeforeId))
                 ORDER BY ts DESC, id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    AccountId = accountId,
                    From = from,
                    To = to,
                    Before = before,
                    BeforeId = beforeId ?? long.MaxValue,
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}

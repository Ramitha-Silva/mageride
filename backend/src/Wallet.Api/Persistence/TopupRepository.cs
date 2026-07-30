using Dapper;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Wallet.Persistence;

/// <summary>The three states <c>wallet.yaml</c>'s <c>Topup.state</c> prints (migration 1107).</summary>
internal static class TopupStates
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>The two top-up rails AL-05 leaves (D6' §7.1/§7.2).</summary>
internal static class TopupMethods
{
    /// <summary>Card and OnePay wallet — one rail, one route (D6' §7.1).</summary>
    public const string Onepay = "onepay";

    /// <summary>The bank-app deep link, with the QR as fallback (AL-15).</summary>
    public const string LankaQr = "lankaqr";
}

/// <summary>One gateway top-up session.</summary>
internal sealed record TopupRow(
    Guid Id,
    Guid DriverId,
    Guid AccountId,
    string Method,
    long AmountMinor,
    string Currency,
    string State,
    string? ProviderOrderId,
    string? ProviderTransactionId,
    Guid? JournalEntryId,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt);

/// <summary><c>billing.topups</c> (migration 1107).</summary>
internal interface ITopupRepository
{
    Task<TopupRow> CreateAsync(
        Guid driverId,
        Guid accountId,
        string method,
        long amountMinor,
        string providerOrderId,
        CancellationToken cancellationToken);

    Task<TopupRow?> ReadAsync(Guid topupId, CancellationToken cancellationToken);

    /// <summary>Finds the session a callback is about, by our order id or by the top-up id it echoed.</summary>
    Task<TopupRow?> ResolveAsync(
        Guid? topupId, string? providerOrderId, CancellationToken cancellationToken);

    /// <summary>The session that already claimed this provider transaction, if any (R-19).</summary>
    Task<TopupRow?> ReadByProviderTransactionAsync(
        string providerTransactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a pending session settled, inside the caller's transaction. Returns false when it was
    /// not <c>Pending</c> any more.
    /// </summary>
    Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid topupId,
        string providerTransactionId,
        Guid journalEntryId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken);

    /// <summary>Marks a pending session failed. Its own transaction — no money is involved.</summary>
    Task<bool> TryFailAsync(
        Guid topupId,
        string? providerTransactionId,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITopupRepository"/>
internal sealed class TopupRepository(INpgsqlConnectionFactory connections) : ITopupRepository
{
    private const string Columns =
        """
        id, driver_id, account_id, method, amount_minor, currency, state,
        provider_order_id, provider_transaction_id, journal_entry_id, failure_reason,
        created_at, settled_at
        """;

    public async Task<TopupRow> CreateAsync(
        Guid driverId,
        Guid accountId,
        string method,
        long amountMinor,
        string providerOrderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<TopupRow>(
            new CommandDefinition(
                $"""
                INSERT INTO billing.topups
                      (driver_id, account_id, method, amount_minor, provider_order_id)
                VALUES (@DriverId, @AccountId, @Method, @AmountMinor, @ProviderOrderId)
                RETURNING {Columns};
                """,
                new
                {
                    DriverId = driverId,
                    AccountId = accountId,
                    Method = method,
                    AmountMinor = amountMinor,
                    ProviderOrderId = providerOrderId,
                },
                cancellationToken: cancellationToken));
    }

    public async Task<TopupRow?> ReadAsync(Guid topupId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<TopupRow?>(
            new CommandDefinition(
                $"SELECT {Columns} FROM billing.topups WHERE id = @TopupId;",
                new { TopupId = topupId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// Two ways in, because D6' §7.1's callback body carries <c>orderId</c> and the contract's
    /// <c>TopupCallback</c> makes <c>topupId</c> optional: a provider that echoes only its own order
    /// reference still has to be able to find the session it is confirming.
    /// </remarks>
    public async Task<TopupRow?> ResolveAsync(
        Guid? topupId, string? providerOrderId, CancellationToken cancellationToken)
    {
        if (topupId is null && string.IsNullOrWhiteSpace(providerOrderId))
        {
            return null;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<TopupRow?>(
            new CommandDefinition(
                $"""
                SELECT {Columns}
                  FROM billing.topups
                 WHERE (@TopupId::uuid IS NOT NULL AND id = @TopupId)
                    OR (@TopupId::uuid IS NULL AND provider_order_id = @ProviderOrderId);
                """,
                new { TopupId = topupId, ProviderOrderId = providerOrderId },
                cancellationToken: cancellationToken));
    }

    public async Task<TopupRow?> ReadByProviderTransactionAsync(
        string providerTransactionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerTransactionId);

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<TopupRow?>(
            new CommandDefinition(
                $"SELECT {Columns} FROM billing.topups WHERE provider_transaction_id = @ProviderTransactionId;",
                new { ProviderTransactionId = providerTransactionId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// The <c>state = 'Pending'</c> predicate is the claim: two callbacks arriving at once settle the
    /// session once, and the loser writes nothing. It runs in the ledger's transaction, so the money
    /// and the settlement commit together.
    /// </remarks>
    public async Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid topupId,
        string providerTransactionId,
        Guid journalEntryId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken)
    {
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.topups
                   SET state = 'Succeeded',
                       provider_transaction_id = @ProviderTransactionId,
                       journal_entry_id = @JournalEntryId,
                       settled_at = @SettledAt
                 WHERE id = @TopupId AND state = 'Pending';
                """,
                new
                {
                    TopupId = topupId,
                    ProviderTransactionId = providerTransactionId,
                    JournalEntryId = journalEntryId,
                    SettledAt = settledAt,
                },
                transaction,
                cancellationToken: cancellationToken));

        return rows == 1;
    }

    public async Task<bool> TryFailAsync(
        Guid topupId,
        string? providerTransactionId,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.topups
                   SET state = 'Failed',
                       provider_transaction_id = coalesce(@ProviderTransactionId, provider_transaction_id),
                       failure_reason = @Reason,
                       settled_at = @At
                 WHERE id = @TopupId AND state = 'Pending';
                """,
                new
                {
                    TopupId = topupId,
                    ProviderTransactionId = providerTransactionId,
                    Reason = reason,
                    At = at,
                },
                cancellationToken: cancellationToken));

        return rows == 1;
    }
}

using Dapper;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.FleetBilling.Persistence;

/// <summary><c>billing.fleet_topups</c> (migration 1108) — one row per gateway session.</summary>
internal interface IFleetTopupRepository
{
    /// <param name="createdAt">
    /// The service's own clock, not the database's <c>now()</c>. D6' §7.1's 90-second window is
    /// evaluated against <c>TimeProvider</c> in <c>FleetTopupService</c>, and a row stamped by a
    /// different clock than the one that measures it is a window whose width depends on the drift
    /// between two machines.
    /// </param>
    Task<FleetTopup> CreateAsync(
        Guid fleetId,
        Guid accountId,
        Guid initiatedBy,
        string method,
        long amountMinor,
        string providerOrderId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken);

    /// <summary>One session, scoped to its organisation.</summary>
    Task<FleetTopup?> FindAsync(Guid fleetId, Guid topupId, CancellationToken cancellationToken);

    /// <summary>One session by id, whatever organisation it belongs to (the callback path).</summary>
    Task<FleetTopup?> ReadAsync(Guid topupId, CancellationToken cancellationToken);

    /// <summary>R-19's first guard: the session this provider transaction already settled.</summary>
    Task<FleetTopup?> ReadByProviderTransactionAsync(
        string providerTransactionId, CancellationToken cancellationToken);

    /// <summary>The session a callback names, by our id or by the reference the gateway echoes.</summary>
    Task<FleetTopup?> ResolveAsync(Guid? topupId, string? providerOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a Pending session to Succeeded inside the caller's transaction. False when something
    /// else moved it first.
    /// </summary>
    Task<bool> TrySettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid topupId,
        string providerTransactionId,
        Guid journalEntryId,
        DateTimeOffset settledAt,
        CancellationToken cancellationToken);

    /// <summary>Moves a Pending session to Failed with a reason. Idempotent.</summary>
    Task TryFailAsync(
        Guid topupId,
        string? providerTransactionId,
        string reason,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetTopupRepository"/>
internal sealed class FleetTopupRepository(INpgsqlConnectionFactory connections) : IFleetTopupRepository
{
    private const string Columns = """
        id, fleet_id, account_id, initiated_by, method, amount_minor, currency, state,
        provider_order_id, provider_transaction_id, journal_entry_id, failure_reason,
        created_at, settled_at
        """;

    public async Task<FleetTopup> CreateAsync(
        Guid fleetId,
        Guid accountId,
        Guid initiatedBy,
        string method,
        long amountMinor,
        string providerOrderId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleAsync<FleetTopup>(
            new CommandDefinition(
                $"""
                INSERT INTO billing.fleet_topups
                      (fleet_id, account_id, initiated_by, method, amount_minor, provider_order_id,
                       created_at, updated_at)
                VALUES (@FleetId, @AccountId, @InitiatedBy, @Method, @AmountMinor, @ProviderOrderId,
                        @CreatedAt, @CreatedAt)
                RETURNING {Columns};
                """,
                new
                {
                    FleetId = fleetId,
                    AccountId = accountId,
                    InitiatedBy = initiatedBy,
                    Method = method,
                    AmountMinor = amountMinor,
                    ProviderOrderId = providerOrderId,
                    CreatedAt = createdAt,
                },
                cancellationToken: cancellationToken));
    }

    public async Task<FleetTopup?> FindAsync(Guid fleetId, Guid topupId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FleetTopup>(
            new CommandDefinition(
                $"SELECT {Columns} FROM billing.fleet_topups WHERE id = @TopupId AND fleet_id = @FleetId;",
                new { TopupId = topupId, FleetId = fleetId },
                cancellationToken: cancellationToken));
    }

    public async Task<FleetTopup?> ReadAsync(Guid topupId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FleetTopup>(
            new CommandDefinition(
                $"SELECT {Columns} FROM billing.fleet_topups WHERE id = @TopupId;",
                new { TopupId = topupId },
                cancellationToken: cancellationToken));
    }

    public async Task<FleetTopup?> ReadByProviderTransactionAsync(
        string providerTransactionId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FleetTopup>(
            new CommandDefinition(
                $"SELECT {Columns} FROM billing.fleet_topups WHERE provider_transaction_id = @ProviderTransactionId;",
                new { ProviderTransactionId = providerTransactionId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// Our id first, the echoed reference second. A provider that carries both agrees with itself;
    /// one that carries only <c>orderId</c> — which D6' §7.1's callback shape allows — is why
    /// <c>provider_order_id</c> exists as a column with a unique index of its own.
    /// </remarks>
    public async Task<FleetTopup?> ResolveAsync(
        Guid? topupId, string? providerOrderId, CancellationToken cancellationToken)
    {
        if (topupId is { } id)
        {
            var byId = await ReadAsync(id, cancellationToken);

            if (byId is not null)
            {
                return byId;
            }
        }

        if (string.IsNullOrWhiteSpace(providerOrderId))
        {
            return null;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<FleetTopup>(
            new CommandDefinition(
                $"SELECT {Columns} FROM billing.fleet_topups WHERE provider_order_id = @ProviderOrderId;",
                new { ProviderOrderId = providerOrderId },
                cancellationToken: cancellationToken));
    }

    /// <remarks>
    /// <c>WHERE state = 'Pending'</c> is the claim, and it runs inside the ledger's own transaction
    /// (wallet-svc has already committed the postings by then, so this is the local half): two
    /// callbacks racing for one session leave one Succeeded row, and the loser throwing is what
    /// keeps the session state and the money identical.
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
        ArgumentNullException.ThrowIfNull(connection);

        var updated = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.fleet_topups
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

        return updated == 1;
    }

    public async Task TryFailAsync(
        Guid topupId,
        string? providerTransactionId,
        string reason,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE billing.fleet_topups
                   SET state = 'Failed',
                       provider_transaction_id = coalesce(@ProviderTransactionId, provider_transaction_id),
                       failure_reason = @Reason,
                       settled_at = @FailedAt
                 WHERE id = @TopupId AND state = 'Pending';
                """,
                new
                {
                    TopupId = topupId,
                    ProviderTransactionId = providerTransactionId,
                    Reason = reason,
                    FailedAt = failedAt,
                },
                cancellationToken: cancellationToken));
    }
}

using Dapper;
using MageRide.Shared.Persistence;
using Npgsql;

namespace MageRide.Wallet.Persistence;

/// <summary>
/// <c>billing.accounts.owner_type</c> (§10, migration 1101). <b>There is no <c>reseller</c></b> —
/// AL-01 makes reselling a behaviour of an ordinary driver account, and the CHECK enforces it.
/// </summary>
internal static class AccountOwnerTypes
{
    public const string Driver = "driver";
    public const string Fleet = "fleet";

    /// <summary>The platform's own account: the counterparty of every top-up and voucher.</summary>
    public const string Platform = "platform";

    /// <summary>Money in flight — a gateway session that has settled but not been attributed.</summary>
    public const string Suspense = "suspense";

    /// <summary>The two owner types that have a wallet screen and a balance a driver can spend.</summary>
    public static bool IsWalletOwner(string ownerType) =>
        ownerType is Driver or Fleet;
}

/// <summary>One ledger account, as the posting path needs it.</summary>
/// <param name="IsWalletOwner">
/// Driver and fleet accounts. Only these get a <c>billing.wallets</c> mirror, a history line and the
/// non-negativity rule; the platform side of every entry is negative by construction.
/// </param>
internal sealed record LedgerAccount(
    Guid Id, string OwnerType, Guid? OwnerId, string Currency, long BalanceMinor)
{
    public bool IsWalletOwner => AccountOwnerTypes.IsWalletOwner(OwnerType);
}

/// <summary>What the wallet screen reads (US-9.7).</summary>
internal sealed record WalletSummary(
    Guid AccountId, long BalanceMinor, long OutstandingDebtMinor, string Currency, DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Balance net of accrued debt, floored at zero — what the daily-fee gate is really asking about.
    /// </summary>
    public long AvailableMinor => Math.Max(0, BalanceMinor - OutstandingDebtMinor);
}

/// <summary><c>billing.accounts</c> and its <c>billing.wallets</c> mirror.</summary>
internal interface IAccountRepository
{
    /// <summary>
    /// The driver's ledger account, created on first use.
    /// </summary>
    /// <remarks>
    /// Lazily rather than at driver onboarding: registry-svc (C029) grants the driver role and knows
    /// nothing about money, and a wallet with no movements is a row nobody needs. <c>ux_accounts_owner</c>
    /// makes the create idempotent under a race.
    /// </remarks>
    Task<LedgerAccount> EnsureDriverAccountAsync(Guid driverId, CancellationToken cancellationToken);

    /// <summary>The platform's own singleton account — the counterparty of a top-up or a voucher.</summary>
    Task<LedgerAccount> PlatformAccountAsync(CancellationToken cancellationToken);

    /// <summary>Locks accounts for update, in the order given, and returns them by id.</summary>
    Task<IReadOnlyDictionary<Guid, LedgerAccount>> LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> accountIds,
        CancellationToken cancellationToken);

    Task SetBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        long balanceMinor,
        CancellationToken cancellationToken);

    /// <summary>Upserts the <c>billing.wallets</c> read model to match the account.</summary>
    Task MirrorWalletAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        long balanceMinor,
        CancellationToken cancellationToken);

    /// <summary>The wallet screen's summary, or <see langword="null"/> when the user has no account.</summary>
    Task<WalletSummary?> ReadSummaryAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Whether this user is a driver, for the routes that only a driver may reach.</summary>
    Task<bool> IsDriverAsync(Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountRepository"/>
internal sealed class AccountRepository(INpgsqlConnectionFactory connections) : IAccountRepository
{
    private const string SelectColumns =
        "id, owner_type, owner_id, currency, balance_minor";

    public async Task<LedgerAccount> EnsureDriverAccountAsync(
        Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // INSERT … ON CONFLICT DO NOTHING then read: `ux_accounts_owner` is the arbiter, so two
        // concurrent first movements for one driver cannot create two wallets. `RETURNING` alone would
        // give the loser of the race nothing.
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.accounts (owner_type, owner_id, currency)
                VALUES ('driver', @DriverId, 'LKR')
                ON CONFLICT DO NOTHING;
                """,
                new { DriverId = driverId },
                cancellationToken: cancellationToken));

        return await connection.QuerySingleAsync<LedgerAccount>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.accounts
                 WHERE owner_type = 'driver' AND owner_id = @DriverId AND currency = 'LKR';
                """,
                new { DriverId = driverId },
                cancellationToken: cancellationToken));
    }

    public async Task<LedgerAccount> PlatformAccountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // Seeded by migration 1101 and a singleton by `ux_accounts_platform`, so this is a read and
        // never a create: a service that could mint a second platform account would split the
        // platform's own balance across two rows and nobody would notice.
        return await connection.QuerySingleAsync<LedgerAccount>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.accounts
                 WHERE owner_type = 'platform' AND owner_id IS NULL AND currency = 'LKR';
                """,
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<Guid, LedgerAccount>> LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> accountIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        // ORDER BY id inside the FOR UPDATE, so every transaction takes the same lock order and two
        // simultaneous transfers between the same pair of drivers cannot deadlock. `= ANY(@Ids)`
        // rather than an IN list keeps it one prepared statement whatever the leg count.
        var rows = await connection.QueryAsync<LedgerAccount>(
            new CommandDefinition(
                $"""
                SELECT {SelectColumns}
                  FROM billing.accounts
                 WHERE id = ANY(@Ids)
                 ORDER BY id
                   FOR UPDATE;
                """,
                new { Ids = accountIds.ToArray() },
                transaction,
                cancellationToken: cancellationToken));

        return rows.ToDictionary(row => row.Id);
    }

    public Task SetBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        long balanceMinor,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE billing.accounts SET balance_minor = @BalanceMinor WHERE id = @AccountId;",
                new { AccountId = accountId, BalanceMinor = balanceMinor },
                transaction,
                cancellationToken: cancellationToken));

    public Task MirrorWalletAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        long balanceMinor,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.wallets (account_id, balance_minor)
                VALUES (@AccountId, @BalanceMinor)
                ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor;
                """,
                new { AccountId = accountId, BalanceMinor = balanceMinor },
                transaction,
                cancellationToken: cancellationToken));

    /// <remarks>
    /// <para>
    /// The balance comes from <c>billing.accounts</c>, not from the <c>billing.wallets</c> mirror: §10
    /// makes the ledger the master and says "never reconcile the ledger against this". The mirror
    /// exists for dispatch-svc's gate, which reads one row per candidate on the hot path.
    /// </para>
    /// <para>
    /// <b><c>outstandingDebtMinor</c> is read from <c>dispatch.cancellation_penalties</c></b>, which is
    /// another bounded context's table — read-only, and for the same reason iam-svc's bootstrap reads
    /// across lines: the alternative is a synchronous call to dispatch-svc on the wallet screen. For a
    /// *driver* it is nearly always zero, because §11.12 answers a driver cancellation with a
    /// reputation hit and a brief delist rather than money; the column is here because
    /// <c>availableMinor</c> is what the fee gate checks and a wallet that reported gross would
    /// overstate it the day a debt exists.
    /// </para>
    /// </remarks>
    public async Task<WalletSummary?> ReadSummaryAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<(
            Guid AccountId, long BalanceMinor, long OutstandingDebtMinor, string Currency, DateTimeOffset UpdatedAt)?>(
            new CommandDefinition(
                """
                SELECT a.id AS account_id,
                       a.balance_minor,
                       coalesce((SELECT sum(p.amount_minor)
                                   FROM dispatch.cancellation_penalties p
                                  WHERE p.passenger_id = a.owner_id AND p.status = 'OUTSTANDING'), 0)
                         AS outstanding_debt_minor,
                       a.currency,
                       coalesce(w.updated_at, a.created_at) AS updated_at
                  FROM billing.accounts a
                  LEFT JOIN billing.wallets w ON w.account_id = a.id
                 WHERE a.owner_id = @UserId AND a.currency = 'LKR'
                   AND a.owner_type IN ('driver','fleet');
                """,
                new { UserId = userId },
                cancellationToken: cancellationToken));

        return row is null
            ? null
            : new WalletSummary(
                row.Value.AccountId,
                row.Value.BalanceMinor,
                row.Value.OutstandingDebtMinor,
                row.Value.Currency,
                row.Value.UpdatedAt);
    }

    public async Task<bool> IsDriverAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // The role set, not `iam.users.role`: AL-06 makes effective permissions the union of every
        // role held, and a fleet owner who also drives holds `driver` in iam.user_roles while their
        // primary role says otherwise.
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS (
                  SELECT 1 FROM iam.users u WHERE u.id = @UserId AND u.role = 'driver'
                  UNION ALL
                  SELECT 1 FROM iam.user_roles r WHERE r.user_id = @UserId AND r.role = 'driver');
                """,
                new { UserId = userId },
                cancellationToken: cancellationToken));
    }
}

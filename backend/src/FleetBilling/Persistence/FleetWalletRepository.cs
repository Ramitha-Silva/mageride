using Dapper;
using MageRide.FleetBilling.Domain;
using MageRide.Shared.Persistence;

namespace MageRide.FleetBilling.Persistence;

/// <summary>
/// The fleet's side of <c>billing.accounts</c> / <c>wallet_transactions</c>, read-only.
/// </summary>
/// <remarks>
/// <b>Read-only, and that is the whole architecture of this service's money.</b> wallet-svc (C046)
/// is the only writer of <c>billing.journal_postings</c> on this platform (D-09) and therefore the
/// only thing that moves a balance; what happens here is a screen reading a number and a settlement
/// asking wallet-svc to move one. There is no <c>UPDATE billing.accounts</c> anywhere in this
/// assembly, which is what makes "no parallel ledger" a property of the code rather than a promise.
/// </remarks>
internal interface IFleetWalletRepository
{
    /// <summary>The organisation's wallet, or a zero summary when it has never moved money.</summary>
    Task<FleetWalletSummary> ReadSummaryAsync(Guid fleetId, CancellationToken cancellationToken);

    /// <summary>One page of the wallet's history, newest first.</summary>
    Task<IReadOnlyList<FleetWalletMovement>> ReadMovementsAsync(
        Guid fleetId, DateTimeOffset? before, long? beforeId, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetWalletRepository"/>
internal sealed class FleetWalletRepository(INpgsqlConnectionFactory connections) : IFleetWalletRepository
{
    /// <remarks>
    /// <para>
    /// The balance is read from <c>billing.accounts</c>, §10's master, and never from the
    /// <c>billing.wallets</c> mirror: the mirror exists for dispatch-svc's hot path, and a billing
    /// screen reading it would show an operator a number that lags their own top-up.
    /// </para>
    /// <para>
    /// <c>outstandingMinor</c> is Σ of the organisation's open invoices — the fleet analogue of the
    /// <c>outstandingDebtMinor</c> wallet-svc reports for a driver, and the number that decides
    /// whether the next run will settle anything. <b>It may exceed the balance</b>, which is why
    /// <c>availableMinor</c> is signed here where a driver's is floored at zero: a fleet that owes
    /// more than it holds is exactly the state SCR-FP-010 has to draw, and flooring it would render
    /// "you can cover this" over a shortfall.
    /// </para>
    /// <para>
    /// An organisation with no account at all is a real and ordinary state — the account is created
    /// lazily by the first movement (C046's <c>EnsureFleetAccountAsync</c>) — so this answers a zero
    /// summary rather than null, and the billing screen renders the same as it will after the first
    /// top-up.
    /// </para>
    /// </remarks>
    public async Task<FleetWalletSummary> ReadSummaryAsync(Guid fleetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<(
            Guid? AccountId, long BalanceMinor, string Currency, long OutstandingMinor, DateTimeOffset? UpdatedAt)?>(
            new CommandDefinition(
                """
                SELECT a.id AS account_id,
                       coalesce(a.balance_minor, 0) AS balance_minor,
                       coalesce(a.currency, 'LKR') AS currency,
                       coalesce((SELECT sum(i.total_minor)
                                   FROM billing.fleet_invoices i
                                  WHERE i.fleet_id = @FleetId
                                    AND i.status IN ('DUE','OVERDUE')), 0)::bigint AS outstanding_minor,
                       coalesce(w.updated_at, a.created_at) AS updated_at
                  FROM billing.accounts a
                  LEFT JOIN billing.wallets w ON w.account_id = a.id
                 WHERE a.owner_type = 'fleet' AND a.owner_id = @FleetId AND a.currency = 'LKR';
                """,
                new { FleetId = fleetId },
                cancellationToken: cancellationToken));

        if (row is { } found)
        {
            return new FleetWalletSummary(
                found.AccountId, found.BalanceMinor, found.Currency, found.OutstandingMinor, found.UpdatedAt);
        }

        // No account yet. The outstanding total is still real — an invoice can be issued before the
        // organisation has ever topped up, and that combination is the one an operator most needs
        // to see.
        var outstanding = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                SELECT coalesce(sum(total_minor), 0)::bigint
                  FROM billing.fleet_invoices
                 WHERE fleet_id = @FleetId AND status IN ('DUE','OVERDUE');
                """,
                new { FleetId = fleetId },
                cancellationToken: cancellationToken));

        return new FleetWalletSummary(null, 0, "LKR", outstanding, null);
    }

    /// <remarks>
    /// Keyset on <c>(ts, id)</c>, wallet-svc's rule: two entries can land in the same microsecond —
    /// a settlement run posts many — and a cursor on the timestamp alone would skip or repeat rows.
    /// </remarks>
    public async Task<IReadOnlyList<FleetWalletMovement>> ReadMovementsAsync(
        Guid fleetId, DateTimeOffset? before, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<FleetWalletMovement>(
            new CommandDefinition(
                """
                SELECT t.id,
                       t.entry_id,
                       t.kind,
                       t.amount_minor,
                       t.balance_after_minor,
                       t.description,
                       t.ts
                  FROM billing.wallet_transactions t
                  JOIN billing.accounts a ON a.id = t.account_id
                 WHERE a.owner_type = 'fleet' AND a.owner_id = @FleetId
                   AND (@Before::timestamptz IS NULL
                        OR t.ts < @Before
                        OR (t.ts = @Before AND t.id < @BeforeId))
                 ORDER BY t.ts DESC, t.id DESC
                 LIMIT @Limit;
                """,
                new
                {
                    FleetId = fleetId,
                    Before = before,
                    BeforeId = beforeId ?? long.MaxValue,
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows];
    }
}

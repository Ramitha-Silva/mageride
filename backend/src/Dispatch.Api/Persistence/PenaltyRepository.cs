using Dapper;
using MageRide.Dispatch.Domain;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.cancellation_penalties</c> (migrations 0706, 0713) — the passenger's accrued,
/// uncollected debt (D-05, AL-16, D5' §7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Accrued here, collected by fare-svc.</b> There is no card on file, so a post-acceptance
/// cancellation cannot be charged when it happens: the Rs 50 is recorded against the passenger and
/// added to their <em>next</em> completed trip's fare, where it passes through that trip's driver's
/// wallet to the driver who was stood up. ride-svc states the debt on
/// <c>cancellation.penalty.accrued</c>; this table is where it waits; fare-svc moves the money and
/// calls the settle route. No ledger entry is written here — D-09 makes
/// <c>billing.journal_entries</c> the master of money and this bounded context does not own it.
/// </para>
/// <para>
/// <b>The double-apply guard is not the index D5' §7.1 names.</b> <c>ux_penalty_apply(id,
/// applied_ride_id)</c> (0706) is unique by construction — <c>id</c> is the primary key — so it
/// rejects nothing on its own, which 0706's own header says. What actually holds is here: the
/// settle statement is conditional on <c>status = 'OUTSTANDING'</c> and claims its rows
/// <c>FOR UPDATE SKIP LOCKED</c>, so a settled penalty cannot be settled again by a retry, by a
/// second completed trip or by two fare-svc replicas racing. The accrual side is guarded by
/// <c>ux_penalty_accrual(original_ride_id, basis)</c> (0713), because <c>ride.events</c> delivery
/// is at-least-once (D6' §2.3).
/// </para>
/// </remarks>
public interface IPenaltyRepository
{
    /// <summary>
    /// Records an accrual. Returns <see langword="null"/> when this (ride, basis) pair already has
    /// a row — a redelivery, which is normal and not an error.
    /// </summary>
    Task<PenaltyRow?> TryAccrueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        Guid originalRideId,
        Guid affectedDriverId,
        long amountMinor,
        string basis,
        CancellationToken cancellationToken);

    /// <summary>Everything this passenger still owes, oldest first.</summary>
    Task<IReadOnlyList<PenaltyRow>> OutstandingAsync(
        NpgsqlConnection connection, Guid passengerId, CancellationToken cancellationToken);

    /// <summary>
    /// Settles every outstanding penalty for one passenger against one completed ride, and returns
    /// the rows it settled. A second call with the same ride settles nothing and returns nothing.
    /// </summary>
    Task<IReadOnlyList<PenaltyRow>> SettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid passengerId,
        Guid appliedRideId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPenaltyRepository"/>
public sealed class PenaltyRepository : IPenaltyRepository
{
    private const string Columns =
        """
        id AS Id,
        passenger_id AS PassengerId,
        original_ride_id AS OriginalRideId,
        affected_driver_id AS AffectedDriverId,
        -- ::bigint because `amount_minor` is INTEGER and `PenaltyRow.AmountMinor` is a long:
        -- Dapper matches a record constructor on exact field types and will not widen for you.
        amount_minor::bigint AS AmountMinor,
        basis AS Basis,
        status AS Status,
        applied_ride_id AS AppliedRideId,
        created_at AS CreatedAt
        """;

    /// <summary>
    /// The same list, qualified. <c>RETURNING</c> on an <c>UPDATE … FROM</c> sees both relations,
    /// so a bare <c>id</c> is ambiguous against the claim CTE's.
    /// </summary>
    private const string QualifiedColumns =
        """
        p.id AS Id,
        p.passenger_id AS PassengerId,
        p.original_ride_id AS OriginalRideId,
        p.affected_driver_id AS AffectedDriverId,
        p.amount_minor::bigint AS AmountMinor,
        p.basis AS Basis,
        p.status AS Status,
        p.applied_ride_id AS AppliedRideId,
        p.created_at AS CreatedAt
        """;

    public Task<PenaltyRow?> TryAccrueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        Guid originalRideId,
        Guid affectedDriverId,
        long amountMinor,
        string basis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<PenaltyRow>(new CommandDefinition(
            $"""
             INSERT INTO dispatch.cancellation_penalties
               (passenger_id, original_ride_id, affected_driver_id, amount_minor, basis, status)
             VALUES
               (@PassengerId, @OriginalRideId, @AffectedDriverId, @AmountMinor, @Basis,
                '{PenaltyStatuses.Outstanding}')
                 ON CONFLICT (original_ride_id, basis) DO NOTHING
             RETURNING {Columns};
             """,
            new
            {
                PassengerId = passengerId,
                OriginalRideId = originalRideId,
                AffectedDriverId = affectedDriverId,

                // `amount_minor` is INTEGER in the DDL and the event carries int64 minor units.
                // A fare large enough to overflow would be Rs 21 million; the cast is checked so a
                // corrupt payload fails loudly here rather than silently wrapping into the ledger.
                AmountMinor = checked((int)amountMinor),
                Basis = basis,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PenaltyRow>> OutstandingAsync(
        NpgsqlConnection connection, Guid passengerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<PenaltyRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.cancellation_penalties
              WHERE passenger_id = @PassengerId AND status = '{PenaltyStatuses.Outstanding}'
              ORDER BY created_at, id;
             """,
            new { PassengerId = passengerId },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<PenaltyRow>> SettleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid passengerId,
        Guid appliedRideId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        // D5' §7.1 verbatim: "for each OUTSTANDING penalty FOR UPDATE SKIP LOCKED". SKIP LOCKED and
        // not NOWAIT because a row another settlement already holds is a row that is being paid —
        // waiting for it would only produce a second attempt to pay it.
        //
        // The `status = 'OUTSTANDING'` predicate appears twice on purpose: once inside the claim so
        // the lock is taken on the right rows, and once on the UPDATE so a row that changed between
        // the two is not overwritten. Together they are what "never applied twice" means.
        var rows = await connection.QueryAsync<PenaltyRow>(new CommandDefinition(
            $"""
             WITH claimed AS (
               SELECT id FROM dispatch.cancellation_penalties
                WHERE passenger_id = @PassengerId AND status = '{PenaltyStatuses.Outstanding}'
                ORDER BY created_at, id
                  FOR UPDATE SKIP LOCKED)
             UPDATE dispatch.cancellation_penalties p
                SET status = '{PenaltyStatuses.Settled}', applied_ride_id = @AppliedRideId
               FROM claimed c
              WHERE p.id = c.id AND p.status = '{PenaltyStatuses.Outstanding}'
             RETURNING {QualifiedColumns};
             """,
            new { PassengerId = passengerId, AppliedRideId = appliedRideId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

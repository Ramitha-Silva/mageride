using Dapper;
using MageRide.Reputation.Domain;
using Npgsql;

namespace MageRide.Reputation.Persistence;

/// <summary>
/// <c>reputation.counters</c> — the tallies this service exists to be the only home for.
/// </summary>
/// <remarks>
/// Every read that precedes a write takes the row <c>FOR UPDATE</c>. Two facts about one user can
/// arrive at once — a Kafka partition rebalance redelivering a cancellation while a gRPC report
/// lands — and a read-modify-write without the lock would lose one of them, which for a counter
/// whose third increment blocks somebody is the difference between two strikes and three.
/// </remarks>
public interface ICounterRepository
{
    /// <summary>Reads the row without locking. For the query paths only.</summary>
    Task<CounterRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the row for update, creating it if the user has never been counted before.
    /// </summary>
    /// <remarks>
    /// The insert is <c>ON CONFLICT DO NOTHING</c> followed by the locking select rather than
    /// <c>ON CONFLICT DO UPDATE … RETURNING</c>: two concurrent first-facts for the same user would
    /// otherwise both write, and the second would overwrite the first's increment.
    /// </remarks>
    Task<CounterRow> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, DateTimeOffset now,
        CancellationToken cancellationToken);

    Task SaveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CounterRow row, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICounterRepository"/>
public sealed class CounterRepository : ICounterRepository
{
    // cancellations_continuous is SMALLINT (server_db_schema.md §7) and CounterRow types it as
    // int: Dapper picks a record's constructor by matching parameter types against the reader's
    // field types, and Int16 is not Int32, so the cast is what makes the row materialise at all.
    private const string Columns =
        """
        user_id AS UserId,
        cancellations_continuous::int AS CancellationsContinuous,
        reports_total AS ReportsTotal,
        no_shows AS NoShows,
        window_reset_at AS WindowStartedAt,
        updated_at AS UpdatedAt
        """;

    public Task<CounterRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<CounterRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reputation.counters WHERE user_id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<CounterRow> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reputation.counters (user_id, window_reset_at)
            VALUES (@UserId, @Now)
            ON CONFLICT (user_id) DO NOTHING;
            """,
            new { UserId = userId, Now = now },
            transaction,
            cancellationToken: cancellationToken));

        return await connection.QuerySingleAsync<CounterRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reputation.counters WHERE user_id = @UserId FOR UPDATE;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task SaveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CounterRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(row);

        // updated_at is left to trg_counters_updated (migration 0002) — the same reason the C032
        // handoff gives for rides.rides: a column the application sets can be forged, and this one
        // is what an appeal reads to find out when a strike landed.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE reputation.counters
               SET cancellations_continuous = @CancellationsContinuous,
                   reports_total = @ReportsTotal,
                   no_shows = @NoShows,
                   window_reset_at = @WindowStartedAt
             WHERE user_id = @UserId;
            """,
            new
            {
                row.UserId,
                row.CancellationsContinuous,
                row.ReportsTotal,
                row.NoShows,
                row.WindowStartedAt,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}

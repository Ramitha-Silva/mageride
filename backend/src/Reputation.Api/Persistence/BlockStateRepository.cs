using Dapper;
using MageRide.Reputation.Domain;
using Npgsql;

namespace MageRide.Reputation.Persistence;

/// <summary>
/// <c>reputation.block_states</c> — the row dispatch-svc gates every candidate build on (D-04).
/// </summary>
public interface IBlockStateRepository
{
    Task<BlockStateRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the row for update, creating an <c>OK</c> one first if the user has none yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is taken before the counter row, always.</b> The expiry sweep discovers users
    /// through <see cref="ClaimExpiredAsync"/> and can only lock in that order, so an intake that
    /// locked counters first would deadlock against it. One lock order, stated here because it is
    /// invisible at either call site.
    /// </para>
    /// <para>
    /// <b>It creates the row rather than returning null, and that is what makes the order hold.</b>
    /// A <c>SELECT … FOR UPDATE</c> that matches nothing takes no lock, so two concurrent facts for
    /// a user with no row yet would both fall through to the counters and then race on the
    /// block-state insert — one holding block_states and waiting for counters while the other holds
    /// counters and waits for block_states. Postgres detects that and aborts one, which for an
    /// intake means a counted fact somebody is waiting on. Materialising the row first removes the
    /// cycle instead of retrying around it. Reads still use <see cref="FindAsync"/>: a dispatch
    /// round asking about a thousand drivers must not create a thousand rows.
    /// </para>
    /// </remarks>
    Task<BlockStateRow> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken);

    /// <summary>Writes the effective state. Idempotent — the same decision twice writes the same row.</summary>
    Task UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        BlockDecision decision,
        string source,
        Guid? setBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> rows whose time box has passed, for the expiry
    /// sweep.
    /// </summary>
    /// <remarks>
    /// <c>FOR UPDATE SKIP LOCKED</c> so several replicas can sweep at once without deadlocking —
    /// the same claim shape ride-svc's and dispatch-svc's timer sweeps use (R-04).
    /// </remarks>
    Task<IReadOnlyList<BlockStateRow>> ClaimExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBlockStateRepository"/>
public sealed class BlockStateRepository : IBlockStateRepository
{
    private const string Columns =
        """
        user_id AS UserId,
        state AS State,
        expires_at AS ExpiresAt,
        source AS Source,
        reason AS Reason,
        set_by AS SetBy,
        updated_at AS UpdatedAt
        """;

    public Task<BlockStateRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<BlockStateRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reputation.block_states WHERE user_id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<BlockStateRow> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        // Column defaults do the work: state 'OK' (0801) and source 'auto' (0804). A row created
        // here says nothing has happened yet, which is exactly what it means.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reputation.block_states (user_id) VALUES (@UserId)
            ON CONFLICT (user_id) DO NOTHING;
            """,
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));

        return await connection.QuerySingleAsync<BlockStateRow>(new CommandDefinition(
            $"SELECT {Columns} FROM reputation.block_states WHERE user_id = @UserId FOR UPDATE;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        BlockDecision decision,
        string source,
        Guid? setBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reputation.block_states (user_id, state, expires_at, source, reason, set_by)
            VALUES (@UserId, @State, @ExpiresAt, @Source, @Reason, @SetBy)
            ON CONFLICT (user_id) DO UPDATE
               SET state = EXCLUDED.state,
                   expires_at = EXCLUDED.expires_at,
                   source = EXCLUDED.source,
                   reason = EXCLUDED.reason,
                   set_by = EXCLUDED.set_by;
            """,
            new
            {
                UserId = userId,
                decision.State,
                decision.ExpiresAt,
                Source = source,
                decision.Reason,
                SetBy = setBy,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BlockStateRow>> ClaimExpiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var rows = await connection.QueryAsync<BlockStateRow>(new CommandDefinition(
            $"""
             SELECT {Columns}
               FROM reputation.block_states
              WHERE expires_at IS NOT NULL
                AND expires_at <= @Now
                AND state <> 'OK'
              ORDER BY expires_at
              LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED;
             """,
            new { Now = now, BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

using Dapper;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>
/// <c>rides.transitions</c> (migration 0602) — the immutable per-ride audit ADD Appendix B.2
/// invariant 4 requires: every state move writes exactly one row here, in the same transaction as
/// the <c>UPDATE</c> and the outbox insert.
/// </summary>
/// <remarks>
/// Append-only by design: a correction is a new row, never an edit, so there is no update and no
/// delete on this interface and there never should be.
/// </remarks>
public interface IRideTransitionRepository
{
    Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string? fromState,
        string toState,
        string actorType,
        Guid? actorId,
        string? reasonCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// The ride's audit, oldest first — the transition log
    /// <c>GET /v1/internal/rides/{rideId}/saga-state</c> serves so an operator can see why a ride
    /// is stuck without querying Postgres directly.
    /// </summary>
    Task<IReadOnlyList<RideTransitionRow>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken);
}

/// <summary>One row of <c>rides.transitions</c>.</summary>
public sealed record RideTransitionRow(
    string? FromState, string ToState, string? ReasonCode, string ActorType, Guid? ActorId, DateTimeOffset Ts);

/// <inheritdoc cref="IRideTransitionRepository"/>
public sealed class RideTransitionRepository : IRideTransitionRepository
{
    public Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string? fromState,
        string toState,
        string actorType,
        Guid? actorId,
        string? reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // `ts` is supplied rather than left to the column's `now()` default. `now()` is the
        // transaction's start time, so the InProgress→Completed→PaymentPending pair that
        // `complete` writes in one transaction would land on the same instant and the audit would
        // no longer say which came first. `clock_timestamp()` is the real reading.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO rides.transitions (ride_id, from_state, to_state, reason_code, actor_type, actor_id, ts)
            VALUES (@RideId, @FromState, @ToState, @ReasonCode, @ActorType, @ActorId, clock_timestamp());
            """,
            new
            {
                RideId = rideId,
                FromState = fromState,
                ToState = toState,
                ReasonCode = reasonCode,
                ActorType = actorType,
                ActorId = actorId,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<RideTransitionRow>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<RideTransitionRow>(new CommandDefinition(
            """
            SELECT from_state AS FromState, to_state AS ToState, reason_code AS ReasonCode,
                   actor_type AS ActorType, actor_id AS ActorId, ts AS Ts
              FROM rides.transitions
             WHERE ride_id = @RideId
             ORDER BY ts, id;
            """,
            new { RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

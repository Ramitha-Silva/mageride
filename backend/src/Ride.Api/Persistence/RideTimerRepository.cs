using Dapper;
using MageRide.Ride.Domain;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>A <c>rides.timers</c> row this worker has leased.</summary>
/// <param name="Payload">Raw JSON, exactly as it was armed. Interpreted per <see cref="Kind"/>.</param>
public sealed record DueRideTimer(Guid Id, Guid RideId, string Kind, DateTimeOffset FireAt, string? Payload);

/// <summary>
/// <c>rides.timers</c> (migration 0605) — the R-04 durable backstop for the ride aggregate.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is scoped to <see cref="RideTimerKinds.Owned"/>, so dispatch-svc's
/// <c>offer_expiry</c> rows in the same table are invisible to this service and vice versa. That
/// separation is the whole reason two services can share one table without a coordination
/// protocol; the note on <see cref="RideTimerKinds"/> says why the split falls where it does.
/// </para>
/// <para>
/// Timers are armed <b>inside the transaction that changes the state</b>. A ride that reached
/// <c>DriverArrived</c> and whose no-show timer was written afterwards would, on a crash in
/// between, wait forever for a rider who never came — which is exactly the class of bug the
/// transactional outbox exists to remove, applied to time instead of to events.
/// </para>
/// </remarks>
public interface IRideTimerRepository
{
    /// <summary>Arms one timer. Returns its id.</summary>
    Task<Guid> ArmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string kind,
        DateTimeOffset fireAt,
        string? payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Arms one timer only if the ride has no unfired timer of that kind already.
    /// </summary>
    /// <returns>The new timer's id, or <see langword="null"/> when one was already armed.</returns>
    /// <remarks>
    /// The last-will path needs this: EMQX redelivers a retained <c>offline</c> to every replica and
    /// again on reconnect, and each delivery must not start the clock over. Deciding it in the
    /// <c>INSERT … WHERE NOT EXISTS</c> rather than with a prior read is what makes two replicas
    /// applying the same last will produce one grace instead of two.
    /// </remarks>
    Task<Guid?> ArmIfAbsentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string kind,
        DateTimeOffset fireAt,
        string? payload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires every unfired timer of these kinds on a ride, because the thing they were watching
    /// for has happened (or can no longer happen).
    /// </summary>
    /// <remarks>
    /// Marked fired rather than deleted: <c>rides.timers</c> is also the record of what the backstop
    /// was ever asked to watch, and a deleted row cannot be told apart from one never written.
    /// </remarks>
    Task<int> RetireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyCollection<string> kinds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Leases due timers to this worker: one statement that selects them <c>FOR UPDATE SKIP
    /// LOCKED</c> and pushes <c>fire_at</c> out by <paramref name="lease"/> in the same breath.
    /// </summary>
    /// <remarks>
    /// A lease rather than a held row lock, because acting on a claimed timer means opening another
    /// transaction on another connection, which would block behind the claim's own lock. A lease
    /// rather than an immediate <c>fired_at</c>, because a worker that dies mid-fire must not take
    /// the ride's only backstop with it: the row simply becomes due again when the lease runs out.
    /// </remarks>
    Task<IReadOnlyList<DueRideTimer>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    /// <summary>Marks a timer as run. Idempotent — a second call changes nothing.</summary>
    Task MarkFiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Timers this service owns that are more than <paramref name="olderThan"/> overdue — ADD
    /// §13.4's "<c>rides.timers</c> backlog &gt; 100" runbook trigger.
    /// </summary>
    Task<int> CountBacklogAsync(
        NpgsqlConnection connection, TimeSpan olderThan, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideTimerRepository"/>
public sealed class RideTimerRepository : IRideTimerRepository
{
    private static readonly string[] OwnedKinds = [.. RideTimerKinds.Owned];

    public async Task<Guid> ArmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string kind,
        DateTimeOffset fireAt,
        string? payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RequireOwned(kind);

        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO rides.timers (ride_id, kind, fire_at, payload)
            VALUES (@RideId, @Kind, @FireAt, @Payload::jsonb)
            RETURNING id;
            """,
            new { RideId = rideId, Kind = kind, FireAt = fireAt, Payload = payload },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<Guid?> ArmIfAbsentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string kind,
        DateTimeOffset fireAt,
        string? payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RequireOwned(kind);

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            INSERT INTO rides.timers (ride_id, kind, fire_at, payload)
            SELECT @RideId, @Kind, @FireAt, @Payload::jsonb
             WHERE NOT EXISTS (
                   SELECT 1 FROM rides.timers
                    WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL)
            RETURNING id;
            """,
            new { RideId = rideId, Kind = kind, FireAt = fireAt, Payload = payload },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> RetireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        IReadOnlyCollection<string> kinds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(kinds);

        if (kinds.Count == 0)
        {
            return 0;
        }

        foreach (var kind in kinds)
        {
            RequireOwned(kind);
        }

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE rides.timers
               SET fired_at = now()
             WHERE ride_id = @RideId
               AND kind = ANY(@Kinds)
               AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kinds = kinds.ToArray() },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DueRideTimer>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<DueRideTimer>(new CommandDefinition(
            """
            UPDATE rides.timers t
               SET fire_at = now() + make_interval(secs => @LeaseSeconds)
             WHERE t.id IN (
                   SELECT id FROM rides.timers
                    WHERE kind = ANY(@Kinds)
                      AND fired_at IS NULL
                      AND fire_at <= now()
                    ORDER BY fire_at
                    LIMIT @BatchSize
                      FOR UPDATE SKIP LOCKED)
            RETURNING t.id AS Id,
                      t.ride_id AS RideId,
                      t.kind AS Kind,
                      t.fire_at AS FireAt,
                      t.payload::text AS Payload;
            """,
            new { Kinds = OwnedKinds, BatchSize = batchSize, LeaseSeconds = lease.TotalSeconds },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task MarkFiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "UPDATE rides.timers SET fired_at = now() WHERE id = @TimerId AND fired_at IS NULL;",
            new { TimerId = timerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountBacklogAsync(
        NpgsqlConnection connection, TimeSpan olderThan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT count(*)::int FROM rides.timers
             WHERE kind = ANY(@Kinds)
               AND fired_at IS NULL
               AND fire_at < now() - make_interval(secs => @Seconds);
            """,
            new { Kinds = OwnedKinds, Seconds = olderThan.TotalSeconds },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// A kind outside <see cref="RideTimerKinds.Owned"/> is a programming error, not a request
    /// error: arming one would put a row in this table that this service's claim never picks up,
    /// and firing one would take a timer away from the service that does own it.
    /// </summary>
    private static void RequireOwned(string kind)
    {
        if (!RideTimerKinds.Owned.Contains(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind,
                $"ride-svc owns only {string.Join(", ", RideTimerKinds.Owned.Order(StringComparer.Ordinal))} " +
                "in rides.timers; offer_expiry is dispatch-svc's (C023), and location_request_expiry and " +
                "otp_attempt_window are armed by nobody — see RideTimerKinds for why neither can be.");
        }
    }
}

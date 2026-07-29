using Dapper;
using MageRide.Dispatch.Domain;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.timers</c> (migrations 0708, 0711) — the durable clocks whose subject is a ride or a
/// driver rather than a ride's offer.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds live here. <see cref="DispatchTimerKinds.RideTimeout"/> is US-6A.11's 120-second
/// global cascade deadline, and it has to fire in exactly the case where nothing else would — no
/// candidate was ever found, so no offer exists and no <c>rides.timers</c> backstop was ever armed.
/// <see cref="DispatchTimerKinds.OfferReleaseGrace"/> is R-15's last-will grace.
/// <c>directional_expiry</c> is 0708's own kind and is C036's to arm.
/// </para>
/// <para>
/// <b>Arming is idempotent by index, not by a prior read.</b> <c>ux_dispatch_timers_ride_live</c>
/// (0711) is one live timer per (subject, kind), so a redelivered <c>ride.requested</c> — which
/// D6' §2.3 guarantees will happen — cannot arm a second deadline for the same ride. A
/// <c>SELECT</c>-then-<c>INSERT</c> would have a window between them exactly as wide as the
/// redelivery it is trying to absorb.
/// </para>
/// <para>
/// <b>The same lease discipline as <see cref="OfferTimerRepository"/></b>, for the same two
/// reasons: acting on a claimed timer writes to this table again on another connection, and a
/// worker that dies mid-fire must hand the row back rather than take the ride's only deadline with
/// it.
/// </para>
/// </remarks>
public interface IDispatchTimerRepository
{
    /// <summary>
    /// Arms the ride's global cascade deadline. Returns the deadline that is now live — the one
    /// just written, or the one an earlier delivery had already armed.
    /// </summary>
    Task<DateTimeOffset?> ArmRideTimeoutAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken);

    /// <summary>The ride's live cascade deadline, or <see langword="null"/> if it has none.</summary>
    Task<DateTimeOffset?> FindRideDeadlineAsync(
        NpgsqlConnection connection, Guid rideId, CancellationToken cancellationToken);

    /// <summary>Arms a driver-subject timer, or leaves the one already live alone.</summary>
    Task ArmDriverTimerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string kind,
        DateTimeOffset fireAt,
        string? payload,
        CancellationToken cancellationToken);

    /// <summary>Retires every live timer of one kind for a ride. Idempotent.</summary>
    Task RetireForRideAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string kind,
        CancellationToken cancellationToken);

    /// <summary>Retires every live timer of one kind for a driver. Idempotent.</summary>
    Task RetireForDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string kind,
        CancellationToken cancellationToken);

    /// <summary>Leases due timers to this worker.</summary>
    Task<IReadOnlyList<DueDispatchTimer>> ClaimDueAsync(
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

    /// <summary>Moves a claimed timer's fire time out, without consuming it.</summary>
    Task RescheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDispatchTimerRepository"/>
public sealed class DispatchTimerRepository : IDispatchTimerRepository
{
    private const string Columns =
        "id AS Id, kind AS Kind, ride_id AS RideId, driver_id AS DriverId, fire_at AS FireAt, payload::text AS Payload";

    public Task<DateTimeOffset?> ArmRideTimeoutAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The CTE returns the row this statement inserted, and the UNION picks up the one it did
        // not — so a redelivery gets the *original* deadline back rather than nothing, and the
        // caller can tell the cascade how much of the 120 seconds is genuinely left.
        return connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            $"""
             WITH armed AS (
               INSERT INTO dispatch.timers (ride_id, kind, fire_at)
               VALUES (@RideId, '{DispatchTimerKinds.RideTimeout}', @FireAt)
                   ON CONFLICT (ride_id, kind) WHERE fired_at IS NULL AND ride_id IS NOT NULL
                   DO NOTHING
               RETURNING fire_at)
             SELECT fire_at FROM armed
             UNION ALL
             SELECT fire_at FROM dispatch.timers
              WHERE ride_id = @RideId AND kind = '{DispatchTimerKinds.RideTimeout}' AND fired_at IS NULL
              LIMIT 1;
             """,
            new { RideId = rideId, FireAt = fireAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<DateTimeOffset?> FindRideDeadlineAsync(
        NpgsqlConnection connection, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            $"""
             SELECT fire_at FROM dispatch.timers
              WHERE ride_id = @RideId AND kind = '{DispatchTimerKinds.RideTimeout}' AND fired_at IS NULL
              LIMIT 1;
             """,
            new { RideId = rideId },
            cancellationToken: cancellationToken));
    }

    public Task ArmDriverTimerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string kind,
        DateTimeOffset fireAt,
        string? payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        // DO NOTHING, not DO UPDATE: a flapping EMQX session republishing `offline` must not keep
        // pushing the grace out, or a driver whose connection is genuinely unstable would hold an
        // offer they cannot answer for as long as the flapping continued.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dispatch.timers (driver_id, kind, fire_at, payload)
            VALUES (@DriverId, @Kind, @FireAt, @Payload::jsonb)
                ON CONFLICT (driver_id, kind) WHERE fired_at IS NULL AND driver_id IS NOT NULL
                DO NOTHING;
            """,
            new { DriverId = driverId, Kind = kind, FireAt = fireAt, Payload = payload },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task RetireForRideAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid rideId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Marked fired rather than deleted, like rides.timers: the table is also the audit of what
        // was ever watched, and a deleted row cannot be told apart from one never written.
        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dispatch.timers SET fired_at = now()
             WHERE ride_id = @RideId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { RideId = rideId, Kind = kind },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task RetireForDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dispatch.timers SET fired_at = now()
             WHERE driver_id = @DriverId AND kind = @Kind AND fired_at IS NULL;
            """,
            new { DriverId = driverId, Kind = kind },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DueDispatchTimer>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<DueDispatchTimer>(new CommandDefinition(
            $"""
             UPDATE dispatch.timers t
                SET fire_at = now() + make_interval(secs => @LeaseSeconds)
              WHERE t.id IN (
                    SELECT id FROM dispatch.timers
                     WHERE kind = ANY(@Kinds)
                       AND fired_at IS NULL
                       AND fire_at <= now()
                     ORDER BY fire_at
                     LIMIT @BatchSize
                       FOR UPDATE SKIP LOCKED)
             RETURNING {Columns};
             """,
            new
            {
                BatchSize = batchSize,
                LeaseSeconds = lease.TotalSeconds,

                // Scoped by kind so this sweep never claims a `directional_expiry` row C036 owns.
                // Two components sharing one table with no coordination protocol is only safe while
                // every query says which kinds it is for.
                Kinds = new[] { DispatchTimerKinds.RideTimeout, DispatchTimerKinds.OfferReleaseGrace },
            },
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
            "UPDATE dispatch.timers SET fired_at = now() WHERE id = @TimerId AND fired_at IS NULL;",
            new { TimerId = timerId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task RescheduleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid timerId,
        DateTimeOffset fireAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dispatch.timers SET fire_at = @FireAt WHERE id = @TimerId AND fired_at IS NULL;",
            new { TimerId = timerId, FireAt = fireAt },
            transaction,
            cancellationToken: cancellationToken));
    }
}

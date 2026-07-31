using Dapper;
using MageRide.Fleet.Domain;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// <c>registry.fleet_schedules</c> — per-vehicle departures and the US-13.11 not-started alarm.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sweep is two statements, not one.</b> A departure that has been made and a departure that
/// has been missed are decided from different evidence — a <c>trips.sessions</c> row for the first,
/// the passage of time for the second — and PostgreSQL gives two data-modifying CTEs one snapshot
/// with no ordering between them, so a single statement that tried to do both could mark the same
/// row twice. They run in one transaction instead, in the order that matters: <b>a bus that left is
/// recorded as having left before anything decides whether to ring an alarm</b>.
/// </para>
/// <para>
/// <b>The claim is the update.</b> <c>UPDATE … WHERE status = 'SCHEDULED' … RETURNING</c> selects
/// and moves the row in one statement, so two replicas sweeping the same instant produce one
/// claimed row between them and one alarm — the shape notification-svc's E-01 ack sweep uses, for
/// the same reason. <c>alarm_raised_at</c> is written by the same statement, so "were we told?"
/// survives the status moving on.
/// </para>
/// </remarks>
public interface IFleetScheduleRepository
{
    Task<FleetSchedule> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        Guid? routeId,
        DateTimeOffset departAt,
        short notStartedAlarmMinutes,
        Guid createdBy,
        CancellationToken cancellationToken);

    /// <summary>The org's schedules, soonest departure first from <paramref name="from"/>.</summary>
    Task<IReadOnlyList<FleetSchedule>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        DateTimeOffset from,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the departures that were made: a session opened on the vehicle around its time.
    /// </summary>
    /// <param name="earlyGrace">
    /// How far before <c>depart_at</c> a session still counts as this departure. A bus that pulls
    /// out of the depot eight minutes early has made its 06:10, and an alarm about it would be the
    /// kind of false positive that gets an alarm switched off.
    /// </param>
    Task<int> MarkStartedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        TimeSpan earlyGrace,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims the departures whose alarm offset has passed with no session, and moves them to
    /// <c>MISSED</c> in the same statement.
    /// </summary>
    Task<IReadOnlyList<DueScheduleAlarm>> ClaimMissedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// The org's members who should hear that a bus did not leave (US-13.11's portal notification).
    /// </summary>
    /// <remarks>
    /// Every seat, viewers included: a Viewer is defined as "read-only fleet map &amp; analytics"
    /// (US-13.A5) and being told a vehicle has not departed is a monitoring fact, not a mutation.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListMemberIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetScheduleRepository"/>
internal sealed class FleetScheduleRepository : IFleetScheduleRepository
{
    private const string Columns = """
        id, fleet_id, vehicle_id, route_id, depart_at, not_started_alarm_minutes, status,
        alarm_raised_at, created_at
        """;

    public async Task<FleetSchedule> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        Guid vehicleId,
        Guid? routeId,
        DateTimeOffset departAt,
        short notStartedAlarmMinutes,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Guarded on the roster in the INSERT itself, the same way an assignment is: a vehicle that
        // left the fleet between the caller's screen and this statement must not gain a departure.
        return await connection.QuerySingleAsync<FleetSchedule>(new CommandDefinition(
            $"""
             INSERT INTO registry.fleet_schedules
               (fleet_id, vehicle_id, route_id, depart_at, not_started_alarm_minutes, created_by)
             SELECT @FleetId, @VehicleId, @RouteId, @DepartAt, @AlarmMinutes, @CreatedBy
              WHERE EXISTS (SELECT 1 FROM registry.fleet_vehicles
                             WHERE fleet_id = @FleetId AND vehicle_id = @VehicleId)
             RETURNING {Columns};
             """,
            new
            {
                FleetId = fleetId,
                VehicleId = vehicleId,
                RouteId = routeId,
                DepartAt = departAt,
                AlarmMinutes = notStartedAlarmMinutes,
                CreatedBy = createdBy,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FleetSchedule>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        DateTimeOffset from,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<FleetSchedule>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM registry.fleet_schedules
              WHERE fleet_id = @FleetId AND depart_at >= @From
              ORDER BY depart_at, id
              LIMIT @Limit;
             """,
            new { FleetId = fleetId, From = from, Limit = limit },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public Task<int> MarkStartedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        TimeSpan earlyGrace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Unbounded rather than batched, deliberately: this statement only touches rows whose
        // departure has passed and whose vehicle has a session, which is the small set, and leaving
        // one behind for the next pass would let its alarm fire in between.
        return connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE registry.fleet_schedules s
                SET status = '{FleetScheduleStatuses.Started}'
              WHERE s.status = '{FleetScheduleStatuses.Scheduled}'
                AND s.depart_at <= @Now
                AND EXISTS (SELECT 1
                              FROM trips.sessions t
                             WHERE t.vehicle_id = s.vehicle_id
                               AND t.started_at >= s.depart_at - @EarlyGrace
                               AND t.started_at <= @Now);
             """,
            new { Now = now, EarlyGrace = earlyGrace },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DueScheduleAlarm>> ClaimMissedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // FOR UPDATE SKIP LOCKED on the inner select so several replicas drain one backlog without
        // waiting on each other; the outer UPDATE's own `status = 'SCHEDULED'` predicate is what
        // makes the claim exclusive even without it.
        //
        // The plate is joined on afterwards because RETURNING cannot join, and the alarm is useless
        // without it — "a vehicle did not leave" is not something an operator can act on.
        var rows = await connection.QueryAsync<ClaimedAlarmRow>(new CommandDefinition(
            $"""
             WITH claimed AS (
               UPDATE registry.fleet_schedules s
                  SET status = '{FleetScheduleStatuses.Missed}', alarm_raised_at = now()
                WHERE s.id IN (
                        SELECT id FROM registry.fleet_schedules
                         WHERE status = '{FleetScheduleStatuses.Scheduled}'
                           AND depart_at + make_interval(mins => not_started_alarm_minutes::int) <= @Now
                         ORDER BY depart_at
                         LIMIT @BatchSize
                         FOR UPDATE SKIP LOCKED)
               RETURNING s.id, s.fleet_id, s.vehicle_id, s.depart_at, s.not_started_alarm_minutes)
             SELECT c.id, c.fleet_id, c.vehicle_id, c.depart_at, c.not_started_alarm_minutes,
                    v.registration_number
               FROM claimed c
               JOIN registry.vehicles v ON v.id = c.vehicle_id
              ORDER BY c.depart_at;
             """,
            new { Now = now, BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new DueScheduleAlarm(
                row.Id,
                row.FleetId,
                row.VehicleId,
                row.RegistrationNumber,
                row.DepartAt,
                row.NotStartedAlarmMinutes,
                [],
                [])),
        ];
    }

    public async Task<IReadOnlyList<Guid>> ListMemberIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid fleetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var members = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT user_id FROM iam.fleet_members WHERE fleet_id = @FleetId;",
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. members];
    }

    /// <summary>The claim's row, before the recipients are resolved onto it.</summary>
    private sealed record ClaimedAlarmRow(
        Guid Id,
        Guid FleetId,
        Guid VehicleId,
        DateTimeOffset DepartAt,
        short NotStartedAlarmMinutes,
        string RegistrationNumber);
}

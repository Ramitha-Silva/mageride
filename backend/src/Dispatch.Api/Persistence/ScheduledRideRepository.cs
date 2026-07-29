using Dapper;
using MageRide.Dispatch.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// <c>dispatch.scheduled_rides</c> and <c>dispatch.job_board_intents</c> (migrations 0704, 0713).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheduled rides live here and dispatch-svc owns them.</b> ADD §1.11 AL-36 and one D3' Δ
/// heading name a <c>scheduling-svc</c> over <c>scheduling.scheduled_rides</c>; no such service and
/// no such schema exist anywhere else in the specs, and ADD §9.1, D4' §6, <c>server_db_schema.md</c>
/// §6 and D3' Part 2 all place them here. Planner finding 2, and 0704's own header.
/// </para>
/// <para>
/// <b>This table is its own durable timer.</b> The T-30 trigger needs no <c>dispatch.timers</c> row:
/// <c>ix_sched_due</c> (0704) is a partial index on <c>pickup_time WHERE status = 'SCHEDULED'</c>,
/// which is exactly "the next thing to fire", and the status column is the claim. A timer row would
/// be a second copy of a fact this table already holds, with its own way of falling out of step —
/// and <c>dispatch.timers.ride_id</c> has a foreign key onto <c>rides.rides</c>, which is precisely
/// the row that does not exist yet at T-30.
/// </para>
/// </remarks>
public interface IScheduledRideRepository
{
    Task<ScheduledRideRow> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        GeoPoint pickup,
        GeoPoint dropoff,
        string vehicleType,
        string paymentMethod,
        DateTimeOffset pickupTime,
        CancellationToken cancellationToken);

    Task<ScheduledRideRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid scheduledRideId, CancellationToken cancellationToken);

    /// <summary>The scheduled booking a materialised ride came from, if it came from one.</summary>
    /// <remarks>
    /// Read on every dispatch round so the cascade knows to stay inside the intent list (D5' §3.7)
    /// without any caller having to carry the fact. <c>ux_sched_ride</c> (0713) is the index.
    /// </remarks>
    Task<ScheduledRideRow?> FindByRideAsync(
        NpgsqlConnection connection, Guid rideId, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws a booking that has not been dispatched. Returns <see langword="false"/> when the
    /// row has already moved on, which the caller answers <c>409 illegal-transition</c> — from
    /// <c>DISPATCHED</c> the cancellation belongs to ride-svc's penalty matrix.
    /// </summary>
    Task<bool> CancelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid scheduledRideId,
        string toStatus,
        CancellationToken cancellationToken);

    /// <summary>The D-06 Job Board page for one driver.</summary>
    Task<IReadOnlyList<JobBoardEntry>> JobBoardAsync(
        NpgsqlConnection connection, JobBoardQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Records this driver's intent. Returns the intent id — the existing one on a repeat, because
    /// <c>ux_job_board_intent</c> makes re-posting an upsert rather than a second row (US-6A.5).
    /// </summary>
    Task<Guid> AddIntentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid scheduledRideId,
        Guid driverId,
        CancellationToken cancellationToken);

    /// <summary>Every driver who has posted intent on a scheduled ride, oldest first.</summary>
    Task<IReadOnlyList<Guid>> IntentDriversAsync(
        NpgsqlConnection connection, Guid scheduledRideId, CancellationToken cancellationToken);

    /// <summary>
    /// The scheduled rides a driver has been assigned — US-6A.15's "upcoming". Assignment is the
    /// offer this service made: <c>dispatch.offers</c> is where the fact lives, so no column on the
    /// booking duplicates it.
    /// </summary>
    Task<IReadOnlyList<ScheduledRideRow>> AssignedToDriverAsync(
        NpgsqlConnection connection, Guid driverId, ScheduledRideCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Leases the bookings whose pickup is within <paramref name="leadTime"/>, so two replicas split
    /// a batch rather than both materialising it.
    /// </summary>
    /// <remarks>
    /// The claim is <c>FOR UPDATE SKIP LOCKED</c> and nothing else — no status flip, no fire time
    /// pushed out. The materialisation that follows is idempotent by <c>ux_rides_idem</c> on
    /// ride-svc's side, so a worker that dies mid-flight leaves a row that is still due and still
    /// claimable, which is the failure this is allowed to have.
    /// </remarks>
    Task<IReadOnlyList<ScheduledRideRow>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TimeSpan leadTime,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>Binds the booking to the ride it became. Conditional, so a redelivery is a no-op.</summary>
    Task<bool> MarkDispatchedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid scheduledRideId,
        Guid rideId,
        CancellationToken cancellationToken);
}

/// <param name="Origin">The coordinate the driver asked from — the contract's required lat/lng.</param>
/// <param name="RadiusM">D-06's 30 km, unless the caller narrows it.</param>
public sealed record JobBoardQuery(
    Guid DriverId, GeoPoint Origin, int RadiusM, DateTimeOffset NotBefore, ScheduledRideCursor? After, int Limit);

/// <summary>The position a Job Board or upcoming-rides cursor points at.</summary>
/// <remarks>
/// Both lists are ordered by <c>(pickup_time, id)</c> — soonest first — so the pair is what a page
/// boundary is. The id breaks the tie between two rides booked for the same minute, which is what
/// makes the paging stable rather than merely ordered.
/// </remarks>
public sealed record ScheduledRideCursor(DateTimeOffset PickupTime, Guid Id);

/// <inheritdoc cref="IScheduledRideRepository"/>
public sealed class ScheduledRideRepository : IScheduledRideRepository
{
    /// <summary>
    /// The Job Board query's flat shape. A ride plus the three numbers the card carries, read in
    /// one statement and split in C# — Dapper's multi-mapper cannot build a record whose first
    /// constructor parameter is itself a record.
    /// </summary>
    private sealed record JobBoardProjection(
        Guid Id,
        Guid? RideId,
        Guid PassengerId,
        GeoPoint Pickup,
        GeoPoint Dropoff,
        string VehicleType,
        string PaymentMethod,
        DateTimeOffset PickupTime,
        string Status,
        DateTimeOffset CreatedAt,
        double DistanceM,
        int IntentCount,
        bool HasIntent);

    private const string Columns =
        """
        id AS Id,
        ride_id AS RideId,
        passenger_id AS PassengerId,
        pickup_geo AS Pickup,
        dropoff_geo AS Dropoff,
        vehicle_type AS VehicleType,
        payment_method AS PaymentMethod,
        pickup_time AS PickupTime,
        status AS Status,
        created_at AS CreatedAt
        """;

    /// <summary>
    /// The same list, qualified. Every one of <c>id</c>, <c>ride_id</c> and <c>status</c> exists on
    /// <c>dispatch.offers</c> too, so the joined read cannot use the bare list — Postgres answers
    /// an ambiguous column reference, which reaches a caller as a 500 rather than as an empty page.
    /// </summary>
    private const string QualifiedColumns =
        """
        s.id AS Id,
        s.ride_id AS RideId,
        s.passenger_id AS PassengerId,
        s.pickup_geo AS Pickup,
        s.dropoff_geo AS Dropoff,
        s.vehicle_type AS VehicleType,
        s.payment_method AS PaymentMethod,
        s.pickup_time AS PickupTime,
        s.status AS Status,
        s.created_at AS CreatedAt
        """;

    public Task<ScheduledRideRow> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid passengerId,
        GeoPoint pickup,
        GeoPoint dropoff,
        string vehicleType,
        string paymentMethod,
        DateTimeOffset pickupTime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleAsync<ScheduledRideRow>(new CommandDefinition(
            $"""
             INSERT INTO dispatch.scheduled_rides
               (passenger_id, pickup_geo, dropoff_geo, vehicle_type, payment_method, pickup_time, status)
             VALUES
               (@PassengerId, @Pickup, @Dropoff, @VehicleType, @PaymentMethod, @PickupTime,
                '{ScheduledRideStatuses.Scheduled}')
             RETURNING {Columns};
             """,
            new
            {
                PassengerId = passengerId,
                Pickup = pickup,
                Dropoff = dropoff,
                VehicleType = vehicleType,
                PaymentMethod = paymentMethod,
                PickupTime = pickupTime,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<ScheduledRideRow?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid scheduledRideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<ScheduledRideRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dispatch.scheduled_rides WHERE id = @Id;",
            new { Id = scheduledRideId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<ScheduledRideRow?> FindByRideAsync(
        NpgsqlConnection connection, Guid rideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<ScheduledRideRow>(new CommandDefinition(
            $"SELECT {Columns} FROM dispatch.scheduled_rides WHERE ride_id = @RideId;",
            new { RideId = rideId },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> CancelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid scheduledRideId,
        string toStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Guarded on SCHEDULED, so a booking the T-30 sweep has already materialised cannot be
        // withdrawn from under a driver who is looking at the offer. That cancellation is
        // ride-svc's, and it carries the §11.12 penalty this one deliberately does not.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE dispatch.scheduled_rides SET status = @ToStatus
              WHERE id = @Id AND status = '{ScheduledRideStatuses.Scheduled}';
             """,
            new { Id = scheduledRideId, ToStatus = toStatus },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }

    public async Task<IReadOnlyList<JobBoardEntry>> JobBoardAsync(
        NpgsqlConnection connection, JobBoardQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        // D-06 has two anchors and they are not the same one. D5' §3.7 writes
        // `ST_DWithin(pickup, driver_home, 30 km)` **on dispatch.driver_presence**; D3' makes
        // `lat`/`lng` required query parameters, which is where the driver is standing. A driver
        // parked in Fort who lives in Negombo is in reach of both, so the predicate is the union —
        // the presence row is LEFT JOINed because a driver may read the board while offline, and
        // that must not empty it.
        //
        // `distanceM` is measured from the query origin: it is what the card shows and what the
        // driver is deciding on, and a distance measured from a home 40 km away would be a number
        // about somewhere they are not.
        var rows = await connection.QueryAsync<JobBoardProjection>(new CommandDefinition(
            $"""
             SELECT s.id AS Id,
                    s.ride_id AS RideId,
                    s.passenger_id AS PassengerId,
                    s.pickup_geo AS Pickup,
                    s.dropoff_geo AS Dropoff,
                    s.vehicle_type AS VehicleType,
                    s.payment_method AS PaymentMethod,
                    s.pickup_time AS PickupTime,
                    s.status AS Status,
                    s.created_at AS CreatedAt,
                    ST_Distance(s.pickup_geo, @Origin) AS DistanceM,
                    (SELECT count(*)::int FROM dispatch.job_board_intents i
                      WHERE i.scheduled_ride_id = s.id) AS IntentCount,
                    EXISTS (SELECT 1 FROM dispatch.job_board_intents i
                             WHERE i.scheduled_ride_id = s.id AND i.driver_id = @DriverId) AS HasIntent
               FROM dispatch.scheduled_rides s
               LEFT JOIN dispatch.driver_presence p ON p.driver_id = @DriverId
              WHERE s.status = '{ScheduledRideStatuses.Scheduled}'
                AND s.pickup_time > @NotBefore
                AND (ST_DWithin(s.pickup_geo, @Origin, @RadiusM)
                     OR (p.driver_home IS NOT NULL
                         AND ST_DWithin(s.pickup_geo, p.driver_home, @RadiusM)))
                AND (@AfterTime::timestamptz IS NULL
                     OR (s.pickup_time, s.id) > (@AfterTime::timestamptz, @AfterId::uuid))
              ORDER BY s.pickup_time, s.id
              LIMIT @Limit;
             """,
            new
            {
                query.DriverId,
                Origin = query.Origin,
                RadiusM = (double)query.RadiusM,
                query.NotBefore,
                AfterTime = query.After?.PickupTime,
                AfterId = query.After?.Id,
                query.Limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows.Select(static row => new JobBoardEntry(
            new ScheduledRideRow(
                row.Id, row.RideId, row.PassengerId, row.Pickup, row.Dropoff, row.VehicleType,
                row.PaymentMethod, row.PickupTime, row.Status, row.CreatedAt),
            row.DistanceM,
            row.IntentCount,
            row.HasIntent))];
    }

    public async Task<Guid> AddIntentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid scheduledRideId,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // DO UPDATE rather than DO NOTHING purely so the id comes back on a repeat: US-6A.5 makes a
        // second post a no-op replay, and a caller that had to issue a second SELECT to find that
        // out would race a concurrent one. `ts` is deliberately NOT refreshed — the intent's age is
        // its position in the queue, and re-tapping the button should not buy a driver a better one.
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO dispatch.job_board_intents (scheduled_ride_id, driver_id)
            VALUES (@ScheduledRideId, @DriverId)
                ON CONFLICT (scheduled_ride_id, driver_id)
                DO UPDATE SET scheduled_ride_id = EXCLUDED.scheduled_ride_id
            RETURNING id;
            """,
            new { ScheduledRideId = scheduledRideId, DriverId = driverId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<Guid>> IntentDriversAsync(
        NpgsqlConnection connection, Guid scheduledRideId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT driver_id FROM dispatch.job_board_intents
             WHERE scheduled_ride_id = @ScheduledRideId
             ORDER BY ts, id;
            """,
            new { ScheduledRideId = scheduledRideId },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<ScheduledRideRow>> AssignedToDriverAsync(
        NpgsqlConnection connection, Guid driverId, ScheduledRideCursor? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // "Assigned" is an offer this service made and has not settled against them — OFFERED while
        // the driver is deciding, ACCEPTED once they have. A DECLINED or EXPIRED offer is not an
        // assignment, and `released_at IS NULL` keeps a finished ride out the same way
        // ux_offers_driver_live does.
        var rows = await connection.QueryAsync<ScheduledRideRow>(new CommandDefinition(
            $"""
             SELECT {QualifiedColumns}
               FROM dispatch.scheduled_rides s
               JOIN dispatch.offers o ON o.ride_id = s.ride_id
              WHERE o.driver_id = @DriverId
                AND o.status = ANY(@LiveStatuses)
                AND o.released_at IS NULL
                AND s.status = '{ScheduledRideStatuses.Dispatched}'
                AND (@AfterTime::timestamptz IS NULL
                     OR (s.pickup_time, s.id) > (@AfterTime::timestamptz, @AfterId::uuid))
              ORDER BY s.pickup_time, s.id
              LIMIT @Limit;
             """,
            new
            {
                DriverId = driverId,
                LiveStatuses = OfferStatuses.Live.ToArray(),
                AfterTime = after?.PickupTime,
                AfterId = after?.Id,
                Limit = limit,
            },
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<ScheduledRideRow>> ClaimDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TimeSpan leadTime,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var rows = await connection.QueryAsync<ScheduledRideRow>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM dispatch.scheduled_rides
              WHERE status = '{ScheduledRideStatuses.Scheduled}'
                AND pickup_time - make_interval(secs => @LeadSeconds) <= now()
              ORDER BY pickup_time
              LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED;
             """,
            new { LeadSeconds = leadTime.TotalSeconds, BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<bool> MarkDispatchedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid scheduledRideId,
        Guid rideId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE dispatch.scheduled_rides
                SET ride_id = @RideId, status = '{ScheduledRideStatuses.Dispatched}'
              WHERE id = @Id AND status = '{ScheduledRideStatuses.Scheduled}';
             """,
            new { Id = scheduledRideId, RideId = rideId },
            transaction,
            cancellationToken: cancellationToken));

        return affected == 1;
    }
}

using System.Text.Json;
using Dapper;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;
using MageRide.TripState.Domain;
using Npgsql;

namespace MageRide.TripState.Persistence;

/// <summary>
/// <c>trips.events</c> (0502) — the domain log of what happened on a session.
/// </summary>
/// <remarks>
/// <b>Not the outbox.</b> <c>trips.outbox</c> is a delivery queue that drains to <c>trip.events</c>
/// and is emptied as it goes; this is the audit trail a support engineer reads six weeks later to
/// find out why a bus stopped broadcasting. Both are written in the same transaction as the state
/// change, and they carry different things: an ignition event that changed nothing is recorded
/// here and published nowhere.
/// </remarks>
public interface ITripEventRepository
{
    Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string kind,
        object payload,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>The log for one session, newest first — what a support view shows.</summary>
    Task<IReadOnlyList<(string Kind, string Payload, DateTimeOffset Ts)>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITripEventRepository"/>
public sealed class TripEventRepository : ITripEventRepository
{
    public async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string kind,
        object payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO trips.events (session_id, kind, payload, ts)
            VALUES (@SessionId, @Kind, @Payload::jsonb, @Now);
            """,
            new
            {
                SessionId = sessionId,
                Kind = kind,
                Payload = JsonSerializer.Serialize(payload, MageRideJson.StorageOptions),
                Now = now,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<(string Kind, string Payload, DateTimeOffset Ts)>> ListAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = await connection.QueryAsync<(string, string, DateTimeOffset)>(new CommandDefinition(
            """
            SELECT kind, payload::text, ts FROM trips.events
             WHERE session_id = @SessionId ORDER BY ts DESC, id DESC;
            """,
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

/// <summary><c>trips.ratings</c> (0502) — both directions of the journey rating.</summary>
public interface IRatingRepository
{
    /// <summary>
    /// Records a rating, or <see langword="null"/> when this rater has already rated this session
    /// in this direction.
    /// </summary>
    /// <remarks>
    /// The duplicate is settled by <c>ux_ratings_once</c> (0504) through <c>ON CONFLICT DO
    /// NOTHING</c>, not by a prior read: two taps on a flaky connection both see "no rating yet".
    /// </remarks>
    Task<SessionRating?> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        Guid raterId,
        Guid rateeId,
        short stars,
        string? comment,
        string direction,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRatingRepository"/>
public sealed class RatingRepository : IRatingRepository
{
    public Task<SessionRating?> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        Guid raterId,
        Guid rateeId,
        short stars,
        string? comment,
        string direction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<SessionRating>(new CommandDefinition(
            """
            INSERT INTO trips.ratings (subject_kind, subject_id, rater_id, ratee_id, stars, comment, direction)
            VALUES ('session', @SessionId, @RaterId, @RateeId, @Stars, @Comment, @Direction)
            ON CONFLICT (subject_kind, subject_id, rater_id, direction) DO NOTHING
            RETURNING id, subject_id, rater_id, ratee_id, stars, comment, direction, created_at;
            """,
            new
            {
                SessionId = sessionId,
                RaterId = raterId,
                RateeId = rateeId,
                Stars = stars,
                Comment = comment,
                Direction = direction,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}

/// <summary>
/// The read-only window onto registry-svc's eligibility projection.
/// </summary>
/// <remarks>
/// <c>registry.driver_eligible_vehicles</c> (migration 0310) is "the one answer to which vehicles
/// may this driver operate", and the C028 handoff names this service as one of its three
/// consumers precisely so the three cannot derive the rule differently. The raw columns come back
/// with it because each consumer maps its own errors — an unapproved vehicle is
/// <c>vehicle-not-approved</c> here, and a pre-filtered read would have collapsed that into
/// "no such vehicle".
/// </remarks>
public interface IVehicleLookupRepository
{
    Task<EligibleVehicle?> FindEligibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The driver a tracker-equipped vehicle auto-starts for, when there is exactly one candidate.
    /// </summary>
    /// <remarks>
    /// Ignition carries no driver — a tracker knows its vehicle and nothing else (US-3.22: "the
    /// mobile app is not needed"). The owner is the answer whenever the vehicle has one, and a
    /// Mode A bus registered to a fleet account resolves to that account. When it cannot be
    /// resolved the auto-start is declined rather than guessed: a session attributed to the wrong
    /// driver takes their D-03 mutex and blocks a journey they are trying to start themselves.
    /// </remarks>
    Task<EligibleVehicle?> FindTrackerVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehicleLookupRepository"/>
public sealed class VehicleLookupRepository : IVehicleLookupRepository
{
    private const string Columns =
        """
        vehicle_id AS "VehicleId", driver_id AS "DriverId", source AS "Source", fleet_id AS "FleetId",
        owner_id AS "OwnerId", mode AS "Mode", status AS "Status", dispatch_state AS "DispatchState",
        is_go_live_eligible AS "IsGoLiveEligible"
        """;

    public Task<EligibleVehicle?> FindEligibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid driverId,
        Guid vehicleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The view is DISTINCT ON (driver_id, vehicle_id), so this is single-row by construction
        // even for a driver who both owns a vehicle and is assigned to it.
        return connection.QuerySingleOrDefaultAsync<EligibleVehicle>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM registry.driver_eligible_vehicles
              WHERE driver_id = @DriverId AND vehicle_id = @VehicleId;
             """,
            new { DriverId = driverId, VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }

    public Task<EligibleVehicle?> FindTrackerVehicleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid vehicleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The owner's row specifically ('owned'), not whichever row sorts first: an assigned
        // driver has a person behind them who may be off shift, while the owner is the account the
        // vehicle belongs to and is the only defensible answer when nobody has told us who is
        // driving.
        return connection.QueryFirstOrDefaultAsync<EligibleVehicle>(new CommandDefinition(
            $"""
             SELECT {Columns} FROM registry.driver_eligible_vehicles
              WHERE vehicle_id = @VehicleId AND source = 'owned'
              LIMIT 1;
             """,
            new { VehicleId = vehicleId },
            transaction,
            cancellationToken: cancellationToken));
    }
}

/// <summary>
/// Records whether a vehicle's broker session is present (R-15, T-04).
/// </summary>
/// <remarks>
/// Written by the presence subscriber and read by the sweep. It is stored on the live session
/// rather than per vehicle because that is the only place it can change anything: a last will for
/// a vehicle with no session is a fact about a parked bus, and there is nothing to end.
/// </remarks>
public interface IVehiclePresenceStore
{
    /// <summary>Stamps the vehicle's live session with the instant its last will arrived.</summary>
    /// <remarks>Idempotent — a redelivered last will keeps the first instant, so the grace window
    /// is measured from when the vehicle actually went away and not from the retry.</remarks>
    Task<bool> MarkOfflineAsync(Guid vehicleId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Clears it when the vehicle reconnects, so a tunnel costs nothing.</summary>
    Task<bool> MarkOnlineAsync(Guid vehicleId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVehiclePresenceStore"/>
public sealed class VehiclePresenceStore(INpgsqlConnectionFactory connectionFactory) : IVehiclePresenceStore
{
    public async Task<bool> MarkOfflineAsync(Guid vehicleId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // COALESCE keeps the earliest instant: EMQX redelivers an unacknowledged last will, and
        // taking the newest would push the grace window forward on every retry — a vehicle that
        // never comes back would never be swept.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE trips.sessions
                SET offline_since = COALESCE(offline_since, @At)
              WHERE vehicle_id = @VehicleId AND state = '{SessionStates.Active}';
             """,
            new { VehicleId = vehicleId, At = at },
            cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<bool> MarkOnlineAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE trips.sessions
                SET offline_since = NULL
              WHERE vehicle_id = @VehicleId AND state = '{SessionStates.Active}' AND offline_since IS NOT NULL;
             """,
            new { VehicleId = vehicleId },
            cancellationToken: cancellationToken));

        return affected > 0;
    }
}

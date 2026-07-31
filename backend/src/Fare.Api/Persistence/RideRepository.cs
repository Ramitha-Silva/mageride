using Dapper;
using MageRide.Fare.Distance;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;

namespace MageRide.Fare.Persistence;

/// <summary>
/// What fare-svc needs to know about a ride to price it. Read-only — ride-svc owns the row (R-01).
/// </summary>
/// <param name="RequestedAt">
/// The instant the ride was created. The tariff is resolved <b>here</b>, not at completion: a rate
/// published while somebody was in the car must not change what they are charged.
/// </param>
/// <param name="FareEstimateMinor">
/// What the passenger was quoted, from the token ride-svc verified. The D5' §1.2 fallback when the
/// track cannot be measured.
/// </param>
public sealed record RideFacts(
    Guid RideId,
    Guid PassengerId,
    Guid BookerId,
    Guid? AcceptedDriverId,
    Guid? AcceptedVehicleId,
    string VehicleType,
    string State,
    short Kind,
    string PaymentMethod,
    long? FareEstimateMinor,
    string Currency,
    GeoPoint PickupGeo,
    GeoPoint DropoffGeo,
    DateTimeOffset RequestedAt,
    DateTimeOffset? TerminalAt);

/// <summary>The window a ride was actually under way for, from its audited transitions.</summary>
/// <param name="StartedAt">
/// When the ride entered <c>InProgress</c>, or <see langword="null"/> if it never did — a ride
/// cancelled before the driver started has no travelled track and no distance to measure.
/// </param>
public sealed record RideTravelWindow(DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);

/// <summary>The <c>rides.*</c> reads. <b>This service writes nothing here.</b></summary>
/// <remarks>
/// The same read-only cross-context read subscription-svc makes into <c>registry.*</c> and
/// ride-svc's own <c>DriverSummaryRepository</c> makes into <c>registry.vehicles</c>: two indexed
/// statements on a path a driver is waiting on, rather than a synchronous hop to a service that
/// would have to answer both.
/// </remarks>
internal interface IRideRepository
{
    Task<RideFacts?> ReadAsync(Guid rideId, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>InProgress</c> → terminal window, which is the only interval whose positions belong to
    /// the fare.
    /// </summary>
    /// <remarks>
    /// Read from <c>rides.transitions</c> rather than from a column, because there is no column: the
    /// audit trail is where "when did this ride actually start moving" lives, and it is immutable
    /// (0602 has no UPDATE path), so the window cannot drift after the fact. Positions before the
    /// start are the driver approaching the pickup — distance the passenger did not travel and must
    /// not be charged for.
    /// </remarks>
    Task<RideTravelWindow> ReadTravelWindowAsync(Guid rideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRideRepository"/>
internal sealed class RideRepository(INpgsqlConnectionFactory connections) : IRideRepository
{
    public async Task<RideFacts?> ReadAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<RideFacts>(new CommandDefinition(
            """
            SELECT r.id AS ride_id, r.passenger_id, r.booker_id, r.accepted_driver_id,
                   r.accepted_vehicle_id, r.vehicle_type, r.state, r.kind, r.payment_method,
                   r.fare_estimate_minor, r.currency, r.pickup_geo, r.dropoff_geo,
                   r.created_at AS requested_at, r.terminal_at
              FROM rides.rides r
             WHERE r.id = @RideId;
            """,
            new { RideId = rideId },
            cancellationToken: cancellationToken));
    }

    public async Task<RideTravelWindow> ReadTravelWindowAsync(Guid rideId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // The FIRST InProgress and the LAST transition out of it. A ride only reaches InProgress
        // once in the §6 machine, but the audit table is deliberately unconstrained (0602) and
        // min/max cost nothing over ix_transitions_ride.
        return await connection.QuerySingleAsync<RideTravelWindow>(new CommandDefinition(
            """
            SELECT min(ts) FILTER (WHERE to_state = 'InProgress')  AS started_at,
                   max(ts) FILTER (WHERE to_state <> 'InProgress'
                                     AND ts > coalesce(
                                       (SELECT min(t2.ts) FROM rides.transitions t2
                                         WHERE t2.ride_id = @RideId AND t2.to_state = 'InProgress'),
                                       'infinity'::timestamptz)) AS ended_at
              FROM rides.transitions
             WHERE ride_id = @RideId;
            """,
            new { RideId = rideId },
            cancellationToken: cancellationToken));
    }
}

/// <summary>The <c>telemetry.positions</c> read behind E-04.</summary>
/// <remarks>
/// <b>Raw samples, not the continuous aggregate.</b> Migration 1802's <c>positions_1m</c> is one
/// point per minute — the granularity query-svc draws a trip line from — and chaining sixty-second
/// chords across a route with turns loses a third of the distance. The fare is charged on this
/// number, so it reads the rows the tracker actually sent.
/// </remarks>
internal interface ITrackRepository
{
    Task<IReadOnlyList<TrackSample>> ReadAsync(
        Guid vehicleId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITrackRepository"/>
internal sealed class TrackRepository(INpgsqlConnectionFactory connections) : ITrackRepository
{
    public async Task<IReadOnlyList<TrackSample>> ReadAsync(
        Guid vehicleId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // By vehicle and time window rather than by trip_id: telemetry.positions.trip_id is set by
        // the Mode A/B session plane and is null for a Mode C ride (the C042 handoff's gap (a) —
        // no Mode C track is stored anywhere). The hypertable is partitioned on sample_ts, so a
        // bounded window is a chunk-pruned scan rather than a search of the vehicle's history.
        //
        // accuracy_m and the coordinates are REAL/DOUBLE PRECISION; the cast to double precision
        // keeps Dapper's exact-type constructor binding satisfied.
        var rows = await connection.QueryAsync<TrackSample>(new CommandDefinition(
            """
            SELECT sample_ts, lat, lng, accuracy_m::double precision AS accuracy_m
              FROM telemetry.positions
             WHERE vehicle_id = @VehicleId
               AND sample_ts >= @From
               AND sample_ts <= @To
             ORDER BY sample_ts, seq
             LIMIT @Limit;
            """,
            new { VehicleId = vehicleId, From = from, To = to, Limit = limit },
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}

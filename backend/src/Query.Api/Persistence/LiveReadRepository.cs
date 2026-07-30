using Dapper;
using MageRide.Shared.Primitives;

namespace MageRide.Query.Persistence;

/// <summary>
/// The registry facts a map marker's popup needs (US-7.4, US-7.12).
/// </summary>
/// <param name="VehicleId">The vehicle.</param>
/// <param name="RegistrationNumber">Its plate.</param>
/// <param name="VehicleType">Canonical type (AL-09) — the authoritative copy, unlike the sample's.</param>
/// <param name="Mode">A, B or C — likewise.</param>
/// <param name="DriverName">The name registry-svc holds for the driver (US-2.12).</param>
public sealed record VehicleIdentity(
    Guid VehicleId, string RegistrationNumber, string VehicleType, string Mode, string? DriverName);

/// <summary>
/// A ride the caller is a party to, and the vehicle it has engaged.
/// </summary>
/// <param name="RideId">The ride.</param>
/// <param name="State">Its <c>rides.rides.state</c> — which end of the journey the ETA points at.</param>
/// <param name="Pickup">Where the passenger is picked up.</param>
/// <param name="Dropoff">Where they are going.</param>
/// <param name="DriverId">The accepted driver, or <see langword="null"/> before an accept.</param>
public sealed record OwnRide(Guid RideId, string State, GeoPoint Pickup, GeoPoint Dropoff, Guid? DriverId);

/// <summary>
/// The Postgres side of the live map: identity for a marker, and the caller's own ride.
/// </summary>
public interface ILiveReadRepository
{
    /// <summary>Registry identity for specific vehicles. Absent ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, VehicleIdentity>> ReadIdentitiesAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="rideIds"/> <paramref name="userId"/> is a party to, with the detail
    /// an ETA needs.
    /// </summary>
    /// <remarks>
    /// The predicate is the participant test and nothing else: the caller supplies ride ids read out
    /// of <c>veh:engaged:{vehicleId}</c>, so "is this vehicle on a hire" has already been answered and
    /// only "is that hire yours" is left. Keeping the two apart is what stops this service holding a
    /// second copy of ride-svc's eighteen-state machine.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, OwnRide>> ReadOwnRidesAsync(
        Guid userId, IReadOnlyCollection<Guid> rideIds, CancellationToken cancellationToken);

    /// <summary>
    /// Vehicles currently running <paramref name="routeNumber"/> (US-7.9), or <see langword="null"/>
    /// when no route carries that number at all.
    /// </summary>
    /// <remarks>
    /// The distinction matters to the contract: an unknown route number is <c>404</c>, a known route
    /// with nothing running on it is <c>200</c> with an empty list — which is what US-7.14's "no
    /// vehicles of your type are active" message is drawn from.
    /// </remarks>
    Task<IReadOnlyList<Guid>?> ReadRouteVehiclesAsync(string routeNumber, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILiveReadRepository"/>
/// <remarks>
/// Every read here is <see cref="ReadConsistency.Eventual"/>: a plate and a driver's name are
/// registry facts measured in months, and a route's membership changes when a driver presses Start
/// Journey — seconds of replica lag on either is invisible against a map that refreshes every two
/// seconds anyway.
/// </remarks>
public sealed class LiveReadRepository(IQueryConnectionFactory connections) : ILiveReadRepository
{
    /// <summary>
    /// Identity for a set of vehicles.
    /// </summary>
    /// <remarks>
    /// <c>= ANY(@VehicleIds)</c> rather than an <c>IN</c> list Dapper expands: one plan for every
    /// call size, which matters on an endpoint whose parameter count is however many vehicles happen
    /// to be on somebody's screen.
    /// </remarks>
    private const string IdentitiesSql =
        """
        SELECT id                  AS VehicleId,
               registration_number AS RegistrationNumber,
               vehicle_type        AS VehicleType,
               mode                AS Mode,
               driver_name         AS DriverName
          FROM registry.vehicles
         WHERE id = ANY(@VehicleIds);
        """;

    private const string OwnRidesSql =
        """
        SELECT id                                    AS RideId,
               state                                 AS State,
               ST_Y(pickup_geo::geometry)            AS PickupLat,
               ST_X(pickup_geo::geometry)            AS PickupLng,
               ST_Y(dropoff_geo::geometry)           AS DropoffLat,
               ST_X(dropoff_geo::geometry)           AS DropoffLng,
               accepted_driver_id                    AS DriverId
          FROM rides.rides
         WHERE id = ANY(@RideIds)
           -- P-01/P-03: on a proxy booking the booker, the registered rider and the passenger of
           -- record can be three different accounts, and all three are watching the same car.
           AND (passenger_id = @UserId OR booker_id = @UserId OR rider_id = @UserId);
        """;

    /// <summary>
    /// A Mode A vehicle's route is declared when its driver starts the tracking session
    /// (<c>trips.sessions.route_id</c>, US-5.1) — there is no route column on the vehicle, because a
    /// bus is reassigned between routes and the vehicle row would then be wrong for every past
    /// journey.
    /// </summary>
    private const string RouteVehiclesSql =
        """
        WITH matched AS (
            SELECT id FROM spatial.routes WHERE lower(route_number) = lower(@RouteNumber)
        )
        SELECT s.vehicle_id
          FROM trips.sessions s
          JOIN matched m ON m.id = s.route_id
         WHERE s.state = 'ACTIVE';
        """;

    private const string RouteExistsSql =
        "SELECT EXISTS (SELECT 1 FROM spatial.routes WHERE lower(route_number) = lower(@RouteNumber));";

    public async Task<IReadOnlyDictionary<Guid, VehicleIdentity>> ReadIdentitiesAsync(
        IReadOnlyCollection<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleIds);

        if (vehicleIds.Count == 0)
        {
            return new Dictionary<Guid, VehicleIdentity>();
        }

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var rows = await connection.QueryAsync<VehicleIdentity>(
            new CommandDefinition(
                IdentitiesSql,
                new { VehicleIds = vehicleIds.ToArray() },
                cancellationToken: cancellationToken));

        return rows.ToDictionary(static row => row.VehicleId);
    }

    public async Task<IReadOnlyDictionary<Guid, OwnRide>> ReadOwnRidesAsync(
        Guid userId, IReadOnlyCollection<Guid> rideIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rideIds);

        if (rideIds.Count == 0)
        {
            return new Dictionary<Guid, OwnRide>();
        }

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var rows = await connection.QueryAsync<OwnRideRow>(
            new CommandDefinition(
                OwnRidesSql,
                new { UserId = userId, RideIds = rideIds.ToArray() },
                cancellationToken: cancellationToken));

        return rows.ToDictionary(
            static row => row.RideId,
            static row => new OwnRide(
                row.RideId,
                row.State,
                new GeoPoint(row.PickupLat, row.PickupLng),
                new GeoPoint(row.DropoffLat, row.DropoffLng),
                row.DriverId));
    }

    public async Task<IReadOnlyList<Guid>?> ReadRouteVehiclesAsync(
        string routeNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeNumber);

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var vehicles = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                RouteVehiclesSql, new { RouteNumber = routeNumber }, cancellationToken: cancellationToken));

        var ids = vehicles.ToArray();

        if (ids.Length > 0)
        {
            return ids;
        }

        // Empty could mean "no such route" or "nothing running on it", and the contract answers those
        // differently. Only asked when the first query found nothing, so the common case is one query.
        var exists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                RouteExistsSql, new { RouteNumber = routeNumber }, cancellationToken: cancellationToken));

        return exists ? ids : null;
    }

    /// <summary>Dapper's shape for <see cref="OwnRidesSql"/>; the geography arrives as two doubles.</summary>
    private sealed record OwnRideRow(
        Guid RideId,
        string State,
        double PickupLat,
        double PickupLng,
        double DropoffLat,
        double DropoffLng,
        Guid? DriverId);
}

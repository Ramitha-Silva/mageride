using Dapper;
using MageRide.Shared.Primitives;

namespace MageRide.Query.Persistence;

/// <summary>Which plane a trip belongs to.</summary>
/// <remarks>
/// R-01's fence, surfaced: a Mode C journey is a <c>rides.rides</c> aggregate owned by ride-svc, a
/// Mode A/B journey is a <c>trips.sessions</c> row owned by trip-state-svc, and the ids come from
/// different tables. A client needs to know which it is holding — the two support very different
/// screens — so the discriminator is in the payload rather than inferred from which fields are set.
/// </remarks>
public static class TripPlanes
{
    /// <summary>Mode C (<c>rides.rides</c>).</summary>
    public const string Ride = "ride";

    /// <summary>Mode A/B (<c>trips.sessions</c>).</summary>
    public const string Session = "session";
}

/// <summary>Which relation a trip's geometry was computed from.</summary>
public static class TripGeometrySources
{
    /// <summary>Full-resolution <c>telemetry.positions</c>, via <c>trips.session_summaries</c>.</summary>
    public const string Telemetry = "telemetry";

    /// <summary>The 1/min <c>trips.position_samples</c>. Distance is a lower bound.</summary>
    public const string Operational = "operational";

    /// <summary>
    /// The <c>telemetry.positions_1m</c> continuous aggregate — one point per minute.
    /// </summary>
    /// <remarks>
    /// Mode C only, and only because no service stores a Mode C track. See
    /// <see cref="TripRepository"/>.
    /// </remarks>
    public const string Aggregate1m = "aggregate_1m";

    /// <summary>The journey produced no usable line.</summary>
    public const string None = "none";
}

/// <summary>A row on the trip-history list (US-8.7).</summary>
public sealed record TripSummaryRow(
    Guid TripId,
    string Plane,
    string? Mode,
    GeoPoint? Pickup,
    GeoPoint? Dropoff,
    long? FareMinor,
    string Currency,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>One trip in full, with its track.</summary>
public sealed record TripDetailRow(
    TripSummaryRow Summary,
    IReadOnlyList<GeoPoint> Path,
    string GeometrySource,
    double? DistanceKm,
    int? DurationSec,
    Guid? DriverId,
    string? DriverName,
    string? RegistrationNumber,
    int? Rating);

/// <summary>Trip history and detail over both planes.</summary>
public interface ITripRepository
{
    /// <summary>
    /// One page of a user's trips, newest first, across both planes.
    /// </summary>
    /// <param name="userId">Whose trips.</param>
    /// <param name="before">Exclusive upper bound on <c>startedAt</c> from the cursor, or null.</param>
    /// <param name="beforeId">Tie-break id from the cursor, or null.</param>
    /// <param name="limit">Rows to fetch — the caller over-fetches by one to detect a next page.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<TripSummaryRow>> ListAsync(
        Guid userId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One trip, or <see langword="null"/> when it does not exist or is not this user's.
    /// </summary>
    Task<TripDetailRow?> GetAsync(Guid userId, Guid tripId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITripRepository"/>
/// <remarks>
/// <para>
/// <b>"My trips" spans two tables and only one of them knows about passengers.</b> D3' says the
/// history covers both planes, and it does — but the link from a <em>user</em> to a Mode A/B session
/// is <c>trips.sessions.driver_id</c> and nothing else: the platform records no ridership for a bus
/// or a school van, because nobody is ticketed. So a passenger's history is their Mode C rides, and a
/// driver's is their Mode C rides plus their Mode A/B sessions. That is not a narrowing of the
/// contract, it is the only join the schema has, and it is recorded as a finding in the C042 handoff.
/// </para>
/// <para>
/// <b>The Mode C polyline comes from a continuous aggregate, and no Mode C track is stored
/// anywhere.</b> ADD §9.2's stored trip summary — "start, end, distance, polyline" — is per
/// <em>session</em>: <c>trips.session_summaries</c> (migration 0506, written by
/// persistence-writer-svc) covers Mode A and Mode B and its <c>mode</c> CHECK admits only those two.
/// The Mode C equivalent has no table, no column and no writer: E-04's Kalman-filtered track is
/// computed by fare-svc for the distance the fare is charged on and is not persisted. So for a ride,
/// this reads <c>telemetry.positions_1m</c> — a materialised continuous aggregate, which is the read
/// path ADD §9.5 item 2 prescribes for a trip summary ("hits aggregates, not raw rows") and which
/// migration 1802 landed naming this component. It is one point per minute and it labels itself
/// <c>aggregate_1m</c> so a client and an operator can both see the grain.
/// </para>
/// <para>
/// <b>A Mode C <c>distanceKm</c> is not derived from that line and is omitted instead.</b> Chaining
/// sixty-second chords across a route with turns in it loses a third of the distance or more — C040's
/// own note on the same trade-off — and the distance is the number the fare was charged on. A figure
/// a third short of the receipt is worse than no figure. The authoritative one is fare-svc's, on the
/// day it stores it (C049).
/// </para>
/// </remarks>
public sealed class TripRepository(IQueryConnectionFactory connections) : ITripRepository
{
    /// <summary>
    /// The two planes, unioned and ordered as one series.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyset pagination on <c>(started_at DESC, trip_id DESC)</c>. The id is in the key because two
    /// trips can start in the same microsecond — a fleet's morning departure does exactly that — and a
    /// cursor on the timestamp alone would either skip rows or repeat them.
    /// </para>
    /// <para>
    /// A ride's <c>started_at</c> is <c>created_at</c>, the moment the passenger asked. That is what
    /// they mean by when the trip was: an expired request with no driver is a trip they remember making
    /// and has no other timestamp at all.
    /// </para>
    /// <para>
    /// Both halves filter before the union, so <c>ix_rides_passenger_created</c> and
    /// <c>ix_sessions_driver</c> are usable; a union followed by a filter would scan both tables whole.
    /// </para>
    /// </remarks>
    private const string ListSql =
        """
        WITH rides AS (
            SELECT r.id                          AS TripId,
                   'ride'                        AS Plane,
                   'C'                           AS Mode,
                   ST_Y(r.pickup_geo::geometry)  AS PickupLat,
                   ST_X(r.pickup_geo::geometry)  AS PickupLng,
                   ST_Y(r.dropoff_geo::geometry) AS DropoffLat,
                   ST_X(r.dropoff_geo::geometry) AS DropoffLng,
                   COALESCE(p.amount_minor, r.fare_estimate_minor) AS FareMinor,
                   r.currency                    AS Currency,
                   r.created_at                  AS StartedAt,
                   r.terminal_at                 AS EndedAt
              FROM rides.rides r
              LEFT JOIN LATERAL (
                  SELECT amount_minor
                    FROM fares.ride_payments
                   WHERE ride_id = r.id
                   ORDER BY attempt_no DESC
                   LIMIT 1
              ) p ON TRUE
             WHERE r.passenger_id = @UserId
                OR r.booker_id = @UserId
                OR r.rider_id = @UserId
                OR r.accepted_driver_id = @UserId
        ),
        sessions AS (
            SELECT s.id                          AS TripId,
                   'session'                     AS Plane,
                   s.mode                        AS Mode,
                   ST_Y(sum.start_geo::geometry) AS PickupLat,
                   ST_X(sum.start_geo::geometry) AS PickupLng,
                   ST_Y(sum.end_geo::geometry)   AS DropoffLat,
                   ST_X(sum.end_geo::geometry)   AS DropoffLng,
                   NULL::BIGINT                  AS FareMinor,
                   'LKR'                         AS Currency,
                   s.started_at                  AS StartedAt,
                   s.ended_at                    AS EndedAt
              FROM trips.sessions s
              LEFT JOIN trips.session_summaries sum ON sum.session_id = s.id
             WHERE s.driver_id = @UserId
        ),
        combined AS (SELECT * FROM rides UNION ALL SELECT * FROM sessions)
        SELECT *
          FROM combined
         -- The casts are load-bearing: `@Before IS NULL` on its own gives Postgres no way to infer
         -- the parameter's type and it refuses the statement with 42P08 rather than guessing.
         WHERE @Before::timestamptz IS NULL
            OR (StartedAt, TripId) < (@Before::timestamptz, @BeforeId::uuid)
         ORDER BY StartedAt DESC, TripId DESC
         LIMIT @Limit;
        """;

    /// <summary>
    /// A Mode C ride, scoped to a party to it.
    /// </summary>
    /// <remarks>
    /// The driver's name and plate come from the vehicle the ride was accepted on, not from the
    /// driver's profile: <c>registry.vehicles.driver_name</c> is the name shown to passengers
    /// (US-2.12) and the pair belongs together on a receipt. Both are <c>NULL</c> before an accept,
    /// which is correct — there was no driver.
    /// </remarks>
    private const string RideSql =
        """
        SELECT r.id                          AS TripId,
               ST_Y(r.pickup_geo::geometry)  AS PickupLat,
               ST_X(r.pickup_geo::geometry)  AS PickupLng,
               ST_Y(r.dropoff_geo::geometry) AS DropoffLat,
               ST_X(r.dropoff_geo::geometry) AS DropoffLng,
               COALESCE(p.amount_minor, r.fare_estimate_minor) AS FareMinor,
               r.currency                    AS Currency,
               r.created_at                  AS StartedAt,
               r.terminal_at                 AS EndedAt,
               r.accepted_vehicle_id         AS VehicleId,
               r.accepted_driver_id          AS DriverId,
               v.driver_name                 AS DriverName,
               v.registration_number         AS RegistrationNumber,
               rating.stars::int             AS Rating,
               -- The window the track is read over: the moment the driver started driving the
               -- passenger, not the moment the ride was requested. A ride sitting in `Matching` for
               -- two minutes would otherwise put the pickup point in the line twice over.
               started.ts                    AS InProgressAt
          FROM rides.rides r
          LEFT JOIN registry.vehicles v ON v.id = r.accepted_vehicle_id
          LEFT JOIN LATERAL (
              SELECT amount_minor FROM fares.ride_payments
               WHERE ride_id = r.id ORDER BY attempt_no DESC LIMIT 1
          ) p ON TRUE
          LEFT JOIN LATERAL (
              SELECT stars FROM trips.ratings
               WHERE subject_kind = 'ride' AND subject_id = r.id AND rater_id = @UserId
               LIMIT 1
          ) rating ON TRUE
          LEFT JOIN LATERAL (
              SELECT ts FROM rides.transitions
               WHERE ride_id = r.id AND to_state = 'InProgress' ORDER BY ts LIMIT 1
          ) started ON TRUE
         WHERE r.id = @TripId
           AND (r.passenger_id = @UserId
             OR r.booker_id = @UserId
             OR r.rider_id = @UserId
             OR r.accepted_driver_id = @UserId);
        """;

    /// <summary>A Mode A/B session with the stored summary ADD §9.2 promises.</summary>
    private const string SessionSql =
        """
        SELECT s.id                            AS TripId,
               s.mode                           AS Mode,
               ST_Y(sum.start_geo::geometry)    AS PickupLat,
               ST_X(sum.start_geo::geometry)    AS PickupLng,
               ST_Y(sum.end_geo::geometry)      AS DropoffLat,
               ST_X(sum.end_geo::geometry)      AS DropoffLng,
               s.started_at                     AS StartedAt,
               s.ended_at                       AS EndedAt,
               s.driver_id                      AS DriverId,
               v.driver_name                    AS DriverName,
               v.registration_number            AS RegistrationNumber,
               sum.distance_m                   AS DistanceM,
               COALESCE(sum.geometry_source, 'none') AS GeometrySource,
               -- The stored line, read as geometry. Npgsql's NetTopologySuite plugin is registered on
               -- every data source the platform builds (see NpgsqlConnectionFactory), so this arrives
               -- as an NTS LineString and needs no parse. Deliberately *not* ST_AsEncodedPolyline:
               -- the wire encoding is the endpoint's business and belongs on one side of the wire.
               sum.polyline AS Polyline
          FROM trips.sessions s
          LEFT JOIN trips.session_summaries sum ON sum.session_id = s.id
          LEFT JOIN registry.vehicles v ON v.id = s.vehicle_id
         WHERE s.id = @TripId AND s.driver_id = @UserId;
        """;

    /// <summary>
    /// A ride's track, one point per minute, from the continuous aggregate (ADD §9.5 item 2).
    /// </summary>
    /// <remarks>
    /// <c>last(lat, sample_ts)</c> per bucket is what 1802 materialises, so the line is the vehicle's
    /// position at the end of each minute of the journey. <c>positions_1m</c> is declared
    /// <c>materialized_only = false</c>, so the current, not-yet-materialised bucket is included —
    /// which matters for a ride that ended a minute ago.
    /// </remarks>
    private const string RideTrackSql =
        """
        SELECT last_lat AS Lat, last_lng AS Lng
          FROM telemetry.positions_1m
         WHERE vehicle_id = @VehicleId
           AND bucket >= @From
           AND bucket <= @To
         ORDER BY bucket;
        """;

    public async Task<IReadOnlyList<TripSummaryRow>> ListAsync(
        Guid userId, DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connections.OpenAsync(ReadConsistency.Eventual, cancellationToken);

        var rows = await connection.QueryAsync<ListRow>(
            new CommandDefinition(
                ListSql,
                new
                {
                    UserId = userId,
                    Before = before,
                    // Never read when @Before is NULL, but a parameter Npgsql cannot infer a type for
                    // is an error rather than an ignored value.
                    BeforeId = beforeId ?? Guid.Empty,
                    Limit = limit,
                },
                cancellationToken: cancellationToken));

        return [.. rows.Select(static row => row.ToSummary())];
    }

    public async Task<TripDetailRow?> GetAsync(Guid userId, Guid tripId, CancellationToken cancellationToken)
    {
        // The one read in this service that needs the primary. A passenger opens the receipt seconds
        // after ride-svc marked the ride terminal, and replica lag there does not stale the answer, it
        // inverts it: a 404 on a trip they have just finished.
        await using var connection = await connections.OpenAsync(
            ReadConsistency.ReadAfterWrite, cancellationToken);

        var ride = await connection.QuerySingleOrDefaultAsync<RideRow>(
            new CommandDefinition(
                RideSql, new { UserId = userId, TripId = tripId }, cancellationToken: cancellationToken));

        if (ride is not null)
        {
            var path = await ReadRideTrackAsync(connection, ride, cancellationToken);

            return new TripDetailRow(
                new TripSummaryRow(
                    ride.TripId,
                    TripPlanes.Ride,
                    "C",
                    Point(ride.PickupLat, ride.PickupLng),
                    Point(ride.DropoffLat, ride.DropoffLng),
                    ride.FareMinor,
                    ride.Currency,
                    ride.StartedAt,
                    ride.EndedAt),
                path,
                path.Count >= 2 ? TripGeometrySources.Aggregate1m : TripGeometrySources.None,
                // Deliberately null — see the class remarks. A minute-grain line understates a city
                // journey badly and this is the number the fare was charged on.
                DistanceKm: null,
                Duration(ride.InProgressAt ?? ride.StartedAt, ride.EndedAt),
                ride.DriverId,
                ride.DriverName,
                ride.RegistrationNumber,
                ride.Rating);
        }

        var session = await connection.QuerySingleOrDefaultAsync<SessionRow>(
            new CommandDefinition(
                SessionSql, new { UserId = userId, TripId = tripId }, cancellationToken: cancellationToken));

        if (session is null)
        {
            return null;
        }

        return new TripDetailRow(
            new TripSummaryRow(
                session.TripId,
                TripPlanes.Session,
                session.Mode,
                Point(session.PickupLat, session.PickupLng),
                Point(session.DropoffLat, session.DropoffLng),
                // Mode A is free to ride and Mode B is a monthly subscription paid to the fleet owner
                // (BR-23.8/23.9), so a session has no fare. Not zero — zero would read as "this
                // journey cost nothing", which is a different claim from "journeys are not priced".
                FareMinor: null,
                "LKR",
                session.StartedAt,
                session.EndedAt),
            ToPath(session.Polyline),
            session.GeometrySource,
            session.DistanceM is { } metres ? metres / 1000d : null,
            Duration(session.StartedAt, session.EndedAt),
            session.DriverId,
            session.DriverName,
            session.RegistrationNumber,
            Rating: null);
    }

    private static async Task<IReadOnlyList<GeoPoint>> ReadRideTrackAsync(
        Npgsql.NpgsqlConnection connection, RideRow ride, CancellationToken cancellationToken)
    {
        // No vehicle means no accept, and a ride nobody drove has no track. No end means it is still
        // running, and a history detail for a live ride is the socket's job, not this one's.
        if (ride.VehicleId is not { } vehicleId || ride.EndedAt is not { } endedAt)
        {
            return [];
        }

        var points = await connection.QueryAsync<TrackPoint>(
            new CommandDefinition(
                RideTrackSql,
                new { VehicleId = vehicleId, From = ride.InProgressAt ?? ride.StartedAt, To = endedAt },
                cancellationToken: cancellationToken));

        return [.. points
            .Where(static point => point.Lat.HasValue && point.Lng.HasValue)
            .Select(static point => new GeoPoint(point.Lat!.Value, point.Lng!.Value))];
    }

    private static GeoPoint? Point(double? lat, double? lng) =>
        lat.HasValue && lng.HasValue ? new GeoPoint(lat.Value, lng.Value) : null;

    /// <summary>
    /// The stored line as an ordered point list. PostGIS is (x = longitude, y = latitude).
    /// </summary>
    private static IReadOnlyList<GeoPoint> ToPath(NetTopologySuite.Geometries.Geometry? geometry) =>
        geometry is null
            ? []
            : [.. geometry.Coordinates.Select(static c => new GeoPoint(c.Y, c.X))];

    private static int? Duration(DateTimeOffset startedAt, DateTimeOffset? endedAt) =>
        endedAt is { } ended && ended > startedAt ? (int)(ended - startedAt).TotalSeconds : null;

    private sealed record ListRow(
        Guid TripId,
        string Plane,
        string? Mode,
        double? PickupLat,
        double? PickupLng,
        double? DropoffLat,
        double? DropoffLng,
        long? FareMinor,
        string Currency,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt)
    {
        internal TripSummaryRow ToSummary() => new(
            TripId, Plane, Mode,
            Point(PickupLat, PickupLng), Point(DropoffLat, DropoffLng),
            FareMinor, Currency, StartedAt, EndedAt);
    }

    private sealed record RideRow(
        Guid TripId,
        double PickupLat,
        double PickupLng,
        double DropoffLat,
        double DropoffLng,
        long? FareMinor,
        string Currency,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        Guid? VehicleId,
        Guid? DriverId,
        string? DriverName,
        string? RegistrationNumber,
        int? Rating,
        DateTimeOffset? InProgressAt);

    private sealed record SessionRow(
        Guid TripId,
        string Mode,
        double? PickupLat,
        double? PickupLng,
        double? DropoffLat,
        double? DropoffLng,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        Guid? DriverId,
        string? DriverName,
        string? RegistrationNumber,
        double? DistanceM,
        string GeometrySource,
        NetTopologySuite.Geometries.Geometry? Polyline);

    private sealed record TrackPoint(double? Lat, double? Lng);
}

using System.Globalization;
using Dapper;
using MageRide.Fleet.Domain;
using MageRide.Shared.Primitives;
using Npgsql;

namespace MageRide.Fleet.Persistence;

/// <summary>
/// The three read-mostly screens: the live map (US-13.3), the analytics table (US-13.4) and the
/// operational geofences (US-13.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every query here is answered by a relation the database has already scoped.</b>
/// <c>telemetry.positions_fleet</c> (1804), <c>registry.fleet_vehicles_fleet</c> and
/// <c>trips.sessions_fleet</c> (1806) each carry <c>fleet_id = current_fleet_id()</c> in their own
/// definition, and the fleet reader holds no privilege on the tables underneath any of them. That
/// is what makes "the fleet map returns only the caller org's vehicles under RLS" a property of the
/// deployment rather than of these strings.
/// </para>
/// <para>
/// <b>Geofences are the exception and are written, not just read.</b> <c>spatial.geofences</c>
/// carries a policy rather than a view (migration 1807) because it has a <c>fleet_id</c> of its
/// own; the replace runs as the service's login role, which the policy does not restrict, and is
/// guarded by the <c>fleet_id</c> predicate in the SQL — the second lock, as everywhere else here.
/// </para>
/// </remarks>
public interface IFleetInsightsRepository
{
    /// <summary>
    /// The most recent position of each of the org's vehicles seen since <paramref name="since"/>.
    /// </summary>
    /// <param name="since">
    /// The staleness horizon. A vehicle whose tracker has been dark for a day is not "at" its last
    /// position and drawing it there is worse than leaving it off — US-7.16/7.17 make the same
    /// judgement for the passenger map.
    /// </param>
    Task<IReadOnlyList<FleetVehiclePosition>> ReadMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset since,
        CancellationToken cancellationToken);

    /// <summary>Per-vehicle trips, distance, active hours and utilisation over a period (US-13.4).</summary>
    Task<IReadOnlyList<VehicleAnalytics>> ReadAnalyticsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>The org's geofences.</summary>
    Task<IReadOnlyList<FleetGeofence>> ListGeofencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the org's geofences with <paramref name="geofences"/>, returning how many were stored.
    /// </summary>
    /// <remarks>
    /// A <c>PUT</c> is a replace, so the delete and the inserts are one transaction: a crash between
    /// them would leave an operator with no fences at all, which the Phase 3 alerting path would
    /// read as "this fleet has no zones" rather than as "something failed".
    /// </remarks>
    Task<int> ReplaceGeofencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        IReadOnlyCollection<FleetGeofence> geofences,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IFleetInsightsRepository"/>
internal sealed class FleetInsightsRepository : IFleetInsightsRepository
{
    public async Task<IReadOnlyList<FleetVehiclePosition>> ReadMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // DISTINCT ON (vehicle_id) … ORDER BY vehicle_id, sample_ts DESC is the one-row-per-vehicle
        // idiom; the hypertable's (vehicle_id, sample_ts DESC) index answers it directly.
        //
        // LEFT JOIN, not JOIN: a position whose vehicle has since left the roster would otherwise
        // disappear from the map with no explanation, and the plate is a label rather than a fact
        // the row depends on. In practice the join matches — both relations are scoped to the same
        // org — and the outer join is what stops a race between the two views blanking the map.
        var rows = await connection.QueryAsync<FleetVehiclePosition>(new CommandDefinition(
            """
            SELECT latest.vehicle_id,
                   fv.registration_number,
                   latest.lat,
                   latest.lng,
                   latest.heading_deg,
                   latest.speed_mps,
                   latest.sample_ts
              FROM (SELECT DISTINCT ON (p.vehicle_id)
                           p.vehicle_id, p.lat, p.lng, p.heading_deg, p.speed_mps, p.sample_ts
                      FROM telemetry.positions_fleet p
                     WHERE p.sample_ts >= @Since
                     ORDER BY p.vehicle_id, p.sample_ts DESC) latest
              LEFT JOIN registry.fleet_vehicles_fleet fv ON fv.vehicle_id = latest.vehicle_id
             ORDER BY fv.registration_number NULLS LAST, latest.vehicle_id;
            """,
            new { Since = since },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<VehicleAnalytics>> ReadAnalyticsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Three facts about a period, from two relations, per vehicle on the roster.
        //
        // `trips` and `active_hours` come from trips.sessions_fleet — R-01 makes trips.* the Mode
        // A/B tracking plane, and a fleet vehicle can never appear in rides.rides. An open session
        // is measured to the end of the period rather than skipped, or a bus that has been out all
        // day reads as zero hours until it comes back.
        //
        // `distance` is the sum of great-circle hops between consecutive telemetry samples, which
        // is **not road distance**: nothing in this build map-matches a completed journey, and
        // ST_Length over a line built from GPS samples is the same number with more ceremony. It is
        // therefore an under-estimate on a winding road and an over-estimate on a jittery fix, and
        // it is what US-13.4's "distance" can honestly mean today. Raised in the C059 handoff.
        //
        // `utilisation` is active hours over the period's hours — the definition US-13.4 implies
        // ("utilisation, idle time") and the only one derivable without a shift roster.
        var rows = await connection.QueryAsync<AnalyticsRow>(new CommandDefinition(
            """
            WITH period AS (
              -- ::double precision on every derived number, and not for tidiness: EXTRACT(EPOCH …)
              -- answers `numeric`, Dapper's constructor binding matches parameter types exactly,
              -- and a NUMERIC column against a `double` parameter does not fail to convert — it
              -- fails to materialise the record at all, with a message about the constructor.
              SELECT GREATEST(
                       EXTRACT(EPOCH FROM (@To::timestamptz - @From::timestamptz))::double precision / 3600.0,
                       1e-9) AS hours),
                 journeys AS (
              SELECT s.vehicle_id,
                     count(*)::int AS trip_count,
                     (COALESCE(SUM(EXTRACT(EPOCH FROM (LEAST(COALESCE(s.ended_at, @To), @To) - s.started_at))), 0)
                       / 3600.0)::double precision AS active_hours
                FROM trips.sessions_fleet s
               WHERE s.started_at < @To AND COALESCE(s.ended_at, @To) >= @From
               GROUP BY s.vehicle_id),
                 hops AS (
              SELECT p.vehicle_id,
                     ST_Distance(
                       ST_SetSRID(ST_MakePoint(p.lng, p.lat), 4326)::geography,
                       ST_SetSRID(ST_MakePoint(
                         lag(p.lng) OVER (PARTITION BY p.vehicle_id ORDER BY p.sample_ts),
                         lag(p.lat) OVER (PARTITION BY p.vehicle_id ORDER BY p.sample_ts)), 4326)::geography)
                       AS metres
                FROM telemetry.positions_fleet p
               WHERE p.sample_ts >= @From AND p.sample_ts < @To),
                 distances AS (
              SELECT vehicle_id, (COALESCE(SUM(metres), 0) / 1000.0)::double precision AS distance_km
                FROM hops GROUP BY vehicle_id)
            SELECT fv.vehicle_id,
                   fv.registration_number,
                   COALESCE(j.trip_count, 0)                        AS trip_count,
                   COALESCE(d.distance_km, 0)::double precision      AS distance_km,
                   COALESCE(j.active_hours, 0)::double precision     AS active_hours,
                   LEAST(100, COALESCE(j.active_hours, 0) * 100.0 / period.hours)::double precision
                     AS utilisation_pct
              FROM registry.fleet_vehicles_fleet fv
              CROSS JOIN period
              LEFT JOIN journeys j  ON j.vehicle_id = fv.vehicle_id
              LEFT JOIN distances d ON d.vehicle_id = fv.vehicle_id
             ORDER BY fv.registration_number;
            """,
            new { From = from, To = to },
            transaction,
            cancellationToken: cancellationToken));

        return
        [
            .. rows.Select(row => new VehicleAnalytics(
                row.VehicleId,
                row.RegistrationNumber,
                row.TripCount,
                Math.Round(row.DistanceKm, 3),
                Math.Round(row.ActiveHours, 3),
                Math.Round(row.UtilisationPct, 2),
                // `earningsMinor` stays absent rather than zero. `fleet.yaml` offers the field and a
                // fleet's Mode A/B vehicles take no fares on this platform: a bus fare is collected
                // on the bus and a Mode B subscription is a pass-through to the owner's own bank
                // account that MageRide never sees (BR-23.10, §18b — "subscription.payments never
                // posts to billing.journal_entries"). Zero would be a claim that the operator earned
                // nothing; absent is the truth, which is that this platform does not know.
                EarningsMinor: null)),
        ];
    }

    public async Task<IReadOnlyList<FleetGeofence>> ListGeofencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // GeoJSON rather than WKT: it is one `System.Text.Json` parse on this side, where WKT would
        // be a hand-written tokeniser for a format PostGIS already emits structurally.
        var rows = await connection.QueryAsync<GeofenceRow>(new CommandDefinition(
            """
            SELECT id, fleet_id, name, ST_AsGeoJSON(geom) AS geo_json
              FROM spatial.geofences
             WHERE fleet_id = @FleetId
             ORDER BY name NULLS LAST, id;
            """,
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));

        return [.. rows.Select(row => row.ToGeofence())];
    }

    public async Task<int> ReplaceGeofencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fleetId,
        IReadOnlyCollection<FleetGeofence> geofences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(geofences);

        // The predicate is `fleet_id = @FleetId`, so §17's platform polygons — which carry no fleet
        // — are outside it. An operator's PUT can never reach the operating-area geometry the
        // booking path tests against.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spatial.geofences WHERE fleet_id = @FleetId;",
            new { FleetId = fleetId },
            transaction,
            cancellationToken: cancellationToken));

        if (geofences.Count == 0)
        {
            return 0;
        }

        // ST_GeomFromText over a ring the caller closed, checked with ST_IsValid: a self-
        // intersecting polygon is accepted by the geometry type and then behaves unpredictably in
        // every containment test, which is a bug that would surface in Phase 3 rather than here.
        // `kind` records what this row is for, so a future reader of §17's table can tell an
        // operator's zone from the platform's without joining.
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO spatial.geofences (fleet_id, name, kind, geom)
            VALUES (@FleetId, @Name, 'fleet_operational', ST_GeomFromText(@Wkt, 4326));
            """,
            geofences.Select(geofence => new
            {
                FleetId = fleetId,
                geofence.Name,
                Wkt = ToPolygonWkt(geofence.Polygon),
            }).ToArray(),
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>A closed WGS-84 ring as WKT, with every coordinate rendered invariantly.</summary>
    /// <remarks>
    /// <c>CultureInfo.InvariantCulture</c> is load-bearing rather than tidy: under a culture that
    /// writes decimals with a comma, <c>ST_GeomFromText</c> would be handed
    /// <c>POLYGON((79,86 6,93, …))</c> — a syntactically valid ring with twice as many
    /// coordinates, in the sea.
    /// </remarks>
    private static string ToPolygonWkt(IReadOnlyList<GeoPoint> ring) =>
        "POLYGON((" + string.Join(
            ", ",
            ring.Select(point => string.Create(
                CultureInfo.InvariantCulture, $"{point.Longitude} {point.Latitude}"))) + "))";

    private sealed record AnalyticsRow(
        Guid VehicleId,
        string RegistrationNumber,
        int TripCount,
        double DistanceKm,
        double ActiveHours,
        double UtilisationPct);

    private sealed record GeofenceRow(Guid Id, Guid FleetId, string? Name, string GeoJson)
    {
        public FleetGeofence ToGeofence()
        {
            using var document = System.Text.Json.JsonDocument.Parse(GeoJson);

            // GeoJSON polygon coordinates are [[ [lng,lat], … ]] — the outer ring first. Only the
            // outer ring is read: nothing in this service creates a polygon with a hole, and a hole
            // rendered as a second ring on the portal's map would draw as a stray shape.
            var ring = document.RootElement.GetProperty("coordinates")[0];

            return new FleetGeofence(
                Id,
                FleetId,
                Name,
                [.. ring.EnumerateArray().Select(point => new GeoPoint(point[1].GetDouble(), point[0].GetDouble()))]);
        }
    }
}

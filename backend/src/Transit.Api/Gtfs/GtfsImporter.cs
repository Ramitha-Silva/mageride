using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace MageRide.Transit.Gtfs;

/// <summary>What one staging load put on disk, for the activation log line.</summary>
public sealed record GtfsImportSummary(long Routes, long Stops, long Trips, long StopTimes, long Shapes);

/// <summary>
/// Loads a validated feed into <c>transit_staging.gtfs_*</c> — the internal import step D3' keeps
/// under the superseded <c>POST /admin/transit/gtfs-import</c> (AL-54).
/// </summary>
/// <remarks>
/// <para>
/// <b>Staging, never live.</b> This is the half of activation that takes minutes on a national
/// feed, and it runs against tables no request reads. Only the swap that follows it touches
/// <c>transit.*</c>, which is what makes BR-32.2's promise — "on any import failure the prior feed
/// stays live" — a property of where the rows go rather than of how carefully the code unwinds.
/// </para>
/// <para>
/// <b>Binary COPY, not INSERT.</b> Half a million <c>stop_times</c> rows as parameterised inserts
/// is a round trip each; as a binary copy stream it is one. The two geography tables go through a
/// temp table because a COPY stream cannot construct a PostGIS value — the coordinates arrive as
/// doubles and one <c>INSERT … SELECT</c> makes them points, which is also where a NULL coordinate
/// stays NULL instead of becoming (0, 0) in the Gulf of Guinea.
/// </para>
/// </remarks>
public interface IGtfsImporter
{
    /// <summary>
    /// Truncates staging and loads the zip into it, in one transaction on the caller's connection.
    /// </summary>
    Task<GtfsImportSummary> LoadStagingAsync(
        NpgsqlConnection connection, Stream zip, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class GtfsImporter : IGtfsImporter
{
    public async Task<GtfsImportSummary> LoadStagingAsync(
        NpgsqlConnection connection, Stream zip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(zip);

        using var archive = GtfsArchive.Open(zip);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // One statement for all five, so the foreign keys between them do not decide an order.
        await ExecuteAsync(
            connection,
            transaction,
            """
            TRUNCATE transit_staging.gtfs_stop_times, transit_staging.gtfs_trips,
                     transit_staging.gtfs_shapes, transit_staging.gtfs_stops,
                     transit_staging.gtfs_routes;
            """,
            cancellationToken);

        var agencies = ReadAgencyNames(archive);

        var routes = await CopyRoutesAsync(connection, archive, agencies, cancellationToken);
        var stops = await CopyStopsAsync(connection, transaction, archive, cancellationToken);
        var trips = await CopyTripsAsync(connection, archive, cancellationToken);
        var shapes = await CopyShapesAsync(connection, transaction, archive, cancellationToken);
        var stopTimes = await CopyStopTimesAsync(connection, archive, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new GtfsImportSummary(routes, stops, trips, stopTimes, shapes);
    }

    /// <summary>
    /// <c>agency_id → agency_name</c>.
    /// </summary>
    /// <remarks>
    /// <c>transit.gtfs_routes.agency</c> is one TEXT column and §18c does not say which of GTFS's
    /// two agency fields it holds. transit-svc answers it as <c>agencyName</c> on the route-detail
    /// response, so the *name* is what belongs in it — a passenger reading "SLTB" is helped and
    /// one reading "1" is not. The id is kept as the fallback for a feed that names an agency this
    /// map does not have.
    /// </remarks>
    private static Dictionary<string, string> ReadAgencyNames(GtfsArchive archive)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        using var reader = archive.OpenCsv(GtfsFiles.Agency);

        while (reader is not null && reader.Read())
        {
            if (reader["agency_name"] is { } name)
            {
                // A single-agency feed may omit agency_id entirely; the empty key is what
                // routes.txt's own missing agency_id looks up.
                names[reader["agency_id"] ?? string.Empty] = name;
            }
        }

        return names;
    }

    private static async Task<long> CopyRoutesAsync(
        NpgsqlConnection connection,
        GtfsArchive archive,
        Dictionary<string, string> agencies,
        CancellationToken cancellationToken)
    {
        using var reader = archive.OpenCsv(GtfsFiles.Routes);

        if (reader is null)
        {
            return 0;
        }

        var rows = 0L;

        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY transit_staging.gtfs_routes (route_id, agency, route_short_name, route_long_name, route_type)
            FROM STDIN (FORMAT BINARY);
            """,
            cancellationToken);

        while (reader.Read())
        {
            if (reader["route_id"] is not { } routeId)
            {
                continue;
            }

            var agencyId = reader["agency_id"];
            var agency = agencyId is null
                ? agencies.Values.Count == 1 ? agencies.Values.First() : null
                : agencies.TryGetValue(agencyId, out var name) ? name : agencyId;

            await writer.StartRowAsync(cancellationToken);
            await WriteTextAsync(writer, routeId, cancellationToken);
            await WriteTextAsync(writer, agency, cancellationToken);
            await WriteTextAsync(writer, reader["route_short_name"], cancellationToken);
            await WriteTextAsync(writer, reader["route_long_name"], cancellationToken);
            await WriteIntAsync(writer, reader["route_type"], cancellationToken);

            rows++;
        }

        await writer.CompleteAsync(cancellationToken);

        return rows;
    }

    private static async Task<long> CopyStopsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GtfsArchive archive,
        CancellationToken cancellationToken)
    {
        using var reader = archive.OpenCsv(GtfsFiles.Stops);

        if (reader is null)
        {
            return 0;
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TEMP TABLE tmp_gtfs_stops (
              stop_id TEXT, name TEXT, lat DOUBLE PRECISION, lng DOUBLE PRECISION) ON COMMIT DROP;
            """,
            cancellationToken);

        var rows = 0L;

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY tmp_gtfs_stops (stop_id, name, lat, lng) FROM STDIN (FORMAT BINARY);", cancellationToken))
        {
            while (reader.Read())
            {
                if (reader["stop_id"] is not { } stopId)
                {
                    continue;
                }

                await writer.StartRowAsync(cancellationToken);
                await WriteTextAsync(writer, stopId, cancellationToken);
                await WriteTextAsync(writer, reader["stop_name"], cancellationToken);
                await WriteDoubleAsync(writer, reader["stop_lat"], cancellationToken);
                await WriteDoubleAsync(writer, reader["stop_lon"], cancellationToken);

                rows++;
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO transit_staging.gtfs_stops (stop_id, name, geo)
            SELECT stop_id,
                   name,
                   CASE WHEN lat IS NULL OR lng IS NULL
                        THEN NULL
                        ELSE ST_SetSRID(ST_MakePoint(lng, lat), 4326)::geography
                   END
              FROM tmp_gtfs_stops;
            """,
            cancellationToken);

        return rows;
    }

    private static async Task<long> CopyTripsAsync(
        NpgsqlConnection connection, GtfsArchive archive, CancellationToken cancellationToken)
    {
        using var reader = archive.OpenCsv(GtfsFiles.Trips);

        if (reader is null)
        {
            return 0;
        }

        var rows = 0L;

        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY transit_staging.gtfs_trips (trip_id, route_id, service_id, shape_id, direction, trip_headsign)
            FROM STDIN (FORMAT BINARY);
            """,
            cancellationToken);

        while (reader.Read())
        {
            // route_id is NOT NULL on the table; a validated feed has none of these, and an
            // unvalidated one would abort the whole import with a constraint error instead.
            if (reader["trip_id"] is not { } tripId || reader["route_id"] is not { } routeId)
            {
                continue;
            }

            await writer.StartRowAsync(cancellationToken);
            await WriteTextAsync(writer, tripId, cancellationToken);
            await WriteTextAsync(writer, routeId, cancellationToken);
            await WriteTextAsync(writer, reader["service_id"], cancellationToken);
            await WriteTextAsync(writer, reader["shape_id"], cancellationToken);
            await WriteShortAsync(writer, reader["direction_id"], cancellationToken);

            // Migration 1406, and the reason C056 asked for this component by name: without it
            // "138 to Kottawa" and "138 to Pettah" are the same card twice.
            await WriteTextAsync(writer, reader["trip_headsign"], cancellationToken);

            rows++;
        }

        await writer.CompleteAsync(cancellationToken);

        return rows;
    }

    private static async Task<long> CopyShapesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GtfsArchive archive,
        CancellationToken cancellationToken)
    {
        using var reader = archive.OpenCsv(GtfsFiles.Shapes);

        if (reader is null)
        {
            return 0;
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TEMP TABLE tmp_gtfs_shapes (
              shape_id TEXT, seq INTEGER, lat DOUBLE PRECISION, lng DOUBLE PRECISION) ON COMMIT DROP;
            """,
            cancellationToken);

        var rows = 0L;

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY tmp_gtfs_shapes (shape_id, seq, lat, lng) FROM STDIN (FORMAT BINARY);", cancellationToken))
        {
            while (reader.Read())
            {
                if (reader["shape_id"] is not { } shapeId ||
                    !int.TryParse(reader["shape_pt_sequence"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
                {
                    continue;
                }

                await writer.StartRowAsync(cancellationToken);
                await WriteTextAsync(writer, shapeId, cancellationToken);
                await writer.WriteAsync(sequence, NpgsqlDbType.Integer, cancellationToken);
                await WriteDoubleAsync(writer, reader["shape_pt_lat"], cancellationToken);
                await WriteDoubleAsync(writer, reader["shape_pt_lon"], cancellationToken);

                rows++;
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO transit_staging.gtfs_shapes (shape_id, seq, geo)
            SELECT shape_id,
                   seq,
                   CASE WHEN lat IS NULL OR lng IS NULL
                        THEN NULL
                        ELSE ST_SetSRID(ST_MakePoint(lng, lat), 4326)::geography
                   END
              FROM tmp_gtfs_shapes;
            """,
            cancellationToken);

        return rows;
    }

    private static async Task<long> CopyStopTimesAsync(
        NpgsqlConnection connection, GtfsArchive archive, CancellationToken cancellationToken)
    {
        using var reader = archive.OpenCsv(GtfsFiles.StopTimes);

        if (reader is null)
        {
            return 0;
        }

        var rows = 0L;

        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY transit_staging.gtfs_stop_times (trip_id, stop_id, stop_sequence, arr, dep)
            FROM STDIN (FORMAT BINARY);
            """,
            cancellationToken);

        while (reader.Read())
        {
            if (reader["trip_id"] is not { } tripId ||
                reader["stop_id"] is not { } stopId ||
                !int.TryParse(reader["stop_sequence"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
            {
                continue;
            }

            await writer.StartRowAsync(cancellationToken);
            await WriteTextAsync(writer, tripId, cancellationToken);
            await WriteTextAsync(writer, stopId, cancellationToken);
            await writer.WriteAsync(sequence, NpgsqlDbType.Integer, cancellationToken);
            await WriteTimeAsync(writer, reader["arrival_time"], cancellationToken);
            await WriteTimeAsync(writer, reader["departure_time"], cancellationToken);

            rows++;
        }

        await writer.CompleteAsync(cancellationToken);

        return rows;
    }

    // -----------------------------------------------------------------------------------------
    // Column writers. Each maps "the feed did not say" to NULL rather than to a default, because
    // a default is a fact nobody supplied.
    // -----------------------------------------------------------------------------------------

    private static async Task WriteTextAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        await writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
    }

    private static async Task WriteIntAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            await writer.WriteAsync(parsed, NpgsqlDbType.Integer, cancellationToken);
            return;
        }

        await writer.WriteNullAsync(cancellationToken);
    }

    private static async Task WriteShortAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            await writer.WriteAsync(parsed, NpgsqlDbType.Smallint, cancellationToken);
            return;
        }

        await writer.WriteNullAsync(cancellationToken);
    }

    private static async Task WriteDoubleAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            await writer.WriteAsync(parsed, NpgsqlDbType.Double, cancellationToken);
            return;
        }

        await writer.WriteNullAsync(cancellationToken);
    }

    /// <summary>
    /// A GTFS clock time as an INTERVAL, because it may exceed 24 hours — "25:10:00" is ten past
    /// one the next morning on the same service day, and a TIME column cannot hold it (§18c says
    /// so at the column).
    /// </summary>
    private static async Task WriteTimeAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken cancellationToken)
    {
        if (GtfsTime.TryParse(value, out var parsed))
        {
            await writer.WriteAsync(parsed, NpgsqlDbType.Interval, cancellationToken);
            return;
        }

        await writer.WriteNullAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

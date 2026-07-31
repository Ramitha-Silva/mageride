using Dapper;
using MageRide.Shared.Geo;
using MageRide.Shared.Persistence;
using MageRide.Shared.Primitives;
using MageRide.Transit.Domain;

namespace MageRide.Transit.Feed;

/// <summary>Identifies the active feed without loading it.</summary>
public sealed record ActiveFeed(Guid FeedVersionId, string? FeedInfoVersion);

/// <summary>Loads the active GTFS feed out of <c>transit.*</c> (§18c).</summary>
public interface IGtfsFeedRepository
{
    /// <summary>Which feed is active, or null before the first import (AL-55's safety net).</summary>
    Task<ActiveFeed?> FindActiveAsync(CancellationToken cancellationToken);

    /// <summary>Reads the whole feed into memory. Called on activation, never per request.</summary>
    Task<GtfsFeed> LoadAsync(ActiveFeed active, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGtfsFeedRepository"/>
/// <remarks>
/// <para>
/// <b>Four queries, none of them per request.</b> Everything the matcher needs is read once at
/// activation and answered from memory afterwards — which is what BR-23.2's "all direct routes"
/// requires to be affordable at all: the alternative is a self-join over half a million
/// <c>gtfs_stop_times</c> rows on a screen the passenger is watching.
/// </para>
/// <para>
/// <b>Patterns are folded in this process, not in SQL.</b> Deduplicating stop sequences is a
/// grouping over ordered lists; expressing it as SQL would mean an array aggregate per trip and a
/// <c>DISTINCT</c> over arrays, which reads worse and gives the planner a harder problem than
/// streaming the rows in order does.
/// </para>
/// </remarks>
internal sealed class GtfsFeedRepository(INpgsqlConnectionFactory connections) : IGtfsFeedRepository
{
    public async Task<ActiveFeed?> FindActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ActiveFeed>(new CommandDefinition(
            """
            SELECT feed_version_id AS FeedVersionId, feed_info_version AS FeedInfoVersion
              FROM transit.gtfs_feed_versions
             WHERE status = 'active'
             LIMIT 1;
            """,
            cancellationToken: cancellationToken));
    }

    public async Task<GtfsFeed> LoadAsync(ActiveFeed active, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(active);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var stops = (await connection.QueryAsync<TransitStop>(new CommandDefinition(
            """
            SELECT stop_id AS StopId,
                   COALESCE(name, stop_id) AS Name,
                   ST_Y(geo::geometry) AS Lat,
                   ST_X(geo::geometry) AS Lng
              FROM transit.gtfs_stops
             WHERE geo IS NOT NULL;
            """,
            cancellationToken: cancellationToken))).AsList();

        var routes = (await connection.QueryAsync<TransitRoute>(new CommandDefinition(
            """
            SELECT route_id AS RouteId, route_short_name AS ShortName,
                   route_long_name AS LongName, agency AS Agency, route_type AS RouteType
              FROM transit.gtfs_routes;
            """,
            cancellationToken: cancellationToken))).AsList();

        var patterns = await LoadPatternsAsync(connection, cancellationToken);
        var shapes = await LoadShapesAsync(connection, cancellationToken);

        return new GtfsFeed(active.FeedVersionId, active.FeedInfoVersion, stops, routes, patterns, shapes);
    }

    /// <summary>
    /// Every distinct stop sequence, folded across the trips that share it.
    /// </summary>
    /// <remarks>
    /// Ordered by trip and sequence so the rows arrive as complete patterns and the fold is a
    /// single pass. <c>ORDER BY</c> on the primary key of <c>gtfs_stop_times</c> is an index scan.
    /// </remarks>
    private static async Task<IReadOnlyList<RoutePattern>> LoadPatternsAsync(
        Npgsql.NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<StopTimeRow>(new CommandDefinition(
            """
            SELECT t.trip_id AS TripId, t.route_id AS RouteId, t.shape_id AS ShapeId,
                   t.direction AS Direction, t.trip_headsign AS Headsign,
                   st.stop_id AS StopId, st.stop_sequence AS StopSequence,
                   EXTRACT(EPOCH FROM COALESCE(st.arr, st.dep))::bigint AS OffsetSeconds
              FROM transit.gtfs_stop_times st
              JOIN transit.gtfs_trips t ON t.trip_id = st.trip_id
             ORDER BY st.trip_id, st.stop_sequence;
            """,
            flags: CommandFlags.None,
            cancellationToken: cancellationToken));

        var patterns = new Dictionary<string, RoutePattern>(StringComparer.Ordinal);

        string? currentTrip = null;
        StopTimeRow? head = null;
        var stopIds = new List<string>();
        var offsets = new List<long?>();

        void Flush()
        {
            if (head is null || stopIds.Count < 2)
            {
                return;
            }

            // The identity of a pattern is its route and its stop sequence — two trips running the
            // same halts in the same order at different times are one pattern for BR-23.2.
            // Unit separators, so a stop id that happens to contain the delimiter cannot make
            // two different sequences collide into one pattern.
            var key = string.Concat(head.RouteId, "\u001f", string.Join('\u001e', stopIds));

            if (patterns.ContainsKey(key))
            {
                return;
            }

            patterns[key] = new RoutePattern(
                head.RouteId,
                head.ShapeId,
                head.Headsign,
                head.Direction,
                [.. stopIds],
                Durations(offsets));
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.TripId, currentTrip, StringComparison.Ordinal))
            {
                Flush();

                currentTrip = row.TripId;
                head = row;
                stopIds.Clear();
                offsets.Clear();
            }

            stopIds.Add(row.StopId);
            offsets.Add(row.OffsetSeconds);
        }

        Flush();

        return [.. patterns.Values];
    }

    /// <summary>
    /// Seconds from the first halt to each halt, or an empty list when the feed gave no times.
    /// </summary>
    /// <remarks>
    /// <b>All or nothing per pattern.</b> GTFS lets a feed time only its timepoints and leave the
    /// rest blank; interpolating across the gaps would invent a duration, and a partially-filled
    /// list would let <c>DurationBetween</c> subtract a real offset from a zero. A pattern missing
    /// any time simply has no duration, which the wire shape already allows.
    /// </remarks>
    private static IReadOnlyList<int> Durations(List<long?> offsets)
    {
        if (offsets.Count == 0 || offsets[0] is not { } first || offsets.Any(offset => offset is null))
        {
            return [];
        }

        return [.. offsets.Select(offset => (int)(offset!.Value - first))];
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> LoadShapesAsync(
        Npgsql.NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<ShapePointRow>(new CommandDefinition(
            """
            SELECT shape_id AS ShapeId,
                   ST_Y(geo::geometry) AS Lat,
                   ST_X(geo::geometry) AS Lng
              FROM transit.gtfs_shapes
             WHERE geo IS NOT NULL
             ORDER BY shape_id, seq;
            """,
            cancellationToken: cancellationToken));

        var shapes = new List<KeyValuePair<string, string>>();
        var points = new List<GeoPoint>();
        string? current = null;

        void Flush()
        {
            // Encoded once, at load, because the same shape is served on every option that names
            // its route — re-encoding a several-hundred-point line per request would be the most
            // expensive thing on the path.
            if (current is not null && EncodedPolyline.Encode(points) is { } encoded)
            {
                shapes.Add(new KeyValuePair<string, string>(current, encoded));
            }
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.ShapeId, current, StringComparison.Ordinal))
            {
                Flush();

                current = row.ShapeId;
                points.Clear();
            }

            points.Add(new GeoPoint(row.Lat, row.Lng));
        }

        Flush();

        return shapes;
    }

    private sealed record StopTimeRow(
        string TripId, string RouteId, string? ShapeId, short? Direction, string? Headsign,
        string StopId, int StopSequence, long? OffsetSeconds);

    private sealed record ShapePointRow(string ShapeId, double Lat, double Lng);
}

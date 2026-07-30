using Dapper;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.HotPath.PersistenceWriter.Summaries;

/// <summary>The <c>session.ended</c> facts a summary is built from (C031's envelope).</summary>
/// <param name="SessionId">The session that ended.</param>
/// <param name="VehicleId">Its vehicle — the topic's partition key.</param>
/// <param name="DriverId">Its driver, carried so the summary needs no lookup.</param>
/// <param name="Mode">A or B. R-01: a Mode C journey is a ride, not a session.</param>
/// <param name="EndReason">How it closed, for the operator reading the summary later.</param>
/// <param name="EndedAt">When it closed.</param>
public sealed record EndedSession(
    Guid SessionId, Guid VehicleId, Guid DriverId, string Mode, string? EndReason, DateTimeOffset EndedAt);

/// <summary>Which relation a summary's geometry was computed from.</summary>
public static class GeometrySources
{
    /// <summary>Full-resolution <c>telemetry.positions</c> — a fix every 2–10 s (D5' §5.2).</summary>
    public const string Telemetry = "telemetry";

    /// <summary>The 1/min <c>trips.position_samples</c>. Distance is a lower bound.</summary>
    public const string Operational = "operational";

    /// <summary>The session produced no fixes at all.</summary>
    public const string None = "none";
}

/// <summary>Why a <c>session.ended</c> did or did not produce a summary.</summary>
/// <remarks>
/// The three cases have to be told apart by the caller, because two of them are not errors and the
/// third must be retried. A single "null means no" would either stall a partition on a restarted
/// session or commit past a journey whose row was a moment from being visible.
/// </remarks>
public enum SummaryStatus
{
    /// <summary>Computed and stored.</summary>
    Written,

    /// <summary>
    /// No <c>trips.sessions</c> row. The event outran its own transaction — <b>retry</b>.
    /// </summary>
    SessionNotFound,

    /// <summary>
    /// The session is live again after a US-5.10 restart. Not an error; the next end summarises it.
    /// </summary>
    SessionActive,
}

/// <summary>What a summary came out as. The stored row, read back.</summary>
public sealed record TripSummary(
    SummaryStatus Status,
    Guid SessionId,
    double DistanceM = 0,
    int SampleCount = 0,
    string GeometrySource = GeometrySources.None,
    bool HasPolyline = false)
{
    /// <summary>Whether a row was written.</summary>
    public bool IsWritten => Status is SummaryStatus.Written;
}

/// <summary>ADD §9.2's per-session trip summary: start, end, distance, polyline.</summary>
public interface ITripSummaryService
{
    /// <summary>Computes and stores the summary for a session that has ended.</summary>
    Task<TripSummary> SummariseAsync(EndedSession session, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITripSummaryService"/>
/// <remarks>
/// <para>
/// <b>ADD §9.2 promises this artefact and no DDL source printed a table for it</b> — migration 0506
/// adds <c>trips.session_summaries</c> and the C040 handoff raises it. ADD §9.5 item 2 says the
/// <i>query</i> path for a trip summary "hits aggregates, not raw rows", and that cannot be the whole
/// story: <c>telemetry.positions_1m</c> is bucketed by time and knows nothing about sessions, so it
/// can say how fast a vehicle was going at 14:03 and not where one journey started, ended, or how far
/// it went. The summary is computed once, from raw rows, on the event that closes the journey.
/// </para>
/// <para>
/// <b>Computed from full-resolution <c>telemetry.positions</c>, not from the 1/min samples.</b> A
/// minute of city driving is not a straight line, so a distance summed over 1/min samples understates
/// a real journey badly — chaining sixty-second chords across a route with turns loses a third of it
/// or more. The raw rows are indexed for exactly this read (<c>ix_positions_vehicle_ts</c>, and
/// ADD §9.5 item 6 names "trip linestring for trip Y" as a raw-chunk query), and they are always
/// present when the event arrives: a session ends the same day it started and raw chunks live 30 days.
/// The 1/min fallback exists only for a replayed event and labels itself <c>operational</c> so a
/// reader can see the difference.
/// </para>
/// <para>
/// <b>Bounded by the session's own window, not by <c>trip_id</c>.</b> <c>telemetry.positions.trip_id</c>
/// is populated only when the publishing device chose to set it (<c>mqtt-topics.md</c> §2.1 —
/// "Mode A/B only", and nothing on the platform makes a tracker do it), so a summary keyed on it
/// would be empty for most fleets. <c>(vehicle_id, sample_ts BETWEEN started_at AND ended_at)</c>
/// needs nothing from the device and uses the index the ADD names.
/// </para>
/// <para>
/// <b>Upserted, because a session can end twice.</b> US-5.10 lets an auto-ended session be restarted
/// in place inside a five-minute grace, keeping its id — so <c>session.ended</c> is not a
/// once-per-session event and the summary has to be replaceable. That also makes the write idempotent
/// against D6' §2.3's at-least-once delivery for free.
/// </para>
/// <para>
/// <b>The distance is measured before the line is simplified.</b> Simplifying first and measuring
/// after would quietly shorten every journey by the tolerance's worth of detail — which is the one
/// number in the summary somebody might be paid against.
/// </para>
/// </remarks>
public sealed class TripSummaryService(
    INpgsqlConnectionFactory connectionFactory,
    IOptions<PersistenceWriterOptions> options,
    ILogger<TripSummaryService> logger) : ITripSummaryService
{
    /// <summary>
    /// The session's window. Read from <c>trips.sessions</c> rather than taken from the event,
    /// because the event carries <c>endedAt</c> and not <c>startedAt</c>, and because a session that
    /// has since been restarted (US-5.10) is <c>ACTIVE</c> again — summarising it would store a
    /// journey that is still running.
    /// </summary>
    private const string SessionSql =
        """
        SELECT id AS SessionId, vehicle_id AS VehicleId, driver_id AS DriverId, mode AS Mode,
               state AS State, started_at AS StartedAt, ended_at AS EndedAt, end_reason AS EndReason
          FROM trips.sessions
         WHERE id = @SessionId;
        """;

    /// <summary>
    /// Everything the summary needs, in one pass over the journey's rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ST_MakeLine</c> over an ordered aggregate gives the path; <c>ST_Length</c> on the geography
    /// gives metres over the spheroid, which is the same measure <c>ST_DWithin</c> uses everywhere
    /// else on the platform. The line is built from geometry and cast once, because
    /// <c>ST_MakeLine</c> has no geography overload.
    /// </para>
    /// <para>
    /// <c>ST_SimplifyPreserveTopology</c>, not <c>ST_Simplify</c>: the plain one can produce a
    /// self-intersecting or collapsed line from a route that doubles back on itself, and a Mode A bus
    /// route doubles back at every terminus. The tolerance is in degrees for a geometry, so the
    /// metres are converted at the journey's own latitude rather than assumed.
    /// </para>
    /// </remarks>
    private const string GeometrySql =
        """
        WITH fixes AS (
            SELECT lat, lng, speed_mps, sample_ts
              FROM telemetry.positions
             WHERE vehicle_id = @VehicleId
               AND sample_ts >= @StartedAt
               AND sample_ts <= @EndedAt
             ORDER BY sample_ts
        ),
        line AS (
            SELECT ST_MakeLine(ST_SetSRID(ST_MakePoint(lng, lat), 4326) ORDER BY sample_ts) AS path,
                   count(*)          AS samples,
                   max(speed_mps)    AS max_speed,
                   avg(speed_mps)    AS avg_speed
              FROM fixes
        )
        SELECT samples                                              AS SampleCount,
               COALESCE(ST_Length(path::geography), 0)              AS DistanceM,
               max_speed                                            AS MaxSpeedMps,
               avg_speed                                            AS AvgSpeedMps,
               CASE WHEN ST_NPoints(path) >= 1
                    THEN ST_AsBinary(ST_PointN(path, 1)::geography) END          AS StartGeo,
               CASE WHEN ST_NPoints(path) >= 1
                    THEN ST_AsBinary(ST_PointN(path, ST_NPoints(path))::geography) END AS EndGeo,
               CASE WHEN ST_NPoints(ST_RemoveRepeatedPoints(path)) >= 2
                    THEN ST_AsBinary(
                           ST_SimplifyPreserveTopology(
                             path, @ToleranceM / (111320.0 * cos(radians(ST_Y(ST_PointN(path, 1))))))
                           ::geography) END                          AS Polyline
          FROM line;
        """;

    /// <summary>The 1/min fallback. Same shape, over the operational samples.</summary>
    private const string OperationalGeometrySql =
        """
        WITH line AS (
            SELECT ST_MakeLine(geo::geometry ORDER BY sample_ts) AS path,
                   count(*)       AS samples,
                   max(speed_mps) AS max_speed,
                   avg(speed_mps) AS avg_speed
              FROM trips.position_samples
             WHERE session_id = @SessionId
        )
        SELECT samples                                              AS SampleCount,
               COALESCE(ST_Length(path::geography), 0)              AS DistanceM,
               max_speed                                            AS MaxSpeedMps,
               avg_speed                                            AS AvgSpeedMps,
               CASE WHEN ST_NPoints(path) >= 1
                    THEN ST_AsBinary(ST_PointN(path, 1)::geography) END          AS StartGeo,
               CASE WHEN ST_NPoints(path) >= 1
                    THEN ST_AsBinary(ST_PointN(path, ST_NPoints(path))::geography) END AS EndGeo,
               CASE WHEN ST_NPoints(ST_RemoveRepeatedPoints(path)) >= 2
                    THEN ST_AsBinary(
                           ST_SimplifyPreserveTopology(
                             path, @ToleranceM / (111320.0 * cos(radians(ST_Y(ST_PointN(path, 1))))))
                           ::geography) END                          AS Polyline
          FROM line;
        """;

    private const string UpsertSql =
        """
        INSERT INTO trips.session_summaries (
            session_id, vehicle_id, driver_id, mode, started_at, ended_at, end_reason,
            start_geo, end_geo, distance_m, polyline, sample_count, max_speed_mps, avg_speed_mps,
            geometry_source, computed_at)
        VALUES (
            @SessionId, @VehicleId, @DriverId, @Mode, @StartedAt, @EndedAt, @EndReason,
            ST_GeomFromWKB(@StartGeo, 4326)::geography,
            ST_GeomFromWKB(@EndGeo, 4326)::geography,
            @DistanceM,
            ST_GeomFromWKB(@Polyline, 4326)::geography,
            @SampleCount, @MaxSpeedMps, @AvgSpeedMps, @GeometrySource, now())
        ON CONFLICT (session_id) DO UPDATE SET
            ended_at        = EXCLUDED.ended_at,
            end_reason      = EXCLUDED.end_reason,
            start_geo       = EXCLUDED.start_geo,
            end_geo         = EXCLUDED.end_geo,
            distance_m      = EXCLUDED.distance_m,
            polyline        = EXCLUDED.polyline,
            sample_count    = EXCLUDED.sample_count,
            max_speed_mps   = EXCLUDED.max_speed_mps,
            avg_speed_mps   = EXCLUDED.avg_speed_mps,
            geometry_source = EXCLUDED.geometry_source,
            computed_at     = now();
        """;

    private readonly PersistenceWriterOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<TripSummary> SummariseAsync(EndedSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var stored = await connection.QuerySingleOrDefaultAsync<SessionWindow>(
            new CommandDefinition(
                SessionSql, new { session.SessionId }, cancellationToken: cancellationToken));

        if (stored is null)
        {
            // The event arrived before its own transaction was visible here. Retried by the caller,
            // because a summary lost to a race is a journey with no record of how far it went.
            logger.LogWarning(
                "No trips.sessions row for {SessionId}; cannot summarise it yet", session.SessionId);

            return new TripSummary(SummaryStatus.SessionNotFound, session.SessionId);
        }

        if (stored.State != "COMPLETED" || stored.EndedAt is not { } endedAt)
        {
            // Restarted inside the US-5.10 grace and running again. Summarising it now would store a
            // journey that has not finished; the next `session.ended` brings it back here. Committed,
            // not retried — the session may run for another hour.
            logger.LogInformation(
                "Session {SessionId} is {State} again; leaving its summary to the next end",
                session.SessionId, stored.State);

            return new TripSummary(SummaryStatus.SessionActive, session.SessionId);
        }

        var geometry = await ComputeGeometryAsync(connection, stored, endedAt, cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                UpsertSql,
                new
                {
                    stored.SessionId,
                    stored.VehicleId,
                    stored.DriverId,
                    stored.Mode,
                    stored.StartedAt,
                    EndedAt = endedAt,
                    EndReason = stored.EndReason ?? session.EndReason,
                    geometry.StartGeo,
                    geometry.EndGeo,
                    geometry.DistanceM,
                    geometry.Polyline,
                    geometry.SampleCount,
                    geometry.MaxSpeedMps,
                    geometry.AvgSpeedMps,
                    GeometrySource = geometry.Source,
                },
                cancellationToken: cancellationToken));

        MageRideDiagnostics.TripSummariesWritten.Add(
            1, new KeyValuePair<string, object?>("geometry_source", geometry.Source));

        logger.LogInformation(
            "Summarised session {SessionId}: {DistanceKm:F2} km over {Samples} fixes from {Source}",
            stored.SessionId, geometry.DistanceM / 1000, geometry.SampleCount, geometry.Source);

        return new TripSummary(
            SummaryStatus.Written, stored.SessionId, geometry.DistanceM, geometry.SampleCount,
            geometry.Source, geometry.Polyline is not null);
    }

    private async Task<Geometry> ComputeGeometryAsync(
        Npgsql.NpgsqlConnection connection,
        SessionWindow session,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        var raw = await connection.QuerySingleOrDefaultAsync<GeometryRow>(
            new CommandDefinition(
                GeometrySql,
                new
                {
                    session.VehicleId,
                    session.StartedAt,
                    EndedAt = endedAt,
                    ToleranceM = _options.PolylineToleranceM,
                },
                cancellationToken: cancellationToken));

        if (raw is { SampleCount: > 0 })
        {
            return Geometry.From(raw, GeometrySources.Telemetry);
        }

        if (!_options.AllowOperationalGeometryFallback)
        {
            return Geometry.Empty;
        }

        // The raw chunks have been dropped (ADD §9.5 item 4 retires them at 30 days) or the vehicle
        // published nothing. The 1/min samples are what is left, and they still describe the shape of
        // the journey — the distance is a lower bound and says so.
        var operational = await connection.QuerySingleOrDefaultAsync<GeometryRow>(
            new CommandDefinition(
                OperationalGeometrySql,
                new { session.SessionId, ToleranceM = _options.PolylineToleranceM },
                cancellationToken: cancellationToken));

        return operational is { SampleCount: > 0 }
            ? Geometry.From(operational, GeometrySources.Operational)
            : Geometry.Empty;
    }

    /// <summary>What <c>trips.sessions</c> says about the journey's window.</summary>
    private sealed record SessionWindow(
        Guid SessionId,
        Guid VehicleId,
        Guid DriverId,
        string Mode,
        string State,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string? EndReason);

    /// <summary>
    /// The geometry query's shape. Geometries cross as WKB rather than as PostGIS text: the kernel's
    /// <c>GeoPointTypeHandler</c> reads a point and this needs a line as well, and WKB round-trips
    /// through <c>ST_GeomFromWKB</c> without a parse on either side.
    /// </summary>
    private sealed record GeometryRow(
        long SampleCount,
        double DistanceM,
        float? MaxSpeedMps,
        double? AvgSpeedMps,
        byte[]? StartGeo,
        byte[]? EndGeo,
        byte[]? Polyline);

    private sealed record Geometry(
        int SampleCount,
        double DistanceM,
        float? MaxSpeedMps,
        double? AvgSpeedMps,
        byte[]? StartGeo,
        byte[]? EndGeo,
        byte[]? Polyline,
        string Source)
    {
        public static readonly Geometry Empty =
            new(0, 0, null, null, null, null, null, GeometrySources.None);

        public static Geometry From(GeometryRow row, string source) =>
            // count(*) is bigint; the column it lands in is INTEGER. A journey with two billion
            // fixes is not a journey.
            new((int)row.SampleCount, row.DistanceM, row.MaxSpeedMps, row.AvgSpeedMps,
                row.StartGeo, row.EndGeo, row.Polyline, source);
    }
}

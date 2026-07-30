using Dapper;
using MageRide.HotPath.PersistenceWriter.Summaries;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// ADD §9.2's trip summary — start, end, distance, polyline — computed on <c>session.ended</c>.
/// </summary>
/// <remarks>
/// The artefact §9.2 promises and no DDL source printed; migration 0506 adds
/// <c>trips.session_summaries</c> and the C040 handoff raises it as a micro-change-set. ADD §9.5
/// item 2 puts the *query* path on the continuous aggregates, which cannot answer it: they are
/// bucketed by time and know nothing about sessions.
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "PersistenceWriter")]
public sealed class TripSummaryTests(PostgresFixture postgres)
{
    /// <summary>Galle Road, heading south from Colombo Fort — about 3.3 km of it.</summary>
    private static readonly GeoPoint[] GalleRoad =
    [
        new(6.9344, 79.8428),
        new(6.9280, 79.8480),
        new(6.9210, 79.8520),
        new(6.9140, 79.8560),
        new(6.9060, 79.8590),
    ];

    [Fact]
    public async Task An_ended_session_gets_a_summary_with_start_end_distance_and_polyline()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A", startedAt: startedAt);

        // A fix every 20 s along the route — D5' §5.2's Mode A cadence, so the summary is computed
        // from full-resolution rows the way it is in production.
        var writer = WriterParts.Writer(postgres);
        var rows = Route(journey.VehicleId, startedAt, TimeSpan.FromSeconds(20));

        await writer.WriteAsync(rows, TestContext.Current.CancellationToken);

        var endedAt = startedAt.AddMinutes(20);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, endedAt);

        var summary = await WriterParts.Summaries(postgres).SummariseAsync(
            Ended(journey, endedAt), TestContext.Current.CancellationToken);

        Assert.Equal(SummaryStatus.Written, summary.Status);

        // Full-resolution rows, not the 1/min downsample: a minute of city driving is not a straight
        // line, and chaining sixty-second chords loses a third of a route with turns in it.
        Assert.Equal(GeometrySources.Telemetry, summary.GeometrySource);
        Assert.Equal(rows.Count, summary.SampleCount);
        Assert.True(summary.HasPolyline);

        // The route is ~3.3 km. Asserted as a range rather than a number: this is a great-circle
        // path length over a real polyline, and pinning it to the metre would be asserting PostGIS's
        // spheroid rather than the summary.
        Assert.InRange(summary.DistanceM, 3_000, 4_200);

        await using var connection = await postgres.OpenAsync();

        var stored = await connection.QuerySingleAsync<StoredSummary>(
            """
            SELECT session_id AS SessionId, vehicle_id AS VehicleId, driver_id AS DriverId, mode AS Mode,
                   distance_m AS DistanceM, sample_count AS SampleCount,
                   geometry_source AS GeometrySource,
                   ST_Y(start_geo::geometry) AS StartLat, ST_X(start_geo::geometry) AS StartLng,
                   ST_Y(end_geo::geometry) AS EndLat, ST_X(end_geo::geometry) AS EndLng,
                   ST_NPoints(polyline::geometry) AS PolylinePoints,
                   ST_GeometryType(polyline::geometry) AS PolylineType
              FROM trips.session_summaries WHERE session_id = @Session;
            """,
            new { Session = journey.Session });

        Assert.Equal(journey.VehicleId, stored.VehicleId);
        Assert.Equal(journey.DriverId, stored.DriverId);
        Assert.Equal("A", stored.Mode);

        // ADD §9.2's four fields, all four present and all four right way round.
        Assert.Equal(GalleRoad[0].Latitude, stored.StartLat, 4);
        Assert.Equal(GalleRoad[0].Longitude, stored.StartLng, 4);
        Assert.Equal(GalleRoad[^1].Latitude, stored.EndLat, 4);
        Assert.Equal(GalleRoad[^1].Longitude, stored.EndLng, 4);
        Assert.Equal("ST_LineString", stored.PolylineType);

        // Simplified to the 25 m tolerance, so a phone map renders a line rather than a thousand
        // vertices — and still more than the two a straight line would collapse to.
        Assert.InRange(stored.PolylinePoints, 2, rows.Count);
    }

    [Fact]
    public async Task The_distance_is_measured_before_the_line_is_simplified()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A", startedAt: startedAt);

        await WriterParts.Writer(postgres).WriteAsync(
            Route(journey.VehicleId, startedAt, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

        var endedAt = startedAt.AddMinutes(20);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, endedAt);

        // A tolerance far coarser than the route's own detail. If the distance were measured after
        // simplification it would shrink with the tolerance — and the distance is the one number in
        // a summary somebody might be paid against.
        var coarse = WriterParts.Defaults();
        coarse.PolylineToleranceM = 500;

        var summary = await WriterParts.Summaries(postgres, coarse).SummariseAsync(
            Ended(journey, endedAt), TestContext.Current.CancellationToken);

        Assert.InRange(summary.DistanceM, 3_000, 4_200);

        await using var connection = await postgres.OpenAsync();

        var points = await connection.ExecuteScalarAsync<int>(
            "SELECT ST_NPoints(polyline::geometry) FROM trips.session_summaries WHERE session_id = @Session;",
            new { Session = journey.Session });

        // The line really was simplified — otherwise this test proves nothing about the ordering.
        Assert.True(points < 6, $"a 500 m tolerance left {points} vertices on a 3.3 km route");
    }

    /// <summary>US-5.10: a session can end, restart in place, and end again.</summary>
    [Fact]
    public async Task A_session_restarted_inside_its_grace_is_not_summarised_until_it_ends_again()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "B", startedAt: startedAt);
        var summaries = WriterParts.Summaries(postgres);

        await WriterParts.Writer(postgres).WriteAsync(
            Route(journey.VehicleId, startedAt, TimeSpan.FromSeconds(20)),
            TestContext.Current.CancellationToken);

        var firstEnd = startedAt.AddMinutes(10);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, firstEnd, "idle_timeout");

        var first = await summaries.SummariseAsync(
            Ended(journey, firstEnd), TestContext.Current.CancellationToken);

        Assert.Equal(SummaryStatus.Written, first.Status);

        // The driver takes it back inside the five-minute grace. The session keeps its id, so the
        // summary already written describes a journey that is running again.
        await WriterParts.RestartJourneyAsync(postgres, journey.Session);

        var duringRestart = await summaries.SummariseAsync(
            Ended(journey, firstEnd), TestContext.Current.CancellationToken);

        // Committed, not retried: the session may run for another hour, and stalling the partition
        // would hold up every other session's summary behind it.
        Assert.Equal(SummaryStatus.SessionActive, duringRestart.Status);

        var secondEnd = startedAt.AddMinutes(25);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, secondEnd);

        var second = await summaries.SummariseAsync(
            Ended(journey, secondEnd), TestContext.Current.CancellationToken);

        Assert.Equal(SummaryStatus.Written, second.Status);

        await using var connection = await postgres.OpenAsync();

        // Upserted, not appended — one summary per session however many times it ends.
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM trips.session_summaries WHERE session_id = @Session;",
                new { Session = journey.Session }));

        // Within a microsecond, not exactly: `timestamptz` stores microseconds and a
        // DateTimeOffset counts 100 ns ticks, so a round trip truncates. Comparing exactly makes the
        // test pass or fail on whether the clock happened to land on a microsecond boundary.
        Assert.Equal(
            secondEnd,
            await connection.ExecuteScalarAsync<DateTimeOffset>(
                "SELECT ended_at FROM trips.session_summaries WHERE session_id = @Session;",
                new { Session = journey.Session }),
            TimeSpan.FromMicroseconds(1));
    }

    [Fact]
    public async Task A_session_whose_row_is_not_visible_yet_is_reported_for_retry()
    {
        await RequireAsync();

        // The outbox dispatcher publishes post-commit, but a replica can still lag. Retried rather
        // than committed past: a summary lost to that race is a journey with no record of how far it
        // went, and nothing recomputes it.
        var summary = await WriterParts.Summaries(postgres).SummariseAsync(
            new EndedSession(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
                "B", "driver_ended", DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal(SummaryStatus.SessionNotFound, summary.Status);
    }

    [Fact]
    public async Task A_session_that_produced_no_fixes_still_gets_a_summary_saying_so()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "B", startedAt: startedAt);

        var endedAt = startedAt.AddMinutes(5);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, endedAt);

        // A driver who started and immediately stopped, or a tracker that never reported. A real
        // outcome, and a row that says "no fixes" is more useful than no row at all.
        var summary = await WriterParts.Summaries(postgres).SummariseAsync(
            Ended(journey, endedAt), TestContext.Current.CancellationToken);

        Assert.Equal(SummaryStatus.Written, summary.Status);
        Assert.Equal(GeometrySources.None, summary.GeometrySource);
        Assert.Equal(0, summary.DistanceM);
        Assert.False(summary.HasPolyline);
    }

    [Fact]
    public async Task The_operational_samples_are_the_fallback_when_the_raw_rows_are_gone()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A", startedAt: startedAt);

        // Written a minute apart, so each fix is its own operational bucket…
        await WriterParts.Writer(postgres).WriteAsync(
            Route(journey.VehicleId, startedAt, TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken);

        // …and then the raw chunks are gone, which is what ADD §9.5 item 4's 30-day retention does
        // to a summary computed from a replayed event.
        await using (var connection = await postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                "DELETE FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
                new { journey.VehicleId });
        }

        var endedAt = startedAt.AddMinutes(20);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, endedAt);

        var summary = await WriterParts.Summaries(postgres).SummariseAsync(
            Ended(journey, endedAt), TestContext.Current.CancellationToken);

        Assert.Equal(SummaryStatus.Written, summary.Status);

        // Labelled, so a reader comparing two journeys can see that this distance is a lower bound
        // taken over 1/min samples rather than a full-resolution path.
        Assert.Equal(GeometrySources.Operational, summary.GeometrySource);
        Assert.Equal(GalleRoad.Length, summary.SampleCount);
        Assert.True(summary.DistanceM > 0);
    }

    [Fact]
    public async Task The_fallback_can_be_switched_off_and_then_the_summary_says_none()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A", startedAt: startedAt);

        await WriterParts.Writer(postgres).WriteAsync(
            Route(journey.VehicleId, startedAt, TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken);

        await using (var connection = await postgres.OpenAsync())
        {
            await connection.ExecuteAsync(
                "DELETE FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
                new { journey.VehicleId });
        }

        var endedAt = startedAt.AddMinutes(20);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, endedAt);

        var strict = WriterParts.Defaults();
        strict.AllowOperationalGeometryFallback = false;

        var summary = await WriterParts.Summaries(postgres, strict).SummariseAsync(
            Ended(journey, endedAt), TestContext.Current.CancellationToken);

        Assert.Equal(GeometrySources.None, summary.GeometrySource);
        Assert.Equal(0, summary.DistanceM);
    }

    /// <summary>
    /// The one string this service and trip-state-svc have to agree on and neither can see the other
    /// declare.
    /// </summary>
    /// <remarks>
    /// persistence-writer-svc must not reference TripState.Api — it consumes that service's topic,
    /// not its code. So the event name is spelled twice, and this is where a rename fails: without
    /// it, the two would diverge in production as summaries that silently stop being written.
    /// </remarks>
    [Fact]
    public void The_event_that_closes_a_journey_is_the_one_trip_state_svc_publishes() =>
        Assert.Equal(
            MageRide.TripState.Sessions.SessionEventTypes.SessionEnded,
            TripEventConsumer.SessionEndedEvent);

    // ---------------------------------------------------------------------------------------------

    private static EndedSession Ended(WriterParts.Journey journey, DateTimeOffset endedAt) =>
        new(journey.Session, journey.VehicleId, journey.DriverId, journey.Mode, "driver_ended", endedAt);

    /// <summary>The Galle Road route as a batch, one fix every <paramref name="cadence"/>.</summary>
    private static List<MageRide.HotPath.PersistenceWriter.Persistence.PositionRow> Route(
        Guid vehicleId, DateTimeOffset from, TimeSpan cadence) =>
        WriterParts.Rows(
            [.. GalleRoad.Select((point, index) =>
                WriterParts.Fix(vehicleId, point, seq: index + 1, from + (cadence * index)))]);

    private async Task RequireAsync()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await postgres.EnsureMigratedAsync();
    }

    private sealed record StoredSummary(
        Guid SessionId,
        Guid VehicleId,
        Guid DriverId,
        string Mode,
        double DistanceM,
        int SampleCount,
        string GeometrySource,
        double StartLat,
        double StartLng,
        double EndLat,
        double EndLng,
        int PolylinePoints,
        string PolylineType);
}

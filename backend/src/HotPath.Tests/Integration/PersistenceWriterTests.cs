using System.Diagnostics;
using Dapper;
using MageRide.HotPath.PersistenceWriter.Configuration;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using PositionRow = MageRide.HotPath.PersistenceWriter.Persistence.PositionRow;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// persistence-writer-svc's batch write path against a real TimescaleDB hypertable
/// (ADD §9.5, §9.2, T-06, T-10).
/// </summary>
/// <remarks>
/// These drive <c>PositionBatchWriter</c> directly, so an assertion about what a batch wrote cannot
/// be confused with one about whether Kafka delivered it — <see cref="PersistenceDurabilityTests"/>
/// answers the second.
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "PersistenceWriter")]
public sealed class PersistenceWriterTests(PostgresFixture postgres)
{
    private static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    [Fact]
    public async Task A_batch_is_COPYed_into_the_hypertable_with_every_column_the_DDL_names()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A");
        var writer = WriterParts.Writer(postgres);
        var captured = DateTimeOffset.UtcNow.AddMinutes(-5);

        var sample = WriterParts.Fix(journey.VehicleId, ColomboFort, seq: 1, captured);
        var outcome = await writer.WriteAsync(WriterParts.Rows(sample), TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Rows);
        Assert.Equal(1, outcome.Inserted);

        await using var connection = await postgres.OpenAsync();

        var row = await connection.QuerySingleAsync<StoredPosition>(
            """
            SELECT vehicle_id AS VehicleId, sample_ts AS SampleTs, received_ts AS ReceivedTs, seq AS Seq,
                   lat AS Lat, lng AS Lng, speed_mps AS SpeedMps, heading_deg AS HeadingDeg,
                   accuracy_m AS AccuracyM, sat_count AS SatCount, source AS Source, fleet_id AS FleetId
              FROM telemetry.positions WHERE vehicle_id = @VehicleId;
            """,
            new { journey.VehicleId });

        Assert.Equal(sample.Lat, row.Lat, 6);
        Assert.Equal(sample.Lng, row.Lng, 6);
        Assert.Equal(1, row.Seq);
        Assert.Equal(8.5, row.SpeedMps!.Value, 3);
        Assert.Equal((short)90, row.HeadingDeg);
        Assert.Equal((short)9, row.SatCount);
        Assert.Equal((short)PositionSource.Gt06, row.Source);

        // received_ts is NOT NULL DEFAULT now(), and a COPY supplies no defaults — it has to be
        // written, or the replay lag the column exists to measure would be the write time.
        Assert.Equal(sample.ReceivedTs!.Value, row.ReceivedTs, TimeSpan.FromMilliseconds(1));
        Assert.True(row.ReceivedTs > row.SampleTs);
    }

    /// <summary>The DoD's second line.</summary>
    [Fact]
    public async Task A_duplicate_vehicle_and_seq_batch_does_not_create_duplicate_rows()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A");
        var writer = WriterParts.Writer(postgres);
        var captured = DateTimeOffset.UtcNow.AddMinutes(-5);

        var batch = WriterParts.Rows(
            WriterParts.Fix(journey.VehicleId, ColomboFort, seq: 1, captured),
            WriterParts.Fix(journey.VehicleId, ColomboFort, seq: 2, captured.AddSeconds(5)),
            WriterParts.Fix(journey.VehicleId, ColomboFort, seq: 3, captured.AddSeconds(10)));

        var first = await writer.WriteAsync(batch, TestContext.Current.CancellationToken);
        Assert.Equal(3, first.Inserted);

        // The same batch again — which is what a rebalance, a restart or a kill mid-flush produces
        // (D6' §2.3 is at-least-once). ux_positions_vehicle_seq is what makes it a no-op.
        var second = await writer.WriteAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal(3, second.Rows);
        Assert.Equal(0, second.Inserted);

        await using var connection = await postgres.OpenAsync();

        Assert.Equal(
            3,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
                new { journey.VehicleId }));
    }

    [Fact]
    public async Task A_duplicate_inside_one_batch_is_collapsed_rather_than_raising()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A");
        var writer = WriterParts.Writer(postgres);
        var captured = DateTimeOffset.UtcNow.AddMinutes(-5);

        // A COPY has no conflict handling at all, so a duplicate inside one batch would take the
        // whole batch down if the staging table carried the unique index. It does not; the
        // set-based insert's DISTINCT ON is what resolves this.
        var sample = WriterParts.Fix(journey.VehicleId, ColomboFort, seq: 7, captured);
        var outcome = await writer.WriteAsync(
            WriterParts.Rows(sample, sample), TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Rows);
        Assert.Equal(1, outcome.Inserted);
    }

    /// <summary>The DoD's first line, on this box's profile.</summary>
    /// <remarks>
    /// Measured against the write path itself rather than through Redpanda: the claim is about
    /// <c>COPY</c> throughput into the hypertable (ADD §9.5 item 5 puts it at 40k rows/s on 4 vCPU),
    /// and routing it through a broker would make this a Redpanda benchmark with a database in it.
    /// <see cref="PersistenceDurabilityTests"/> proves the broker path separately.
    /// </remarks>
    [Fact]
    public async Task Sustained_ingest_of_three_thousand_rows_a_second_is_written_without_backlog()
    {
        await RequireAsync();

        const int vehicles = 30;
        const int perVehicle = 400;
        const int total = vehicles * perVehicle;   // 12,000 rows

        var options = WriterParts.Defaults();
        options.OperationalSamplingEnabled = false;   // Mode C traffic writes no samples anyway.

        var writer = WriterParts.Writer(postgres, options: options);
        var ids = Enumerable.Range(0, vehicles).Select(static _ => Guid.CreateVersion7()).ToArray();
        var start = DateTimeOffset.UtcNow.AddHours(-2);

        // Interleaved by vehicle, the way a real partition arrives — a batch of a thousand carries a
        // slice of every vehicle in the shard rather than one vehicle's whole minute, which is what
        // decides how many chunks the COPY touches.
        var rows = new List<PositionRow>(total);

        for (var seq = 0; seq < perVehicle; seq++)
        {
            foreach (var vehicleId in ids)
            {
                rows.Add(new PositionRow(
                    WriterParts.Fix(
                        vehicleId,
                        new GeoPoint(ColomboFort.Latitude + (seq * 0.00002), ColomboFort.Longitude),
                        seq,
                        start.AddSeconds(seq * 5)),
                    null));
            }
        }

        var batches = rows.Chunk(options.BatchRows).Select(static chunk => chunk.ToList()).ToList();

        // Everything is generated before the clock starts, so the measurement is the write path and
        // not this test's own object allocation.
        var clock = Stopwatch.StartNew();
        var written = 0;

        foreach (var batch in batches)
        {
            written += (await writer.WriteAsync(batch, TestContext.Current.CancellationToken)).Inserted;
        }

        clock.Stop();

        Assert.Equal(total, written);

        var rate = total / clock.Elapsed.TotalSeconds;

        Assert.True(
            rate >= 3_000,
            $"Wrote {total} rows in {clock.Elapsed.TotalSeconds:F2} s = {rate:F0} rows/s; the DoD is " +
            $"3000 msg/s sustained on the dev box profile. ADD §9.5 item 5 budgets 40k rows/s on 4 vCPU.");
    }

    // --- ADD §9.2: the 1/min operational downsample ----------------------------------------------

    /// <summary>The first fence: raw positions go to <c>telemetry.positions</c> only.</summary>
    [Fact]
    public async Task A_minute_of_Mode_A_fixes_becomes_one_operational_sample_and_many_hypertable_rows()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A");
        var writer = WriterParts.Writer(postgres);

        // Twelve fixes at a five-second cadence — one minute of D5' §5.2 Mode A standby reporting.
        var minute = new DateTimeOffset(2026, 7, 30, 8, 15, 0, TimeSpan.Zero);
        var batch = WriterParts.Rows(
            [.. Enumerable.Range(0, 12).Select(i =>
                WriterParts.Fix(
                    journey.VehicleId,
                    new GeoPoint(ColomboFort.Latitude + (i * 0.0001), ColomboFort.Longitude),
                    seq: i + 1,
                    minute.AddSeconds(i * 5)))]);

        var outcome = await writer.WriteAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal(12, outcome.Inserted);

        // ADD §9.2 and §21: "high-frequency raw GPS never lands in Postgres operational tables".
        // Twelve rows in the hypertable, one in the operational table.
        Assert.Equal(1, outcome.Sampled);

        await using var connection = await postgres.OpenAsync();

        var stored = await connection.QuerySingleAsync<StoredSample>(
            """
            SELECT session_id AS SessionId, vehicle_id AS VehicleId, sample_ts AS SampleTs,
                   ST_Y(geo::geometry) AS Lat, ST_X(geo::geometry) AS Lng, speed_mps AS SpeedMps
              FROM trips.position_samples WHERE session_id = @Session;
            """,
            new { Session = journey.Session });

        // The row is stamped at the minute boundary, which is what makes the write idempotent
        // against ux_possample_session_minute without any per-vehicle memory.
        Assert.Equal(minute, stored.SampleTs);

        // …and it carries the FIRST fix of the minute, decidable on arrival.
        Assert.Equal(ColomboFort.Latitude, stored.Lat, 6);
    }

    [Fact]
    public async Task The_operational_sample_is_idempotent_across_a_redelivery()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "B");
        var writer = WriterParts.Writer(postgres);

        var minute = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

        // Two batches whose fixes fall in the same minute but carry different sequences — which is
        // what a rebalance mid-minute produces, and the case an in-process "last written minute"
        // would get wrong after a restart.
        var first = WriterParts.Rows(WriterParts.Fix(journey.VehicleId, ColomboFort, 1, minute));
        var second = WriterParts.Rows(
            WriterParts.Fix(journey.VehicleId, ColomboFort, 2, minute.AddSeconds(30)));

        Assert.Equal(1, (await writer.WriteAsync(first, TestContext.Current.CancellationToken)).Sampled);
        Assert.Equal(0, (await writer.WriteAsync(second, TestContext.Current.CancellationToken)).Sampled);

        await using var connection = await postgres.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM trips.position_samples WHERE session_id = @Session;",
                new { Session = journey.Session }));
    }

    /// <summary>R-01: a Mode C vehicle has no tracking session, all day, by construction.</summary>
    [Fact]
    public async Task A_Mode_C_vehicle_writes_to_the_hypertable_and_to_no_operational_table()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "C");
        var writer = WriterParts.Writer(postgres);

        var outcome = await writer.WriteAsync(
            WriterParts.Rows(
                WriterParts.Fix(journey.VehicleId, ColomboFort, 1, DateTimeOffset.UtcNow.AddMinutes(-3))),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Inserted);
        Assert.Equal(0, outcome.Sampled);
    }

    [Fact]
    public async Task A_fix_from_before_its_session_started_is_still_persisted_but_not_sampled()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A", startedAt: startedAt);
        var writer = WriterParts.Writer(postgres);

        // A vehicle publishing before the driver pressed Start Journey — idling at the depot, or a
        // tracker that never stopped after the last journey.
        var outcome = await writer.WriteAsync(
            WriterParts.Rows(
                WriterParts.Fix(journey.VehicleId, ColomboFort, 1, startedAt.AddMinutes(-5)),
                WriterParts.Fix(journey.VehicleId, ColomboFort, 2, startedAt.AddMinutes(2))),
            TestContext.Current.CancellationToken);

        // Both are real telemetry and both belong in the hypertable.
        Assert.Equal(2, outcome.Inserted);

        // Only the one inside the journey is attributed to it: the earlier fix would otherwise put
        // the depot in the trip's polyline and add its distance to the journey's.
        Assert.Equal(1, outcome.Sampled);

        await using var connection = await postgres.OpenAsync();

        var sampled = await connection.ExecuteScalarAsync<DateTimeOffset>(
            "SELECT sample_ts FROM trips.position_samples WHERE session_id = @Session;",
            new { Session = journey.Session });

        Assert.True(sampled >= WriterParts.Truncate(startedAt), $"{sampled:O} precedes the journey");
    }

    // --- mqtt-topics.md §6: "C040 must populate fleetId" -----------------------------------------

    [Fact]
    public async Task The_owning_fleet_is_denormalised_onto_every_row()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A");
        var fleetId = await WriterParts.JoinFleetAsync(postgres, journey.VehicleId);

        var resolver = WriterParts.Resolver();
        var writer = WriterParts.Writer(postgres, resolver: resolver);

        await using var lookup = await postgres.OpenAsync();
        var resolved = await resolver.ResolveFleetsAsync(
            lookup, null, [journey.VehicleId], TestContext.Current.CancellationToken);

        Assert.Equal(fleetId, resolved[journey.VehicleId]);

        var sample = WriterParts.Fix(journey.VehicleId, ColomboFort, 1, DateTimeOffset.UtcNow.AddMinutes(-2));

        await writer.WriteAsync(
            [new PositionRow(sample, fleetId)],
            TestContext.Current.CancellationToken);

        await using var connection = await postgres.OpenAsync();

        // Without this the fleet-scoped view (1804) returns nothing for the fleet that owns the
        // vehicle, and an Epic 13 operator sees an empty map for their own buses.
        Assert.Equal(
            fleetId,
            await connection.ExecuteScalarAsync<Guid>(
                "SELECT fleet_id FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
                new { journey.VehicleId }));
    }

    [Fact]
    public async Task A_vehicle_in_no_fleet_resolves_to_null_and_is_cached_as_such()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "C");
        var resolver = WriterParts.Resolver();

        await using var connection = await postgres.OpenAsync();

        // The common answer for both lookups, and the reason they cache their misses: every Mode C
        // vehicle on the platform is on this topic and belongs to no fleet.
        var first = await resolver.ResolveFleetsAsync(
            connection, null, [journey.VehicleId], TestContext.Current.CancellationToken);

        Assert.Empty(first);

        var second = await resolver.ResolveFleetsAsync(
            connection, null, [journey.VehicleId], TestContext.Current.CancellationToken);

        Assert.Empty(second);
        Assert.Equal(1, resolver.CacheSize.Fleets);
    }

    // --- Poison batches --------------------------------------------------------------------------

    [Fact]
    public async Task A_row_the_hypertable_refuses_is_dead_lettered_and_its_neighbours_are_written()
    {
        await RequireAsync();

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A");
        var deadLetters = new WriterParts.CollectingDeadLetterSink();
        var writer = WriterParts.Writer(postgres, deadLetters);

        var captured = DateTimeOffset.UtcNow.AddMinutes(-4);

        // 999 degrees fails ck_positions_lng. C039 refuses this upstream, so a row that reaches here
        // is a producer that has changed shape — retrying it forever would stall the partition every
        // other vehicle in the shard shares.
        var poison = WriterParts.Fix(journey.VehicleId, ColomboFort, 2, captured.AddSeconds(5))
                     with { Lng = 999 };

        var batch = WriterParts.Rows(
            WriterParts.Fix(journey.VehicleId, ColomboFort, 1, captured),
            poison,
            WriterParts.Fix(journey.VehicleId, ColomboFort, 3, captured.AddSeconds(10)));

        var outcome = await writer.WriteAsync(batch, TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.DeadLettered);
        Assert.Equal(2, outcome.Inserted);

        var sent = Assert.Single(deadLetters.Sent);

        Assert.Equal(2, sent.Sample.Seq);
        Assert.Contains("ck_positions_lng", sent.Reason, StringComparison.Ordinal);

        await using var connection = await postgres.OpenAsync();

        // The two good rows are in. A batch that failed whole would have written neither.
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM telemetry.positions WHERE vehicle_id = @VehicleId;",
                new { journey.VehicleId }));
    }

    [Fact]
    public async Task A_transient_failure_is_never_dead_lettered()
    {
        // A connection reset, a deadlock, a compressed chunk — everything outside SQLSTATE classes
        // 22 and 23 has to be retried, because committing past it silently loses telemetry the
        // hypertable is the system of record for. Asserted on the classifier rather than by breaking
        // a container: the branch it guards is what decides between "retry" and "drop".
        Assert.True(IsDataError("22003"));   // numeric value out of range
        Assert.True(IsDataError("23514"));   // check violation
        Assert.False(IsDataError("40001"));  // serialization failure
        Assert.False(IsDataError("53100"));  // disk full
        Assert.False(IsDataError("08006"));  // connection failure

        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------

    private static bool IsDataError(string sqlState) =>
        MageRide.HotPath.PersistenceWriter.Persistence.PositionBatchWriter.IsDataError(
            new Npgsql.PostgresException("test", "ERROR", "ERROR", sqlState));

    private async Task RequireAsync()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await postgres.EnsureMigratedAsync();
    }

    private sealed record StoredPosition(
        Guid VehicleId,
        DateTimeOffset SampleTs,
        DateTimeOffset ReceivedTs,
        long Seq,
        double Lat,
        double Lng,
        float? SpeedMps,
        short? HeadingDeg,
        float? AccuracyM,
        short? SatCount,
        short Source,
        Guid? FleetId);

    private sealed record StoredSample(
        Guid SessionId, Guid VehicleId, DateTimeOffset SampleTs, double Lat, double Lng, float? SpeedMps);
}

using Confluent.Kafka;
using Dapper;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Messaging;
using MageRide.Shared.Primitives;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using StackExchange.Redis;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// The two DoD lines that are about the seam rather than about a write: a writer killed mid-batch
/// loses nothing, and the live map does not notice it is gone.
/// </summary>
/// <remarks>
/// These run the real service against a real Redpanda and a real hypertable, because both claims are
/// about what survives a process death — and a test that drove the writer in-process would prove
/// only that the method returned.
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "PersistenceWriter")]
public sealed class PersistenceDurabilityTests(
    EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis, PostgresFixture postgres)
{
    private static readonly GeoPoint ColomboFort = new(6.9344, 79.8428);

    /// <summary>Generous enough that a slow container is not read as a lost row.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The DoD's third line.</summary>
    /// <remarks>
    /// The writer is started, killed while it is still working through the backlog, and restarted on
    /// the <b>same consumer group</b> — which is the only arrangement that can tell "the offset
    /// survived" from "the second process started from scratch". Every row has to arrive exactly once:
    /// nothing lost means offsets were never committed ahead of the write, and nothing duplicated
    /// means <c>ux_positions_vehicle_seq</c> absorbed whatever was redelivered.
    /// </remarks>
    [Fact]
    public async Task Killing_the_writer_mid_batch_loses_no_rows_and_duplicates_none()
    {
        await RequireAsync();

        const int samples = 600;

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "C");
        var group = $"writer-restart-{Guid.NewGuid():N}";
        var start = DateTimeOffset.UtcNow.AddHours(-1);

        await redpanda.CreateTopicAsync(EventTopics.TelemetryNormalized);
        await ProduceAsync(journey.VehicleId, start, samples);

        // Small batches so the kill lands between flushes rather than before the first one.
        var options = new HotPathHarnessOptions
        {
            Writer = true,
            BatchRows = 25,
            FlushInterval = TimeSpan.FromMilliseconds(100),
            WriterConsumerGroup = group,
        };

        long midway;

        await using (var first = await HotPathHarness.StartAsync(emqx, redpanda, redis, options, postgres))
        {
            // Wait until it is demonstrably mid-backlog, then kill it. Not a sleep: a fixed delay
            // would either kill it before it started or after it had finished.
            await WaitUntilAsync(
                async () => await CountAsync(journey.VehicleId) is > 50 and < samples,
                "the writer should be part-way through the backlog");

            midway = await CountAsync(journey.VehicleId);

            await first.StopWriterAsync();
        }

        Assert.InRange(midway, 51, samples - 1);

        // A second process, same group. It resumes from the last committed offset, which is behind
        // whatever the first one had in flight.
        await using var second = await HotPathHarness.StartAsync(emqx, redpanda, redis, options, postgres);

        await WaitUntilAsync(
            async () => await CountAsync(journey.VehicleId) >= samples,
            $"the restarted writer should finish the backlog (it stopped at {midway} of {samples})");

        Assert.Equal(samples, await CountAsync(journey.VehicleId));

        // And exactly once each. A replayed batch that had already been written must produce no
        // second row — the property the whole two-step COPY exists for.
        await using var connection = await postgres.OpenAsync();

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<long>(
                """
                SELECT count(*) FROM (
                  SELECT seq FROM telemetry.positions
                   WHERE vehicle_id = @VehicleId
                   GROUP BY seq HAVING count(*) > 1) AS duplicates;
                """,
                new { journey.VehicleId }));
    }

    /// <summary>
    /// The DoD's fourth line, and this component's second fence: "a slow or failed write must not
    /// affect the live map — degrade by buffering and alerting".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window here is seconds rather than the DoD's literal sixty. The property is not
    /// time-dependent and there is nothing on the path with a minute-scale timeout: the writer is a
    /// separate consumer group on <c>telemetry.normalized</c>, the live map is Redis written by
    /// position-processor-svc, and persistence-writer-svc does not register a Redis client at all —
    /// so a longer sleep would exercise the same absence of coupling for longer. What the shortened
    /// window costs is coverage of a timeout nobody has configured; the C040 handoff says so.
    /// </para>
    /// <para>
    /// What is asserted instead is the whole of the fence: the live map keeps advancing while the
    /// writer is dead, the durable table does <i>not</i>, and the backlog is still there to be
    /// written when it comes back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_live_map_keeps_working_while_the_writer_is_stopped_and_the_backlog_survives()
    {
        await RequireAsync();
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "C");
        var group = $"writer-outage-{Guid.NewGuid():N}";
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);

        await redpanda.CreateTopicAsync(EventTopics.TelemetryNormalized);

        var options = new HotPathHarnessOptions
        {
            BridgeReplicas = 1,
            Processor = true,
            Writer = true,
            BatchRows = 10,
            FlushInterval = TimeSpan.FromMilliseconds(100),
            WriterConsumerGroup = group,
        };

        await using var harness = await HotPathHarness.StartAsync(emqx, redpanda, redis, options, postgres);
        await harness.WaitForBridgesAsync();

        await using var connection = await ConnectRedisAsync();
        var db = connection.GetDatabase();

        // One position all the way through: EMQX -> bridge -> processor -> Redis, and separately
        // -> writer -> hypertable.
        await using (var device = await DeviceClient.ConnectAsync(emqx, journey.VehicleId))
        {
            await device.PublishPositionAsync(Sample(journey.VehicleId, ColomboFort, 1, start));

            await WaitUntilAsync(
                async () => await CountAsync(journey.VehicleId) == 1,
                "the first sample should reach the hypertable");

            // Now the writer dies.
            await harness.StopWriterAsync();

            // …and the vehicle keeps moving. Ten samples over a window the writer is absent for.
            for (var seq = 2; seq <= 11; seq++)
            {
                await device.PublishPositionAsync(
                    Sample(
                        journey.VehicleId,
                        new GeoPoint(ColomboFort.Latitude + (seq * 0.0002), ColomboFort.Longitude),
                        seq,
                        start.AddSeconds(seq * 10)));

                await Task.Delay(250);
            }

            // THE FENCE. The live map is current — the passenger's view of this vehicle never
            // depended on the system of record, and could not have: this service registers no Redis
            // client at all.
            await WaitUntilAsync(
                async () =>
                {
                    var position = await db.GeoPositionAsync(RedisKeys.GeoLive, journey.VehicleId.ToString());
                    return position is { } live
                           && live.Latitude >= ColomboFort.Latitude + (11 * 0.0002) - 0.0001;
                },
                "the live map should still be tracking the vehicle with the writer stopped");

            // And the durable table is demonstrably behind, which is what makes the assertion above
            // mean something.
            Assert.Equal(1, await CountAsync(journey.VehicleId));
        }

        // The backlog was never lost — it sat on telemetry.normalized, which D6' §2.1 retains for
        // seven days. A new process on the same group finds it.
        await using var restarted = await HotPathHarness.StartAsync(emqx, redpanda, redis, options, postgres);

        await WaitUntilAsync(
            async () => await CountAsync(journey.VehicleId) >= 11,
            "the restarted writer should catch up everything published while it was down");

        Assert.Equal(11, await CountAsync(journey.VehicleId));
    }

    /// <summary>
    /// The <c>trip.events</c> path end to end: an event on the broker becomes a stored summary.
    /// </summary>
    /// <remarks>
    /// Every other summary test calls the service directly so a background consumer cannot race an
    /// assertion; this one exists because that is exactly what they cannot prove — that the topic
    /// name, the envelope's property names and the consumer group line up with what trip-state-svc
    /// actually publishes.
    /// </remarks>
    [Fact]
    public async Task A_session_ended_event_on_the_broker_becomes_a_stored_trip_summary()
    {
        await RequireAsync();

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var journey = await WriterParts.CreateJourneyAsync(postgres, mode: "A", startedAt: startedAt);

        // The journey's fixes, written directly — this test is about the event, not the batch path.
        await WriterParts.Writer(postgres).WriteAsync(
            WriterParts.Rows(
                [.. Enumerable.Range(0, 5).Select(i =>
                    WriterParts.Fix(
                        journey.VehicleId,
                        new GeoPoint(ColomboFort.Latitude - (i * 0.006), ColomboFort.Longitude + (i * 0.004)),
                        seq: i + 1,
                        startedAt.AddMinutes(i * 2)))]),
            TestContext.Current.CancellationToken);

        var endedAt = startedAt.AddMinutes(10);
        await WriterParts.EndJourneyAsync(postgres, journey.Session, endedAt);

        await redpanda.CreateTopicAsync(EventTopics.TripEvents);

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis,
            new HotPathHarnessOptions { Summaries = true },
            postgres);

        // The consumer reads from the earliest offset — a session that ended while the writer was
        // down still needs its summary — so this does not have to race the subscription.
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = redpanda.BootstrapServers }).Build();

        var envelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            sessionId = journey.Session,
            vehicleId = journey.VehicleId,
            driverId = journey.DriverId,
            mode = journey.Mode,
            endReason = "driver_ended",
            endedBy = "driver",
            endedAt,
        });

        await producer.ProduceAsync(
            EventTopics.TripEvents,
            new Message<string, byte[]>
            {
                // Keyed by vehicle, as D6' §2.1 and C031 both have it.
                Key = journey.VehicleId.ToString(),
                Value = System.Text.Encoding.UTF8.GetBytes(envelope),
                Headers = new Headers
                {
                    { "eventType", System.Text.Encoding.UTF8.GetBytes("session.ended") },
                },
            });

        await WaitUntilAsync(
            async () => await SummaryCountAsync(journey.Session) == 1,
            "session.ended should produce a trip summary");

        await using var connection = await postgres.OpenAsync();

        var summary = await connection.QuerySingleAsync<StoredSummary>(
            """
            SELECT distance_m AS DistanceM, geometry_source AS GeometrySource
              FROM trips.session_summaries WHERE session_id = @Session;
            """,
            new { Session = journey.Session });

        Assert.True(summary.DistanceM > 0, "the summary carries no distance");
        Assert.Equal("telemetry", summary.GeometrySource);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task ProduceAsync(Guid vehicleId, DateTimeOffset start, int count)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = redpanda.BootstrapServers }).Build();

        for (var seq = 1; seq <= count; seq++)
        {
            producer.Produce(
                EventTopics.TelemetryNormalized,
                new Message<string, byte[]>
                {
                    Key = vehicleId.ToString(),
                    Value = PositionSampleCodec.Encode(
                        Sample(
                            vehicleId,
                            new GeoPoint(ColomboFort.Latitude + (seq * 0.00001), ColomboFort.Longitude),
                            seq,
                            start.AddSeconds(seq))),
                    Headers = new Headers
                    {
                        { TelemetryHeaders.Stream, System.Text.Encoding.UTF8.GetBytes(TelemetryHeaders.Live) },
                    },
                });
        }

        producer.Flush(TimeSpan.FromSeconds(30));
    }

    private static PositionSample Sample(Guid vehicleId, GeoPoint point, long seq, DateTimeOffset at) =>
        WriterParts.Fix(vehicleId, point, seq, at);

    private async Task<long> CountAsync(Guid vehicleId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM telemetry.positions WHERE vehicle_id = @vehicleId;", new { vehicleId });
    }

    private async Task<long> SummaryCountAsync(Guid sessionId)
    {
        await using var connection = await postgres.OpenAsync();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM trips.session_summaries WHERE session_id = @sessionId;", new { sessionId });
    }

    private async Task<ConnectionMultiplexer> ConnectRedisAsync() =>
        await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(150);
        }

        Assert.Fail($"Timed out waiting: {because}.");
    }

    private sealed record StoredSummary(double DistanceM, string GeometrySource);

    private async Task RequireAsync()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await postgres.EnsureMigratedAsync();
    }
}

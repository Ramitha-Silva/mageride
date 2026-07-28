using System.Globalization;
using MageRide.HotPath.PositionProcessor.Configuration;
using MageRide.HotPath.PositionProcessor.Processing;
using MageRide.HotPath.PositionProcessor.Redis;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Caching;
using MageRide.Shared.Geo;
using MageRide.Shared.Messaging;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
// `PositionProcessor` is both a namespace (MageRide.HotPath.PositionProcessor) and the class inside
// it. From a MageRide.HotPath.* namespace the namespace wins, so the class is named explicitly.
using Processor = MageRide.HotPath.PositionProcessor.Processing.PositionProcessor;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// position-processor-svc against a real Redis and a real Redpanda: the live indexes a sample
/// produces, and the R-17/T-05 replay watermark (ADD §9.4, <c>mqtt-topics.md</c> §5).
/// </summary>
/// <remarks>
/// The indexing tests drive <see cref="LivePositionIndex"/> and <see cref="Processor"/>
/// directly rather than through the consumer, so an assertion about <i>what</i> a sample writes
/// cannot be confused with one about <i>whether</i> a consumer delivered it — the pipeline tests
/// answer that.
/// </remarks>
[Collection<HotPathCollection>]
public sealed class PositionProcessorTests(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis)
{
    [Fact]
    public async Task A_sample_lands_in_geo_live_its_meta_hash_and_its_res_7_cell_stream()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var index = NewIndex(connection);

        var sample = Samples.At(vehicleId, Samples.ColomboFort, seq: 5);
        var cell = await index.RecordAsync(sample, CancellationToken.None);

        // ADD §7.4 step 1: the fan-out grid is res 7. A cell at any other resolution is a group
        // nothing publishes to.
        Assert.Equal(GeoCells.ViewCell(Samples.ColomboFort), cell);
        Assert.NotNull(cell);

        var db = connection.GetDatabase();

        var position = await db.GeoPositionAsync(RedisKeys.GeoLive, vehicleId.ToString());
        Assert.NotNull(position);
        Assert.Equal(sample.Lat, position!.Value.Latitude, precision: 4);
        Assert.Equal(sample.Lng, position.Value.Longitude, precision: 4);

        var meta = await db.HashGetAllAsync(RedisKeys.VehicleMeta(vehicleId));
        var fields = meta.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString());

        Assert.Equal("three_wheeler", fields["type"]);
        Assert.Equal("C", fields["mode"]);
        Assert.Equal(cell, fields["cell"]);

        // A cell is a place, not a test fixture: every test that puts a vehicle at Colombo Fort
        // writes to this same stream, and the Redis container is shared across the collection. So
        // the entry is found by vehicle id rather than by being the only one there.
        var entries = await db.StreamRangeAsync(RedisKeys.Cell(cell!));
        var mine = entries
            .Select(entry => entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString()))
            .Where(fields => fields[CellStreamFields.VehicleId] == vehicleId.ToString())
            .ToArray();

        var streamFields = Assert.Single(mine);

        // These names are the contract with fanout-svc, which cannot reference this assembly. They
        // are the VehicleFrame property names on purpose, so fan-out is a projection.
        Assert.Equal(vehicleId.ToString(), streamFields[CellStreamFields.VehicleId]);
        Assert.Equal("three_wheeler", streamFields[CellStreamFields.Type]);
        Assert.Equal("90", streamFields[CellStreamFields.Heading]);
        Assert.Equal(
            sample.Lat, double.Parse(streamFields[CellStreamFields.Lat], CultureInfo.InvariantCulture), precision: 6);
    }

    [Fact]
    public async Task A_replayed_seq_is_discarded_on_the_watermark()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var index = NewIndex(connection);

        Assert.NotNull(await index.RecordAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 10), default));

        // R-17/T-05, layer 1: a tracker that reconnects after buffering bursts its backlog, and
        // `seq <= last_seen` is what stops that backlog being written over the live position.
        Assert.Null(await index.RecordAsync(Samples.At(vehicleId, Samples.Kandy, seq: 9), default));
        Assert.Null(await index.RecordAsync(Samples.At(vehicleId, Samples.Kandy, seq: 10), default));

        // And the discarded sample changed nothing: the vehicle is still where seq 10 put it, not
        // 95 km inland.
        var position = await connection.GetDatabase().GeoPositionAsync(RedisKeys.GeoLive, vehicleId.ToString());
        Assert.Equal(Samples.ColomboFort.Latitude, position!.Value.Latitude, precision: 3);

        Assert.NotNull(await index.RecordAsync(Samples.At(vehicleId, Samples.Dehiwala, seq: 11), default));
    }

    [Fact]
    public async Task The_watermark_is_the_key_mqtt_topics_md_5_names()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();

        await NewIndex(connection).RecordAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 84_213), default);

        // Asserted against the literal pattern rather than the helper: C039 and C040 both read it.
        Assert.Equal(
            84_213,
            (long)await connection.GetDatabase().StringGetAsync($"veh:seq:{vehicleId}"));
        Assert.Equal($"veh:seq:{vehicleId}", RedisKeys.VehicleSeq(vehicleId));
    }

    [Fact]
    public async Task A_vehicle_that_moves_across_a_cell_boundary_writes_to_the_new_cell()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var index = NewIndex(connection);

        var fort = await index.RecordAsync(Samples.At(vehicleId, Samples.ColomboFort, seq: 1), default);
        var dehiwala = await index.RecordAsync(Samples.At(vehicleId, Samples.Dehiwala, seq: 2), default);

        Assert.NotEqual(fort, dehiwala);

        // geo:live is a move, not an append — the member is the vehicle id, so the old entry is
        // replaced rather than leaving the vehicle discoverable in two places at once.
        var position = await connection.GetDatabase().GeoPositionAsync(RedisKeys.GeoLive, vehicleId.ToString());
        Assert.Equal(Samples.Dehiwala.Latitude, position!.Value.Latitude, precision: 3);
    }

    [Fact]
    public async Task An_undecodable_payload_is_dropped_rather_than_retried_forever()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection, publishNormalized: false);

        var result = await processor.ProcessAsync("not a position"u8.ToArray(), Guid.NewGuid(), default);

        // One misbehaving handset must not stall the partition every other vehicle in its shard
        // shares. Counted by reason, dropped, moved on.
        Assert.Equal(PositionOutcome.Undecodable, result.Outcome);
    }

    [Fact]
    public async Task A_sample_outside_the_CHECK_domains_is_dropped_before_it_reaches_the_index()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var vehicleId = Guid.NewGuid();
        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection, publishNormalized: false);

        // `mqtt-topics.md` §2.1: "a cheap tracker reporting 0/999 degrees is a bug C039 must filter
        // before the batch arrives". 999 would fail ck_positions_lng at the sink; here it never gets
        // that far, and — importantly — it never reaches geo:live either.
        var wild = new PositionSample(vehicleId, DateTimeOffset.UtcNow, 1, 0, 999, PositionSource.Gt06);
        var encoded = PositionSampleCodec.Encode(wild);

        var result = await processor.ProcessAsync(encoded, vehicleId, default);

        Assert.Equal(PositionOutcome.Malformed, result.Outcome);
        Assert.Null(await connection.GetDatabase().GeoPositionAsync(RedisKeys.GeoLive, vehicleId.ToString()));
    }

    [Fact]
    public async Task The_topics_vehicle_wins_over_a_payload_that_claims_another()
    {
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        var authenticated = Guid.NewGuid();
        var claimed = Guid.NewGuid();

        await using var connection = await ConnectAsync();
        var processor = NewProcessor(connection, publishNormalized: false);

        // The topic is what EMQX bound to the device's credential; the payload is whatever the
        // device chose to write. Trusting the payload would undo the ACL — a handset could report
        // its neighbour's position from its own authenticated topic.
        var spoofed = PositionSampleCodec.Encode(Samples.At(claimed, Samples.ColomboFort, seq: 3));
        var result = await processor.ProcessAsync(spoofed, authenticated, default);

        Assert.Equal(PositionOutcome.Indexed, result.Outcome);
        Assert.Equal(authenticated, result.Sample!.VehicleId);

        var db = connection.GetDatabase();
        Assert.NotNull(await db.GeoPositionAsync(RedisKeys.GeoLive, authenticated.ToString()));
        Assert.Null(await db.GeoPositionAsync(RedisKeys.GeoLive, claimed.ToString()));
    }

    [Fact]
    public async Task An_indexed_sample_is_republished_onto_telemetry_normalized()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1, Processor = true });

        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();
        var sample = Samples.At(vehicleId, Samples.Dehiwala, seq: 21);

        await using (var device = await DeviceClient.ConnectAsync(emqx, vehicleId))
        {
            await device.PublishPositionAsync(sample);
        }

        var records = await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryNormalized, record => record.Key == vehicleId.ToString(), expected: 1);

        var normalized = PositionSampleCodec.Decode(Assert.Single(records).Value);

        Assert.Equal(sample.Seq, normalized.Seq);
        Assert.Equal(sample.Lat, normalized.Lat);

        // D6' §2.1 registers persistence-writer, trip-state and fleet-health as this topic's
        // consumers. None exists yet — C040 should find the data already there rather than have to
        // change this service to get it.
        Assert.NotNull(normalized.ReceivedTs);
        Assert.True(normalized.ReceivedTs >= sample.SampleTs);
    }

    private void RequireContainers()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }

    private Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);

    private static LivePositionIndex NewIndex(IConnectionMultiplexer connection) =>
        new(connection, Options.Create(new PositionProcessorOptions()), NullLogger<LivePositionIndex>.Instance);

    private static Processor NewProcessor(IConnectionMultiplexer connection, bool publishNormalized) =>
        new(
            NewIndex(connection),
            new UnusedPublisher(),
            Options.Create(new PositionProcessorOptions { PublishNormalized = publishNormalized }),
            TimeProvider.System,
            NullLogger<Processor>.Instance);

    /// <summary>
    /// Fails loudly if a test that turned <c>PublishNormalized</c> off ever reaches the producer.
    /// A no-op stub would let a regression in that flag pass unnoticed.
    /// </summary>
    private sealed class UnusedPublisher : IEventPublisher
    {
        public Task PublishAsync(EventMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This test disabled telemetry.normalized; nothing should publish.");

        public Task PublishAsync(
            IReadOnlyCollection<EventMessage> messages, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This test disabled telemetry.normalized; nothing should publish.");
    }
}

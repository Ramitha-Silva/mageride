using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// The bridge's ingest guarantees: E-08 exactly-once dispatch across replicas, the vehicleId
/// partition key, and per-vehicle ordering end to end.
/// </summary>
/// <remarks>
/// Every count here is taken off <c>telemetry.raw</c> rather than off the bridge's own counters. A
/// bridge that published each message twice would still report one forward per delivery, so its
/// counter cannot answer the question; the topic can.
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "MqttBridge")]
public sealed class MqttBridgeTests(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis)
{
    [Fact]
    public async Task A_published_position_reaches_telemetry_raw_keyed_by_its_vehicle()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1 });

        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();
        var sample = Samples.At(vehicleId, Samples.ColomboFort, seq: 41);

        await using (var device = await DeviceClient.ConnectAsync(emqx, vehicleId))
        {
            await device.PublishPositionAsync(sample);
        }

        var records = await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryRaw, record => record.Key == vehicleId.ToString(), expected: 1);

        var record = Assert.Single(records);

        // The key is the vehicleId from the *topic*, which is the half EMQX authenticated. Keying on
        // the payload would let a compromised handset write into another vehicle's partition.
        Assert.Equal(vehicleId.ToString(), record.Key);
        Assert.Equal(MqttTopics.PositionLive(vehicleId), record.Header(MqttBridgeWorker.TopicHeader));
        Assert.Equal(MqttBridgeWorker.LiveStream, record.Header(MqttBridgeWorker.StreamHeader));
        Assert.NotNull(record.Header(MqttBridgeWorker.ReceivedAtHeader));

        // The payload crosses untouched: the bridge decodes nothing, so what a downstream consumer
        // reads is exactly the device's own bytes.
        Assert.Equal(PositionSampleCodec.Encode(sample), record.Value);
        Assert.Equal(sample, PositionSampleCodec.Decode(record.Value));

        // ADD §7.3's "commit Redpanda offsets per partition": the acknowledgement to EMQX follows a
        // delivery report naming a partition and an offset, so by the time the record is readable
        // the bridge can say where it put it.
        var offsets = harness.Bridges[0].PartitionOffsets;
        Assert.Contains(record.Partition, offsets.Keys);
        Assert.True(offsets[record.Partition] >= 0);
    }

    [Fact]
    public async Task The_replay_stream_is_forwarded_and_labelled_separately()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1 });

        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();

        await using (var device = await DeviceClient.ConnectAsync(emqx, vehicleId))
        {
            await device.PublishAsync(
                MqttTopics.PositionReplay(vehicleId),
                PositionSampleCodec.Encode(Samples.At(vehicleId, Samples.Dehiwala, seq: 7)));
        }

        var records = await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryRaw, record => record.Key == vehicleId.ToString(), expected: 1);

        var record = Assert.Single(records);

        // R-09 splits live from replay so a fleet reconnecting after an outage cannot drown live
        // samples. The two arrive on one topic; the header is what keeps them tellable apart.
        Assert.Equal(MqttBridgeWorker.ReplayStream, record.Header(MqttBridgeWorker.StreamHeader));
    }

    /// <summary>The E-08 assertion.</summary>
    /// <remarks>
    /// A fleet, not one handset, because <c>emqx.conf</c> dispatches shared subscriptions
    /// <c>sticky</c>: one publishing session goes to one member of the group and stays there, which
    /// is what keeps a vehicle's samples in order (see
    /// <see cref="Per_vehicle_ordering_holds_across_replicas"/>). Load balancing is therefore per
    /// device — which is how a fleet actually distributes — and this is the shape that tests it.
    /// </remarks>
    [Fact]
    public async Task Two_replicas_share_the_subscription_with_no_duplicate_ingest()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 2 });

        await harness.WaitForBridgesAsync();

        const int vehicles = 16;
        const int perVehicle = 3;

        var fleet = Enumerable.Range(0, vehicles).Select(_ => Guid.NewGuid()).ToArray();

        await PublishLiveAsync(fleet, perVehicle);

        var keys = fleet.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.TelemetryRaw,
            record => keys.Contains(record.Key),
            expected: vehicles * perVehicle,
            timeout: TimeSpan.FromSeconds(60),
            // Long enough for a second copy to show up if the subscription were not shared. Without
            // the settle window the read would stop at the expected count and a duplicating bridge
            // would pass.
            settle: TimeSpan.FromSeconds(5));

        // The E-08 guarantee: EMQX dispatches each message to exactly one member of posGroup, so
        // `telemetry.raw` carries one copy however many replicas are running. Two replicas each
        // holding an ordinary `veh/+/pos/live` subscription would produce twice this.
        Assert.Equal(vehicles * perVehicle, records.Count);

        var forwarded = harness.Bridges.Select(bridge => bridge.Forwarded).ToArray();

        Assert.Equal(vehicles * perVehicle, forwarded.Sum());

        // And they genuinely shared it. The sticky pick is random per publishing session, so with 16
        // devices the chance of either replica taking none is 2 × 2^-16 — about one run in 32 000.
        Assert.All(forwarded, count => Assert.True(
            count > 0, $"Every replica should have taken a share; got [{string.Join(", ", forwarded)}]."));
    }

    /// <summary>
    /// DoD: "N replicas ingest each message exactly once (E-08) under a load test."
    /// </summary>
    [Fact]
    public async Task Three_replicas_under_load_ingest_each_message_exactly_once()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis,
            // Live only: the backlog stream has its own group and its own rate limit, and mixing
            // them here would measure the throttle rather than the dispatch.
            new HotPathHarnessOptions { BridgeReplicas = 3, ConsumeReplay = false });

        await harness.WaitForBridgesAsync();

        const int vehicles = 30;
        const int perVehicle = 8;
        const int published = vehicles * perVehicle;

        var fleet = Enumerable.Range(0, vehicles).Select(_ => Guid.NewGuid()).ToArray();

        await PublishLiveAsync(fleet, perVehicle);

        var keys = fleet.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.TelemetryRaw,
            record => keys.Contains(record.Key),
            expected: published,
            timeout: TimeSpan.FromSeconds(90),
            settle: TimeSpan.FromSeconds(5));

        Assert.Equal(published, records.Count);

        // Exactly once, stated as a set rather than as a count: a bridge that dropped one sample and
        // duplicated another would pass a count assertion and fail this one.
        var delivered = records
            .Select(record => (record.Key, PositionSampleCodec.Decode(record.Value).Seq))
            .ToList();

        Assert.Equal(published, delivered.Distinct().Count());

        var forwarded = harness.Bridges.Select(bridge => bridge.Forwarded).ToArray();

        Assert.Equal(published, forwarded.Sum());
        Assert.All(forwarded, count => Assert.True(
            count > 0, $"Every replica should have taken a share; got [{string.Join(", ", forwarded)}]."));
    }

    [Fact]
    public async Task Per_vehicle_ordering_holds_because_the_key_is_the_vehicle()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1 });

        await harness.WaitForBridgesAsync();

        const int published = 12;
        var vehicleId = Guid.NewGuid();

        await using (var device = await DeviceClient.ConnectAsync(emqx, vehicleId))
        {
            for (var seq = 1; seq <= published; seq++)
            {
                await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq));
            }
        }

        var records = await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryRaw, record => record.Key == vehicleId.ToString(), expected: published);

        Assert.Equal(published, records.Count);

        // D6' §2.1: the default partition key is vehicleId, and ordering is a per-partition
        // guarantee — so one vehicle's samples land in one partition and arrive in the order they
        // were sent. An unkeyed producer would round-robin them and the seq watermark downstream
        // would start discarding perfectly good positions.
        Assert.Single(records.Select(record => record.Partition).Distinct());
        Assert.Equal(
            Enumerable.Range(1, published).Select(seq => (long)seq),
            records.Select(record => PositionSampleCodec.Decode(record.Value).Seq));
    }

    /// <summary>
    /// DoD: "per-vehicle message order is preserved end to end" — with more than one replica, which
    /// is the case that can actually break it.
    /// </summary>
    /// <remarks>
    /// This is what <c>mqtt.shared_subscription_strategy = sticky</c> buys. Under EMQX 5.8's
    /// <c>round_robin</c> default the two replicas take a vehicle's samples alternately and race
    /// each other to the producer, so the partition holds them in whichever order two processes
    /// happened to win — the Redpanda key keeps a partition ordered, it cannot reorder what arrived
    /// scrambled. The bridge's own pipelining is ordered by construction (the produce is enqueued
    /// synchronously, in receive order, and an idempotent producer will not reorder a retry), so
    /// with sticky dispatch the guarantee holds all the way through.
    /// </remarks>
    [Fact]
    public async Task Per_vehicle_ordering_holds_across_replicas()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 2, ConsumeReplay = false });

        await harness.WaitForBridgesAsync();

        const int published = 30;
        var vehicleId = Guid.NewGuid();

        await using (var device = await DeviceClient.ConnectAsync(emqx, vehicleId))
        {
            for (var seq = 1; seq <= published; seq++)
            {
                await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq));
            }
        }

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.TelemetryRaw,
            record => record.Key == vehicleId.ToString(),
            expected: published,
            timeout: TimeSpan.FromSeconds(60),
            settle: TimeSpan.FromSeconds(3));

        Assert.Equal(published, records.Count);
        Assert.Equal(
            Enumerable.Range(1, published).Select(seq => (long)seq),
            records.Select(record => PositionSampleCodec.Decode(record.Value).Seq));
    }

    /// <summary>
    /// DoD: "graceful rebalance on replica loss with no duplicate ingest."
    /// </summary>
    /// <remarks>
    /// A replica is stopped while a fleet is publishing. Stopping is UNSUBSCRIBE, then drain, then
    /// DISCONNECT: EMQX stops routing the group's messages here, the forwards already started
    /// finish and acknowledge, and only then does the socket close. Skip the drain and every payload
    /// produced but not yet acknowledged comes back to the surviving replica and
    /// <c>telemetry.raw</c> carries it twice.
    /// </remarks>
    [Fact]
    public async Task A_replica_leaving_mid_flight_neither_duplicates_nor_drops()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 2, ConsumeReplay = false });

        await harness.WaitForBridgesAsync();

        const int vehicles = 16;
        const int perVehicle = 10;
        const int published = vehicles * perVehicle;

        var fleet = Enumerable.Range(0, vehicles).Select(_ => Guid.NewGuid()).ToArray();

        var publishing = PublishLiveAsync(fleet, perVehicle);

        // Mid-flight: each connection is paced by the broker at 5 msg/s, so ten samples take about
        // two seconds and this lands in the middle of them.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await harness.StopBridgeAsync(1);

        await publishing;

        var keys = fleet.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.TelemetryRaw,
            record => keys.Contains(record.Key),
            expected: published,
            timeout: TimeSpan.FromSeconds(90),
            settle: TimeSpan.FromSeconds(5));

        var delivered = records
            .Select(record => (record.Key, PositionSampleCodec.Decode(record.Value).Seq))
            .ToList();

        Assert.Equal(published, delivered.Distinct().Count());
        Assert.Equal(published, records.Count);
    }

    [Fact]
    public async Task A_second_vehicle_is_keyed_separately()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1 });

        await harness.WaitForBridgesAsync();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await using (var deviceA = await DeviceClient.ConnectAsync(emqx, first))
        await using (var deviceB = await DeviceClient.ConnectAsync(emqx, second))
        {
            await deviceA.PublishPositionAsync(Samples.At(first, Samples.ColomboFort));
            await deviceB.PublishPositionAsync(Samples.At(second, Samples.Kandy));
        }

        var keys = new[] { first.ToString(), second.ToString() };

        var records = await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryRaw, record => keys.Contains(record.Key), expected: 2);

        Assert.Equal(2, records.Count);
        Assert.Equal(
            keys.Order(StringComparer.Ordinal),
            records.Select(record => record.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every vehicle publishes <paramref name="perVehicle"/> live samples from its own connection,
    /// all at once.
    /// </summary>
    /// <remarks>
    /// One connection per vehicle, because <c>emqx.conf</c>'s <c>messages_rate = "5/s"</c> is a
    /// per-connection ceiling (D-17) — the fleet is what produces the load, not any one handset.
    /// </remarks>
    private async Task PublishLiveAsync(IReadOnlyList<Guid> fleet, int perVehicle)
    {
        await Task.WhenAll(fleet.Select(async vehicleId =>
        {
            await using var device = await DeviceClient.ConnectAsync(emqx, vehicleId);

            for (var seq = 1; seq <= perVehicle; seq++)
            {
                await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq));
            }
        }));
    }

    private void RequireContainers()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }
}

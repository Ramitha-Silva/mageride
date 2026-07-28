using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// DoD: "two mqtt-bridge replicas share the subscription with no duplicate ingest (E-08)."
/// </summary>
/// <remarks>
/// Every count here is taken off <c>telemetry.raw</c> rather than off the bridge's own counters. A
/// bridge that published each message twice would still report one forward per delivery, so its
/// counter cannot answer the question; the topic can.
/// </remarks>
[Collection<HotPathCollection>]
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
    [Fact]
    public async Task Two_replicas_share_the_subscription_with_no_duplicate_ingest()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 2 });

        await harness.WaitForBridgesAsync();

        const int published = 40;
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
            // Long enough for a second copy to show up if the subscription were not shared. Without
            // the settle window the read would stop at 40 and a duplicating bridge would pass.
            settle: TimeSpan.FromSeconds(5));

        // The E-08 guarantee: EMQX dispatches each message to exactly one member of posGroup, so
        // `telemetry.raw` carries one copy however many replicas are running. Two replicas each
        // holding an ordinary `veh/+/pos/live` subscription would produce 80.
        Assert.Equal(published, records.Count);

        var forwarded = harness.Bridges.Select(bridge => bridge.Forwarded).ToArray();

        Assert.Equal(published, forwarded.Sum());

        // And they genuinely shared it. EMQX's default strategy is random, so with 40 messages the
        // chance of either replica seeing none is about one in 5×10^11 — this is a real assertion,
        // not a coin toss.
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

    private void RequireContainers()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }
}

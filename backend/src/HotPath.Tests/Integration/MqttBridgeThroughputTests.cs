using System.Diagnostics;
using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Messaging;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// What one bridge replica actually carries, against ADD §7.6's 1,200 msg/s sustained.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test C129 found missing.</b> Its report §1.4 names the gap exactly: "the existing
/// suites drive one frame at a time … nothing before this component published faster than the
/// ceiling". <see cref="MqttBridgeTests"/> proves a sample arrives; <see cref="MqttBridgeRateTests"/>
/// proves the two rate limits behave. Neither asks how fast, so a chain carrying ~10 msg/s against a
/// 1,200 msg/s target passed every one of them.
/// </para>
/// <para>
/// <b>Why the offered load has to come from many connections.</b> The fixture mounts the deployed
/// <c>emqx.conf</c>, which sets <c>messages_rate = "5/s"</c> on every listener (D-17). That limiter
/// is per CONNECTION, so no single publisher can offer more than five samples a second however hard
/// it tries — measured at 4.9/s in <see cref="MqttBridgeRateTests"/>. The load here is therefore one
/// connection per vehicle, which is also what a fleet is.
/// </para>
/// <para>
/// <b>The measurement is the BRIDGE's, not the broker's.</b> Every publish is PUBACKed by EMQX
/// before it decides whether it can deliver — that is the whole reason C129's loss was invisible —
/// so a client-side timer measures nothing about the chain. The clock here stops when the records
/// are readable on <c>telemetry.raw</c>, which is the first point anything downstream could see them.
/// </para>
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "MqttBridge")]
public sealed class MqttBridgeThroughputTests(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis)
{
    /// <summary>Vehicles, one connection each. 24 × 5/s is ~120 msg/s offered — 12× the found ceiling.</summary>
    private const int Vehicles = 24;

    /// <summary>Samples per vehicle. 240 total: enough to time, short enough to run in a suite.</summary>
    private const int SamplesEach = 10;

    /// <summary>
    /// The floor this asserts, in messages a second, for ONE replica.
    /// </summary>
    /// <remarks>
    /// Not ADD §7.6's 1,200: that is the platform's sustained target across replicas and this is a
    /// container-per-fixture test on whatever CPU the runner gave it. 100 msg/s is chosen because it
    /// is an order of magnitude above the ~10 msg/s C129 measured and an order of magnitude below the
    /// rate a correct pipeline reaches, so it fails on the DEFECT and not on a slow machine. The real
    /// production figure is `load/`'s to measure against the replica.
    /// </remarks>
    private const double FloorPerSecond = 100;

    [Fact]
    public async Task One_replica_carries_far_more_than_the_ten_messages_a_second_C129_measured()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1 });

        await harness.WaitForBridgesAsync();

        var vehicles = Enumerable.Range(0, Vehicles).Select(_ => Guid.NewGuid()).ToArray();
        var devices = await Task.WhenAll(vehicles.Select(id => DeviceClient.ConnectAsync(emqx, id)));

        try
        {
            var started = Stopwatch.StartNew();

            // Every vehicle publishes concurrently. Each connection is paced by the broker's own
            // 5/s limiter, so the offered rate is the fleet's and no single socket is asked to do
            // something the deployed configuration forbids.
            await Task.WhenAll(devices.Select(async device =>
            {
                for (var seq = 1; seq <= SamplesEach; seq++)
                {
                    await device.PublishPositionAsync(
                        Samples.At(device.VehicleId, Samples.ColomboFort, seq: seq));
                }
            }));

            var offered = started.Elapsed;
            var expected = Vehicles * SamplesEach;
            var keys = vehicles.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
            var bridge = Assert.Single(harness.Bridges);

            // Timed against the BRIDGE's own counter, not against a consumer's. `TopicReader` waits
            // a two-second settle after the expected count arrives — deliberately, so a duplicate
            // cannot hide behind an early stop — and that settle is a property of the test, not of
            // the platform. Timing it made a 300 msg/s bridge read as 87.
            while (started.Elapsed < TimeSpan.FromSeconds(90) && bridge.ForwardedLive < expected)
            {
                await Task.Delay(20);
            }

            var elapsed = started.Elapsed;
            var carried = bridge.ForwardedLive / elapsed.TotalSeconds;

            // Correctness, untimed: what the bridge counted is what the topic actually carries.
            var records = await TopicReader.ReadAsync(
                redpanda,
                EventTopics.TelemetryRaw,
                record => keys.Contains(record.Key),
                expected,
                // 90 s is deliberately generous: at C129's measured ceiling these 240 samples take
                // 24 s, so a timeout would report "the bridge is slow" as "the bridge is broken" and
                // the failure message below would never be printed.
                timeout: TimeSpan.FromSeconds(90));

            // The offered rate first: if the publishers could not push past the floor, the run says
            // nothing about the bridge and the number below would be the broker's limiter, not a
            // ceiling.
            var offeredRate = expected / offered.TotalSeconds;
            Assert.True(
                offeredRate > FloorPerSecond,
                $"the publishers only offered {offeredRate:F1} msg/s — this run cannot measure a "
                + $"ceiling above that. {Vehicles} connections × {SamplesEach} samples in {offered.TotalSeconds:F1} s.");

            Assert.Equal(expected, records.Count);

            Assert.True(
                carried > FloorPerSecond,
                $"one bridge replica carried {carried:F1} msg/s ({records.Count} samples in "
                + $"{elapsed.TotalSeconds:F1} s) against a floor of {FloorPerSecond} and ADD §7.6's "
                + "1,200 msg/s sustained target. C129 measured ~10 msg/s here and named the ack path "
                + "inside TelemetryForwarder.CompleteAsync; see load/report.md §1.");
        }
        finally
        {
            await Task.WhenAll(devices.Select(async device => await device.DisposeAsync()));
        }
    }

    /// <summary>
    /// Nothing may be silently dropped on the way, whatever the rate.
    /// </summary>
    /// <remarks>
    /// The count assertion above would pass if the bridge forwarded a sample twice and lost another.
    /// This one reads the forwarder's own counters, which is the platform's view rather than the
    /// topic's: C129's central finding is exactly a case where the two disagreed and only the
    /// broker's counters knew.
    /// </remarks>
    [Fact]
    public async Task Every_sample_offered_is_forwarded_exactly_once()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis, new HotPathHarnessOptions { BridgeReplicas = 1 });

        await harness.WaitForBridgesAsync();

        var vehicles = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var devices = await Task.WhenAll(vehicles.Select(id => DeviceClient.ConnectAsync(emqx, id)));
        const int each = 10;

        try
        {
            await Task.WhenAll(devices.Select(async device =>
            {
                for (var seq = 1; seq <= each; seq++)
                {
                    await device.PublishPositionAsync(
                        Samples.At(device.VehicleId, Samples.ColomboFort, seq: seq));
                }
            }));

            var expected = vehicles.Length * each;
            var keys = vehicles.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);

            var records = await TopicReader.ReadAsync(
                redpanda, EventTopics.TelemetryRaw, record => keys.Contains(record.Key),
                expected, timeout: TimeSpan.FromSeconds(90));

            Assert.Equal(expected, records.Count);

            // One record per (vehicle, seq): no duplicates, nothing missing.
            var pairs = records
                .Select(record => (record.Key, Seq: PositionSampleCodec.Decode(record.Value).Seq))
                .ToList();

            Assert.Equal(expected, pairs.Distinct().Count());

            var bridge = Assert.Single(harness.Bridges);
            Assert.Equal(expected, bridge.ForwardedLive);
        }
        finally
        {
            await Task.WhenAll(devices.Select(async device => await device.DisposeAsync()));
        }
    }

    private void RequireContainers()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }
}

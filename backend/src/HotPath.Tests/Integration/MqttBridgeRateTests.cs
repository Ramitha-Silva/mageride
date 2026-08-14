using System.Diagnostics;
using System.Text.Json;
using MageRide.HotPath.MqttBridge.Bridging;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.HotPath.Tests.Infrastructure;
using MageRide.Shared.Http;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.RateLimiting;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;

namespace MageRide.HotPath.Tests.Integration;

/// <summary>
/// The two limits C038 owns: T-05's 20 samples/s/device on the backlog stream and D-17's 5 msg/s
/// per-vehicle ceiling on the live one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The deployed broker paces a device connection at exactly 5 msg/s</b> — <c>emqx.conf</c> sets
/// <c>messages_rate = "5/s"</c> on the listeners devices reach (8883 trackers, 8084 mobile) and the
/// fixture mounts that file, so a handset cannot push 20 samples/s through one socket however hard
/// it tries. Two consequences run through this class: a device that exceeds D-17's
/// <i>per-vehicle</i> ceiling has to do it over several connections (which is exactly the gap the
/// bridge-side counter exists to close, since the broker's limiter is per connection), and a test of
/// the throttle <i>mechanism</i> has to configure a rate the broker will let a device beat.
/// <see cref="RateLimitPolicies.MqttReplay"/> carries the spec'd 20/s and
/// <see cref="The_spec_default_for_the_backlog_rate_is_twenty_a_second"/> pins it.
/// </para>
/// <para>
/// <b>The fixture's own listener is 1883, and that one no longer carries the limit.</b> It was
/// removed there because 1883 is the in-cluster listener — no device reaches it, and what does is
/// mqtt-bridge-svc holding the whole fleet's shared subscription, so a per-connection message limit
/// on it was a per-FLEET limit on ingest and is what capped the platform at ~10 msg/s (C129 §1;
/// <c>emqx.conf</c> has the measurements). Nothing in this class depended on that pacing — the
/// several-connections shape above is what the tests already did, for the per-vehicle reason — but
/// a test written here in future cannot assume the broker will slow its publisher down.
/// </para>
/// </remarks>
[Collection<HotPathCollection>]
[Trait("Category", "MqttBridge")]
public sealed class MqttBridgeRateTests(EmqxFixture emqx, RedpandaFixture redpanda, RedisFixture redis)
{
    /// <summary>What the specs say, kept out of the tests that have to configure something else.</summary>
    [Fact]
    public void The_spec_default_for_the_backlog_rate_is_twenty_a_second()
    {
        // T-05 / ADD §7.5.2: "max 20 backlog samples/s/vehicle on veh/{vehicleId}/pos/replay".
        Assert.Equal(20, new MqttBridgeOptions().ReplaySamplesPerSecond);
        Assert.Equal(20, RateLimitPolicies.MqttReplay.Capacity);
        Assert.Equal(20, RateLimitPolicies.MqttReplay.RefillRatePerSecond);

        // D-17 / ADD §7.5.2: "more than 5 messages per second is rate-limited ... and a
        // mqtt.rate_violation event emitted to audit.events".
        Assert.Equal(5, new MqttBridgeOptions().PublishCeilingPerSecond);

        // No burst credit. A tracker that has been offline for an hour is the case T-05 exists for,
        // and letting it spend an hour of accumulated tokens at once is the reconnect storm.
        Assert.Equal(RateLimitPolicies.MqttReplay.Capacity, (int)RateLimitPolicies.MqttReplay.RefillRatePerSecond);
    }

    /// <summary>
    /// DoD: "a replay flood is rate-limited". The backlog arrives faster than the limit and comes
    /// out of the bridge paced, whole.
    /// </summary>
    [Fact]
    public async Task The_backlog_stream_is_held_to_its_per_device_rate()
    {
        RequireContainers();

        const int rate = 4;
        const int published = 24;

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis,
            new HotPathHarnessOptions { BridgeReplicas = 1, ReplaySamplesPerSecond = rate });

        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();
        var started = Stopwatch.StartNew();

        // Four connections, because one is paced by the broker at 5 msg/s and the point is to hand
        // the bridge a backlog arriving faster than it may forward it.
        await PublishReplayAsync(vehicleId, connections: 4, perConnection: published / 4);

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.TelemetryRaw,
            record => record.Key == vehicleId.ToString(),
            expected: published,
            timeout: TimeSpan.FromSeconds(60),
            settle: TimeSpan.FromSeconds(3));

        var elapsed = started.Elapsed;

        // Nothing is lost. A throttle that dropped the overflow would be a silently lossy backlog,
        // which is the one thing a backlog stream must not be — it waits instead, and the wait
        // reaches EMQX as unacknowledged QoS 1 messages filling the session's inflight window.
        Assert.Equal(published, records.Count);
        Assert.All(records, record => Assert.Equal(
            MqttBridgeWorker.ReplayStream, record.Header(MqttBridgeWorker.StreamHeader)));

        // The bucket starts full, so `rate` go straight through and the remaining
        // (published - rate) are paced at `rate` per second.
        var floor = TimeSpan.FromSeconds((double)(published - rate) / rate);

        Assert.True(
            elapsed >= floor * 0.8,
            $"A {published}-sample backlog at {rate}/s should take about {floor.TotalSeconds:0.0} s; took {elapsed.TotalSeconds:0.0} s.");

        // And it was this bridge that paced it, not the broker or the network.
        Assert.True(harness.Bridges[0].ReplayThrottled > 0, "The T-05 bucket never made a sample wait.");
    }

    /// <summary>
    /// DoD: "a replay flood is rate-limited <b>without measurable added latency on live samples</b>."
    /// </summary>
    /// <remarks>
    /// The two streams hold separate broker sessions on purpose. One session with both filters would
    /// share an inflight window — 32 unacknowledged backlog samples, each waiting on a T-05 token,
    /// and EMQX stops delivering live positions on the same socket until the wait clears. That is
    /// the failure R-09 is written against, and it is what this measures.
    /// </remarks>
    [Fact]
    public async Task A_backlog_flood_does_not_delay_live_samples()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis,
            // Two a second: a backlog this throttled is still queued long after the live samples
            // have been and gone, which is what makes the comparison mean anything.
            new HotPathHarnessOptions { BridgeReplicas = 1, ReplaySamplesPerSecond = 2 });

        await harness.WaitForBridgesAsync();

        var flooding = Guid.NewGuid();
        var live = Guid.NewGuid();

        const int floodSize = 80;
        const int liveSamples = 10;

        var flood = PublishReplayAsync(flooding, connections: 4, perConnection: floodSize / 4);

        // Let the flood get ahead, so the live samples are published into a bridge that is already
        // holding a queue.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var publishedAt = new Dictionary<long, DateTimeOffset>();

        await using (var device = await DeviceClient.ConnectAsync(emqx, live))
        {
            for (var seq = 1; seq <= liveSamples; seq++)
            {
                publishedAt[seq] = DateTimeOffset.UtcNow;
                await device.PublishPositionAsync(Samples.At(live, Samples.ColomboFort, seq));
                await Task.Delay(150);
            }
        }

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.TelemetryRaw,
            record => record.Key == live.ToString(),
            expected: liveSamples,
            timeout: TimeSpan.FromSeconds(30),
            settle: TimeSpan.FromSeconds(1));

        // Read before awaiting the flood: the claim is that live went through *while* the backlog
        // was still queued, and a drained backlog would prove nothing.
        var forwardedReplay = harness.Bridges[0].ForwardedReplay;

        Assert.Equal(liveSamples, records.Count);

        foreach (var record in records)
        {
            var seq = PositionSampleCodec.Decode(record.Value).Seq;
            var receivedAt = DateTimeOffset.Parse(
                record.Header(MqttBridgeWorker.ReceivedAtHeader)!, System.Globalization.CultureInfo.InvariantCulture);

            var latency = receivedAt - publishedAt[seq];

            // The bridge's own clock against the test's, in the same process. ADD §13.3 budgets
            // p95 < 5 s for the whole device-to-passenger path; the broker-to-bridge hop inside it
            // is milliseconds, and anything approaching a second here means the backlog is in the
            // way.
            Assert.True(
                latency < TimeSpan.FromSeconds(1),
                $"Live sample {seq} took {latency.TotalMilliseconds:0} ms to reach the bridge under a backlog flood.");
        }

        Assert.True(
            forwardedReplay < floodSize,
            $"The backlog had already drained ({forwardedReplay}/{floodSize}); this measured nothing.");

        await flood;
    }

    /// <summary>
    /// DoD: "a rate_violation event is emitted for a client publishing above 5 msg/s."
    /// </summary>
    /// <remarks>
    /// Four connections under one vehicle credential. The broker's <c>messages_rate</c> limiter is
    /// per connection and lets each of them through at 5 msg/s; D-17's ceiling is per
    /// <c>vehicleId</c>, and the aggregate is four times over it. This is the case the bridge-side
    /// counter exists for — <c>acl.conf</c> confines every one of those sessions to the same
    /// vehicle's topics, so the broker sees four compliant clients and the platform sees one vehicle
    /// publishing at 20 msg/s.
    /// </remarks>
    [Fact]
    public async Task A_vehicle_over_the_ceiling_raises_a_rate_violation_on_audit_events()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis,
            new HotPathHarnessOptions
            {
                BridgeReplicas = 1,
                ConsumeReplay = false,
                MonitorPublishRate = true,
                RateFlushInterval = TimeSpan.FromMilliseconds(250),
            });

        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();

        await PublishLiveAsync(vehicleId, connections: 4, perConnection: 6);

        var records = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.AuditEvents,
            record => record.Key == vehicleId.ToString(),
            expected: 1,
            timeout: TimeSpan.FromSeconds(45),
            // Long enough for a second report to show up. The vehicle is over the ceiling for
            // several consecutive seconds and the cooldown is what keeps that to one event.
            settle: TimeSpan.FromSeconds(6));

        var record = Assert.Single(records);

        Assert.Equal(AuditEvent.MqttRateViolation, record.Header("action"));

        var audit = JsonSerializer.Deserialize<AuditEvent>(record.Value, MageRideJson.Options)!;

        Assert.Equal(AuditEvent.MqttRateViolation, audit.Action);
        Assert.Equal(AuditEvent.VehicleEntity, audit.EntityType);
        Assert.Equal(vehicleId.ToString(), audit.EntityId);

        // The device is the actor. There is no user behind a position publish.
        Assert.Equal(vehicleId.ToString(), audit.ActorId);

        // An observation, not a state change: D-17 records what a vehicle did, and the broker is
        // what stops it.
        Assert.Null(audit.Before);

        var after = audit.After!.Value;

        Assert.Equal(5, after.GetProperty("ceilingPerSecond").GetInt32());
        Assert.True(after.GetProperty("observedPerSecond").GetInt64() > 5);
        Assert.Equal(MqttTopics.AllPositionsLive, after.GetProperty("topic").GetString());

        // The samples themselves are still forwarded. Enforcement is the broker's; a position
        // dropped here would be one anti-spoof never gets to look at.
        var forwarded = await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryRaw, r => r.Key == vehicleId.ToString(), expected: 24);

        Assert.Equal(24, forwarded.Count);
    }

    /// <summary>A vehicle inside the ceiling is not reported. The monitor must not cry wolf.</summary>
    [Fact]
    public async Task A_vehicle_within_the_ceiling_raises_nothing()
    {
        RequireContainers();

        await using var harness = await HotPathHarness.StartAsync(
            emqx, redpanda, redis,
            new HotPathHarnessOptions
            {
                BridgeReplicas = 1,
                ConsumeReplay = false,
                MonitorPublishRate = true,
                RateFlushInterval = TimeSpan.FromMilliseconds(250),
            });

        await harness.WaitForBridgesAsync();

        var vehicleId = Guid.NewGuid();

        // Three a second, comfortably inside D-17's five — the 1 s near-geofence cadence the ceiling
        // was raised to accommodate (ADD §7.5.2).
        await using (var device = await DeviceClient.ConnectAsync(emqx, vehicleId))
        {
            for (var seq = 1; seq <= 9; seq++)
            {
                await device.PublishPositionAsync(Samples.At(vehicleId, Samples.ColomboFort, seq));
                await Task.Delay(330);
            }
        }

        await TopicReader.ReadAsync(
            redpanda, EventTopics.TelemetryRaw, record => record.Key == vehicleId.ToString(), expected: 9);

        var violations = await TopicReader.ReadAsync(
            redpanda,
            EventTopics.AuditEvents,
            record => record.Key == vehicleId.ToString(),
            // Nothing is expected, so the read runs its settle window out from the first poll.
            expected: 0,
            timeout: TimeSpan.FromSeconds(10),
            settle: TimeSpan.FromSeconds(5));

        Assert.Empty(violations);
        Assert.Equal(0, harness.Bridges[0].ForwardedReplay);
    }

    private async Task PublishLiveAsync(Guid vehicleId, int connections, int perConnection) =>
        await PublishAsync(vehicleId, connections, perConnection, MqttTopics.PositionLive(vehicleId));

    private async Task PublishReplayAsync(Guid vehicleId, int connections, int perConnection) =>
        await PublishAsync(vehicleId, connections, perConnection, MqttTopics.PositionReplay(vehicleId));

    private async Task PublishAsync(Guid vehicleId, int connections, int perConnection, string topic)
    {
        var devices = new List<DeviceClient>();

        try
        {
            for (var i = 0; i < connections; i++)
            {
                devices.Add(await DeviceClient.ConnectAsync(emqx, vehicleId));
            }

            await Task.WhenAll(devices.Select(async (device, index) =>
            {
                for (var i = 0; i < perConnection; i++)
                {
                    var seq = (index * perConnection) + i + 1;
                    await device.PublishAsync(
                        topic, PositionSampleCodec.Encode(Samples.At(vehicleId, Samples.ColomboFort, seq)));
                }
            }));
        }
        finally
        {
            foreach (var device in devices)
            {
                await device.DisposeAsync();
            }
        }
    }

    private void RequireContainers()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
    }
}

using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using MageRide.FleetHealth.Endpoints;
using MageRide.FleetHealth.Ingest;
using MageRide.FleetHealth.Tests.Infrastructure;
using MageRide.Shared.Messaging;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.FleetHealth.Tests.Integration;

/// <summary>
/// The four inputs to the health plane, each driven through the real transport it arrives on.
/// </summary>
/// <remarks>
/// <para>
/// The deliverable is "a consumer aggregating <c>veh/{vehicleId}/status</c> + tracker diagnostics", and
/// every one of these has a failure mode that only the real transport exposes: a Kafka payload the codec
/// cannot read, an event name that does not match the producer's, and — the one that matters most — a
/// refused MQTT subscription, whose symptom is silence rather than an error.
/// </para>
/// <para>
/// <b>Every wait here is on an observable effect, never on a counter.</b> The Redpanda and EMQX
/// containers are shared across the collection and each harness joins with a fresh consumer group, so a
/// consumer starting from the earliest offset replays every record earlier tests produced — and EMQX
/// redelivers every retained <c>veh/+/status</c> message on subscribe. A counter reaching one therefore
/// says "something arrived", not "this test's message arrived".
/// </para>
/// </remarks>
[Collection<FleetHealthCollection>]
public sealed class IngestTests(PostgresFixture postgres, RedpandaFixture redpanda, EmqxFixture emqx)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_normalized_sample_advances_the_devices_ping_clock()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();

        // Silent for 40 minutes, so it starts Offline and any advance is visible.
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now.AddMinutes(-40));

        Assert.Equal("offline", await StateOfAsync(harness, fleet, tracker.VehicleId));

        await ProduceSampleAsync(
            harness, Samples.Position(tracker.VehicleId, fleet.FleetId, now, seq: 42, satCount: 9));

        await DrainPingsAsync(harness, tracker.VehicleId, now);

        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));

        // The satellite count is the one US-3.12 diagnostic a position sample carries, so it is taken
        // from here as well as from sys/diag.
        var device = await DeviceOfAsync(harness, fleet, tracker.VehicleId);

        Assert.Equal(9, device.Sats);
        Assert.Equal(now, device.LastSeen);
    }

    [Fact]
    public async Task A_replayed_sample_cannot_move_a_devices_clock_backwards()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now);

        // A T-05 backlog burst: the device has been out of coverage and is now sending its history. Each
        // sample carries the GNSS instant it was captured with, so an hour-old fix must not make a live
        // device look Offline. The upsert's GREATEST is what makes that true rather than the arrival
        // order — which is the property that lets the consumer commit its offsets after the flush and
        // nothing else.
        var replayed = Samples.Position(tracker.VehicleId, fleet.FleetId, now.AddHours(-1), seq: 1) with
        {
            ReceivedTs = now.AddHours(-1),
        };

        await ProduceSampleAsync(harness, replayed);

        // The write is observable as the sample's satellite count arriving on a row that had none, so the
        // wait does not depend on a ping clock that must deliberately not move.
        var row = await DrainPingsAsync(
            harness,
            tracker.VehicleId,
            until: read => read.SatCount == 11,
            what: "the replayed sample");

        Assert.Equal(now, row.LastPingAt);
        Assert.Equal(now, row.LastSampleTs);
        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));
    }

    [Fact]
    public async Task An_unreadable_payload_advances_the_offset_and_nothing_else()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(postgres, redpanda, emqx);

        var fleet = await harness.CreateFleetAsync();
        var now = harness.Clock.GetUtcNow();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: now.AddMinutes(-40));

        await ProduceRawAsync(harness, tracker.VehicleId, Encoding.UTF8.GetBytes("this is not a position"));
        await ProduceSampleAsync(harness, Samples.Position(tracker.VehicleId, fleet.FleetId, now));

        // position-processor already dropped the undecodable before republishing (C039), so anything
        // unreadable here is a producer that has changed shape — and redelivering it produces the same
        // nothing for ever. The good sample behind it still lands, which is what says the offset moved
        // past the bad one instead of stalling the partition on it.
        await DrainPingsAsync(harness, tracker.VehicleId, now);

        Assert.Equal("online", await StateOfAsync(harness, fleet, tracker.VehicleId));
    }

    [Fact]
    public async Task A_bind_puts_a_tracker_on_its_fleets_dashboard_and_a_revoke_decommissions_it()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(
            postgres,
            redpanda,
            emqx,
            new Dictionary<string, string?> { ["Health:ProvisioningConsumerEnabled"] = "true" });

        var fleet = await harness.CreateFleetAsync();

        // A vehicle and a binding, but nothing in device_health: this is the path that puts it there.
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, inDeviceHealth: false);

        var before = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);
        Assert.Equal(0, before.Counts.Total);

        await ProduceBindingEventAsync(
            harness,
            ProvisioningEventConsumer.TrackerBound,
            tracker.VehicleId,
            new { imei = tracker.Imei, vehicleId = tracker.VehicleId, fleetId = fleet.FleetId });

        // No ping yet, so it arrives Offline rather than Online — a bound device that has never reported
        // is exactly what an operator opens this screen to find.
        var bound = await WaitForDeviceAsync(
            harness, fleet, tracker.VehicleId, device => device.State == "offline", "tracker.bound");

        Assert.Equal(tracker.Imei, bound.Imei);

        await ProduceBindingEventAsync(
            harness,
            ProvisioningEventConsumer.TrackerRevoked,
            tracker.VehicleId,
            new
            {
                imei = tracker.Imei,
                vehicleId = tracker.VehicleId,
                revokedAt = harness.Clock.GetUtcNow(),
                reason = "decommissioned",
            });

        await WaitForDeviceAsync(
            harness, fleet, tracker.VehicleId, device => device.State == "decommissioned", "tracker.revoked");

        var revoked = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);

        // US-3.8, and the fleet still sees it: a decommissioned tracker belongs to the fleet whose
        // dashboard has to show it in that state, which is why the revoke does not blank fleet_id.
        Assert.Equal(1, revoked.Counts.Total);
        Assert.Equal(1, revoked.Counts.Decommissioned);
    }

    [Fact]
    public async Task A_quarantine_is_not_a_decommission()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(
            postgres,
            redpanda,
            emqx,
            new Dictionary<string, string?> { ["Health:ProvisioningConsumerEnabled"] = "true" });

        var fleet = await harness.CreateFleetAsync();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: harness.Clock.GetUtcNow());

        await ProduceBindingEventAsync(
            harness,
            ProvisioningEventConsumer.TrackerQuarantined,
            tracker.VehicleId,
            new { imei = tracker.Imei, vehicleId = tracker.VehicleId, detail = "two serials in 24 h" });

        await WaitForBindingStateAsync(harness, tracker.VehicleId, "QUARANTINED");

        var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);

        // T-08 holds a binding pending the US-3.4 admin decision and it may come back, so it is not
        // retired. It is still publishing, so it is online.
        Assert.Equal(0, rollup.Counts.Decommissioned);
        Assert.Equal("online", Assert.Single(rollup.Items).State);
    }

    [Fact]
    public async Task A_last_will_and_a_diagnostics_frame_arrive_over_the_real_broker()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redpanda.IsAvailable, redpanda.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        await using var harness = await FleetHealthHarness.StartAsync(
            postgres,
            redpanda,
            emqx,
            new Dictionary<string, string?> { ["Health:DevicePlaneEnabled"] = "true" });

        var fleet = await harness.CreateFleetAsync();
        var tracker = await harness.CreateTrackerAsync(fleet.FleetId, lastPingAt: harness.Clock.GetUtcNow());

        await WaitForAsync(
            () => harness.DevicePlane.IsSubscribed,
            "the device-plane subscription",
            () => harness.DevicePlane.LastError?.ToString() ?? "The worker recorded no error at all, "
                + "which means it never ran: check that the DevicePlaneEnabled hosted service was registered.");

        // Published as the vehicle itself, so the acl.conf rule that actually authorises this
        // (`veh/${username}/status`, `sys/diag/${username}`) is the one under test.
        await using var device = await DeviceClient.ConnectAsync(harness, tracker.VehicleId);

        await device.PublishAsync(
            MqttTopics.Diagnostics(tracker.VehicleId),
            """{"signalStrength":24,"batteryPct":82,"batteryMv":4020,"satCount":11}""",
            retain: false);

        var withDiagnostics = await WaitForDeviceAsync(
            harness, fleet, tracker.VehicleId, d => d.BatteryMv == 4020, "the diagnostics frame");

        Assert.Equal(24, withDiagnostics.SignalStrength);
        Assert.Equal(82, withDiagnostics.Battery);
        Assert.Equal(11, withDiagnostics.Sats);

        // The will arrives after the ping, as it does in production — the ladder's tie-break is strict
        // (`last_status_at > last_ping_at`), because a ping at the very same instant is positive evidence
        // the device is alive and a will at that instant is not.
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        // R-15/T-04: retained, exactly as EMQX's last will and the tcp-adapter's half-close emulation
        // publish it. The device was pinged a second ago, so this proves the will alone takes it out of
        // Online.
        await device.PublishAsync(MqttTopics.Status(tracker.VehicleId), VehicleStatus.Offline, retain: true);

        await WaitForDeviceAsync(
            harness, fleet, tracker.VehicleId, d => d.State == "stale", "the last will");
    }

    // -----------------------------------------------------------------------------------------
    // Producing
    // -----------------------------------------------------------------------------------------

    private static Task ProduceSampleAsync(FleetHealthHarness harness, PositionSample sample) =>
        ProduceRawAsync(harness, sample.VehicleId, PositionSampleCodec.Encode(sample));

    private static async Task ProduceRawAsync(FleetHealthHarness harness, Guid vehicleId, byte[] payload)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = harness.Redpanda.BootstrapServers }).Build();

        await producer.ProduceAsync(
            EventTopics.TelemetryNormalized,
            new Message<string, byte[]> { Key = vehicleId.ToString(), Value = payload });

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static async Task ProduceBindingEventAsync(
        FleetHealthHarness harness, string eventType, Guid vehicleId, object payload)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = harness.Redpanda.BootstrapServers }).Build();

        var message = new Message<string, byte[]>
        {
            Key = vehicleId.ToString(),
            Value = JsonSerializer.SerializeToUtf8Bytes(payload, MageRide.Shared.Http.MageRideJson.StorageOptions),

            // The header the kernel's outbox dispatcher stamps — the same shape a real
            // provisioning.events record has (OutboxDispatcher.ToMessage).
            Headers = [new Header("eventType", Encoding.UTF8.GetBytes(eventType))],
        };

        await producer.ProduceAsync(EventTopics.ProvisioningEvents, message);

        producer.Flush(TimeSpan.FromSeconds(10));
    }

    // -----------------------------------------------------------------------------------------
    // Waiting
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Drives the ping consumer's own consume-and-flush cycle until <paramref name="vehicleId"/>'s clock
    /// has reached <paramref name="expected"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop is driven rather than hosted, for the reason the harness gives: a consumer ticking
    /// underneath an assertion makes "the sample advanced the clock" indistinguishable from "something
    /// advanced it".
    /// </para>
    /// <para>
    /// The exit condition is this vehicle's row and not the consumer's counter. Redpanda is shared across
    /// the collection and every harness joins with a fresh group reading from the earliest offset, so the
    /// first record this consumer sees is almost always one an earlier test produced — and a counter
    /// reaching one would let the loop exit before this test's sample had even been read.
    /// </para>
    /// </remarks>
    private static Task<HealthRowView> DrainPingsAsync(
        FleetHealthHarness harness, Guid vehicleId, DateTimeOffset expected) =>
        DrainPingsAsync(
            harness,
            vehicleId,
            until: read => read.LastPingAt >= expected,
            what: $"vehicle {vehicleId}'s ping clock to reach {expected:o}");

    private static async Task<HealthRowView> DrainPingsAsync(
        FleetHealthHarness harness, Guid vehicleId, Func<HealthRowView, bool> until, string what)
    {
        var consumer = harness.PingConsumer;
        using var client = consumer.BuildConsumer();

        client.Subscribe(EventTopics.TelemetryNormalized);

        try
        {
            var deadline = DateTime.UtcNow + Patience;

            while (DateTime.UtcNow < deadline)
            {
                await consumer.DrainOnceAsync(client, CancellationToken.None);

                var read = await ReadHealthRowAsync(harness, vehicleId);

                if (read is not null && until(read))
                {
                    return read;
                }
            }

            Assert.Fail(
                $"Timed out after {Patience.TotalSeconds:F0} s waiting for {what}; the row was " +
                $"{await ReadHealthRowAsync(harness, vehicleId)}.");

            return null!;
        }
        finally
        {
            client.Close();
        }
    }

    private static async Task<HealthRowView?> ReadHealthRowAsync(FleetHealthHarness harness, Guid vehicleId)
    {
        await using var connection = await harness.OpenAsync();

        return await connection.QuerySingleOrDefaultAsync<HealthRowView>(
            """
            SELECT last_ping_at AS LastPingAt, last_sample_ts AS LastSampleTs, sat_count AS SatCount
              FROM telemetry.device_health WHERE vehicle_id = @VehicleId;
            """,
            new { VehicleId = vehicleId });
    }

    /// <summary>Waits for the fleet rollup to show one device satisfying <paramref name="predicate"/>.</summary>
    private static async Task<TrackerHealthResponse> WaitForDeviceAsync(
        FleetHealthHarness harness,
        SeededFleet fleet,
        Guid vehicleId,
        Func<TrackerHealthResponse, bool> predicate,
        string what)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);
            var device = rollup.Items.FirstOrDefault(item => item.VehicleId == vehicleId);

            if (device is not null && predicate(device))
            {
                return device;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out after {Patience.TotalSeconds:F0} s waiting for {what}.");
        return null!;
    }

    /// <summary>Waits for the mirrored binding state, which is not on the response.</summary>
    private static async Task WaitForBindingStateAsync(FleetHealthHarness harness, Guid vehicleId, string state)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            await using (var connection = await harness.OpenAsync())
            {
                var current = await connection.ExecuteScalarAsync<string?>(
                    "SELECT binding_state FROM telemetry.device_health WHERE vehicle_id = @VehicleId;",
                    new { VehicleId = vehicleId });

                if (current == state)
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out after {Patience.TotalSeconds:F0} s waiting for binding_state = {state}.");
    }

    /// <param name="diagnose">
    /// Optional. Called only on timeout, and its answer goes in the failure message — because
    /// "timed out waiting for the device-plane subscription" on its own is not a diagnosis, and the
    /// worker's own log is captured by xUnit and reaches nobody.
    /// </param>
    private static async Task WaitForAsync(Func<bool> condition, string what, Func<string?>? diagnose = null)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        if (diagnose?.Invoke() is { Length: > 0 } detail)
        {
            Assert.Fail($"Timed out after {Patience.TotalSeconds:F0} s waiting for {what}. {detail}");
        }

        Assert.Fail($"Timed out after {Patience.TotalSeconds:F0} s waiting for {what}.");
    }

    private static async Task<string> StateOfAsync(FleetHealthHarness harness, SeededFleet fleet, Guid vehicleId) =>
        (await DeviceOfAsync(harness, fleet, vehicleId)).State;

    private static async Task<TrackerHealthResponse> DeviceOfAsync(
        FleetHealthHarness harness, SeededFleet fleet, Guid vehicleId)
    {
        var rollup = await harness.ReadHealthAsync(fleet.FleetId, fleet.Bearer);

        return Assert.Single(rollup.Items, item => item.VehicleId == vehicleId);
    }

    /// <summary>The <c>telemetry.device_health</c> columns the ingest waits watch.</summary>
    private sealed record HealthRowView(DateTimeOffset? LastPingAt, DateTimeOffset? LastSampleTs, short? SatCount);

    /// <summary>A real MQTT client presenting a real session JWT for one vehicle.</summary>
    private sealed class DeviceClient : IAsyncDisposable
    {
        private readonly IMqttClient _client;

        private DeviceClient(IMqttClient client) => _client = client;

        public static async Task<DeviceClient> ConnectAsync(FleetHealthHarness harness, Guid vehicleId)
        {
            var issuer = harness.Services.GetRequiredService<MqttSessionTokenIssuer>();
            var credential = issuer.IssueForVehicle(vehicleId, "test-device");

            var client = new MqttClientFactory().CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(harness.Emqx.Host, harness.Emqx.Port)
                .WithClientId($"test-device-{vehicleId:N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .Build();

            var result = await client.ConnectAsync(options);

            Assert.Equal(MqttClientConnectResultCode.Success, result.ResultCode);

            return new DeviceClient(client);
        }

        public async Task PublishAsync(string topic, string payload, bool retain)
        {
            var result = await _client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build());

            Assert.True(
                result.IsSuccess,
                $"EMQX refused the publish to '{topic}': {result.ReasonCode} ({result.ReasonString}).");
        }

        public async ValueTask DisposeAsync()
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync();
            }

            _client.Dispose();
        }
    }
}

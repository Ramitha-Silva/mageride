using System.Text;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using MageRide.TripState.Domain;
using MageRide.TripState.Mqtt;
using MageRide.TripState.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.TripState.Tests.Integration;

/// <summary>
/// R-15 / T-04: the broker's last will takes a vehicle off the air, and D5' §5.2's cadence hint
/// goes back to it on <c>veh/{vehicleId}/cmd</c>.
/// </summary>
/// <remarks>
/// Against a real EMQX running the deployed policy — <c>EmqxFixture</c> bind-mounts
/// <c>infra/deploy/emqx/emqx.conf</c> and <c>acl.conf</c>. A fake broker would prove only that
/// this service can parse a string it wrote itself, and the two things actually under test are
/// whether the ACL lets a <c>svc-</c> principal hold <c>veh/+/status</c> and publish to another
/// party's <c>cmd</c> topic.
/// </remarks>
[Collection<TripStateCollection>]
public sealed class VehiclePresenceTests(
    PostgresFixture postgres, RedisFixture redis, EmqxFixture emqx)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The whole R-15/T-04 path: a device's last will reaches this service, and the session is
    /// ended by the sweep once the vehicle has stayed away for the grace.
    /// </summary>
    [Fact]
    public async Task A_last_will_takes_the_vehicle_off_the_air_after_the_offline_grace()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis, settings: MqttSettings());

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        var presence = harness.Services.GetRequiredService<VehicleStatusWorker>();
        await presence.StartAsync(CancellationToken.None);

        try
        {
            await WaitAsync(() => presence.IsSubscribed, "the presence subscription never went live");

            // The device's own last will, published by the broker on its behalf. Sent here as the
            // vehicle itself, over the same credential and ACL a real device uses.
            await PublishStatusAsync(vehicleId, VehicleStatus.Offline);

            await WaitAsync(() => presence.Applied > 0, "the last will never reached the service");

            // Not ended yet: a bus passing under a bridge must not lose its journey.
            Assert.Equal(0, (await harness.SweepAsync()).Offline);
            Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));

            await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(3));

            Assert.Equal(1, (await harness.SweepAsync()).Offline);

            var session = Assert.Single(await harness.SessionsAsync(vehicleId));
            Assert.Equal(EndReasons.MqttOffline, session.EndReason);
            Assert.Equal(SessionActors.System, session.EndedBy);
        }
        finally
        {
            await presence.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A vehicle that comes back inside the grace keeps its journey — which is the entire reason
    /// the last will does not end a session on arrival.
    /// </summary>
    [Fact]
    public async Task A_vehicle_that_reconnects_inside_the_grace_keeps_its_session()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis, settings: MqttSettings());

        var driverId = await harness.CreateUserAsync();
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        var started = await harness.StartAsync(harness.Tokens.Driver(driverId), vehicleId);
        var sessionId = Guid.Parse(started.GetProperty("sessionId").GetString()!);

        var presence = harness.Services.GetRequiredService<VehicleStatusWorker>();
        await presence.StartAsync(CancellationToken.None);

        try
        {
            await WaitAsync(() => presence.IsSubscribed, "the presence subscription never went live");

            await PublishStatusAsync(vehicleId, VehicleStatus.Offline);
            await WaitAsync(() => presence.Applied > 0, "the last will never reached the service");

            // Out of the tunnel, and the clock is cleared.
            await PublishStatusAsync(vehicleId, VehicleStatus.Online);
            await WaitAsync(
                async () => await harness.OfflineSinceAsync(sessionId) is null,
                "the presence clock was never cleared");

            await harness.AgeSessionAsync(sessionId, TimeSpan.FromMinutes(3));

            Assert.Equal(0, (await harness.SweepAsync()).Offline);
            Assert.Equal(1, await harness.ActiveSessionCountAsync(driverId));
        }
        finally
        {
            await presence.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// D5' §5.2, R-07: the server pushes the phase's cadence. A session start says "standby
    /// moving"; the end that follows says "standby idle".
    /// </summary>
    [Fact]
    public async Task A_session_transition_pushes_the_cadence_hint_to_the_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        await using var harness = await TripStateHarness.StartAsync(postgres, redis, settings: MqttSettings());

        var driverId = await harness.CreateUserAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = await harness.CreateVehicleAsync(driverId);

        // The device subscribes to its own downlink topic, which acl.conf grants it and nothing
        // else — so receiving the hint here also proves the publish was authorised.
        using var device = new MqttClientFactory().CreateMqttClient();
        var hints = new List<string>();
        var received = new SemaphoreSlim(0);

        device.ApplicationMessageReceivedAsync += args =>
        {
            lock (hints)
            {
                hints.Add(Encoding.UTF8.GetString(args.ApplicationMessage.Payload));
            }

            received.Release();
            return Task.CompletedTask;
        };

        await ConnectAsDeviceAsync(device, vehicleId);

        var subscription = await device.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter
                    .WithTopic(MqttTopics.Command(vehicleId))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build());

        Assert.Equal(MqttClientSubscribeResultCode.GrantedQoS1, subscription.Items.Single().ResultCode);

        var started = await harness.StartAsync(bearer, vehicleId);

        Assert.True(await received.WaitAsync(Timeout), "no cadence hint arrived on the start");

        var sessionId = started.GetProperty("sessionId").GetString();
        await harness.PostAsync($"/v1/sessions/{sessionId}/end", null, bearer);

        Assert.True(await received.WaitAsync(Timeout), "no cadence hint arrived on the end");

        lock (hints)
        {
            Assert.Equal(2, hints.Count);
            Assert.All(hints, hint => Assert.Contains("setPosRate", hint, StringComparison.Ordinal));

            // Standby moving on the start (10 s), standby idle on the end (60 s) — D5' §5.2's two
            // Mode A/B phases.
            Assert.Contains("10000", hints[0], StringComparison.Ordinal);
            Assert.Contains("60000", hints[1], StringComparison.Ordinal);
        }

        await device.DisconnectAsync();
    }

    private Dictionary<string, string?> MqttSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mqtt:Host"] = emqx.Host,
        ["Mqtt:Port"] = emqx.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
        ["TripState:VehicleStatusEnabled"] = "true",
        ["TripState:PublishCadenceHints"] = "true",
        // The worker is resolved and started by the test rather than hosted, so one presence
        // subscription cannot outlive the harness that owns it.
        ["TripState:OfflineGrace"] = "00:02:00",
    };

    /// <summary>Publishes a presence payload as the vehicle itself, over the deployed ACL.</summary>
    private async Task PublishStatusAsync(Guid vehicleId, string payload)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        await ConnectAsDeviceAsync(client, vehicleId);

        await client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(MqttTopics.Status(vehicleId))
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                // Retained, as T-04 specifies: a subscriber that joins later still learns the
                // vehicle's last known presence.
                .WithRetainFlag()
                .Build());

        await client.DisconnectAsync();
    }

    private async Task ConnectAsDeviceAsync(IMqttClient client, Guid vehicleId)
    {
        var credential = new MqttSessionTokenIssuer(
                Microsoft.Extensions.Options.Options.Create(
                    new MqttOptions { SessionTokenSecret = EmqxFixture.SessionTokenSecret }),
                TimeProvider.System)
            .IssueForVehicle(vehicleId, "presence-test");

        var result = await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(emqx.Host, emqx.Port)
                .WithClientId($"device-{Guid.NewGuid():N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .Build());

        Assert.Equal(MqttClientConnectResultCode.Success, result.ResultCode);
    }

    private static Task WaitAsync(Func<bool> condition, string because) =>
        WaitAsync(() => Task.FromResult(condition()), because);

    private static async Task WaitAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(because);
    }
}

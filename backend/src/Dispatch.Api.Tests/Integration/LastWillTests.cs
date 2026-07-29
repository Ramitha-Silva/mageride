using System.Text;
using Dapper;
using MageRide.Dispatch.Dispatching;
using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Mqtt;
using MageRide.Dispatch.Presence;
using MageRide.Dispatch.Tests.Infrastructure;
using MageRide.Dispatch.Timers;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Primitives;
using MageRide.TestKit;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.Dispatch.Tests.Integration;

/// <summary>
/// <b>DoD 4 — "a driver whose EMQX session drops has their live offer released within the
/// configured grace".</b> R-15's last will, from the broker to the released offer.
/// </summary>
[Collection<DispatchCollection>]
public sealed class LastWillTests(PostgresFixture postgres, RedisFixture redis, EmqxFixture emqx)
{
    private static readonly GeoPoint Nearest = new(6.9350, 79.8430);
    private static readonly GeoPoint AlsoNear = new(6.9360, 79.8430);

    /// <summary>
    /// The whole path on a real broker: a device publishes <c>offline</c> on its own status topic,
    /// this service's subscription picks it up, the grace is armed, and the sweep releases the
    /// offer so the ride can cascade to somebody who is actually there.
    /// </summary>
    [Fact]
    public async Task A_dropped_session_releases_the_drivers_live_offer_and_re_offers_the_ride()
    {
        await using var harness = await StartWithBrokerAsync();

        var dropped = await harness.CreateOnlineDriverAsync(Nearest);
        var standby = await harness.CreateOnlineDriverAsync(AlsoNear);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var offered = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, offered.Result);
        Assert.Equal(dropped.DriverId, offered.DriverId);

        await WaitForSubscriptionAsync(harness);
        await PublishStatusAsync(dropped.VehicleId, VehicleStatus.Offline);

        // The last will starts a clock; it does not release anything by itself (R-16's reasoning:
        // a driver in an underpass has not declined the ride).
        var timerId = await WaitForGraceAsync(harness, dropped.DriverId);

        Assert.Equal("Offered", (await harness.ReadRideAsync(rideId)).State);

        await MakeDueAsync(harness, timerId);
        Assert.Equal(1, await SweepAsync(harness));

        // The offer is settled and ride-svc has put the ride back in Matching, which is what makes
        // the next round possible.
        await using (var connection = await harness.OpenAsync())
        {
            Assert.Equal(
                OfferStatuses.Expired,
                await connection.ExecuteScalarAsync<string>(
                    "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { offered.OfferId }));

            // Presence follows the session: a driver with no broker connection cannot answer an
            // offer, so they leave the pool exactly as POST /v1/standby/offline would take them out.
            Assert.Equal(
                PresenceStates.Offline,
                await connection.ExecuteScalarAsync<string>(
                    "SELECT state FROM dispatch.driver_presence WHERE driver_id = @DriverId;",
                    new { dropped.DriverId }));
        }

        Assert.Equal("Matching", (await harness.ReadRideAsync(rideId)).State);

        // And the ride finds the driver who is still there.
        var next = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, next.Result);
        Assert.Equal(standby.DriverId, next.DriverId);
    }

    /// <summary>
    /// The flap case. A device that reconnects inside the grace keeps its offer — which is the
    /// difference between "the connection wobbled" and "the driver is gone".
    /// </summary>
    [Fact]
    public async Task Coming_back_inside_the_grace_keeps_the_offer()
    {
        await using var harness = await StartWithBrokerAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        var offered = await OfferLoopTests.DispatchAsync(harness, rideId);

        Assert.Equal(DispatchResult.Offered, offered.Result);

        await WaitForSubscriptionAsync(harness);
        await PublishStatusAsync(driver.VehicleId, VehicleStatus.Offline);
        await WaitForGraceAsync(harness, driver.DriverId);

        await PublishStatusAsync(driver.VehicleId, VehicleStatus.Online);
        await WaitForNoGraceAsync(harness, driver.DriverId);

        // Nothing due, so a sweep finds nothing to fire — and the offer is still the driver's.
        Assert.Equal(0, await SweepAsync(harness));
        Assert.Equal("Offered", (await harness.ReadRideAsync(rideId)).State);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            OfferStatuses.Offered,
            await connection.ExecuteScalarAsync<string>(
                "SELECT status FROM dispatch.offers WHERE id = @OfferId;", new { offered.OfferId }));
    }

    /// <summary>
    /// §11.12 gives ride-svc four last-will graces on an <em>accepted</em> ride, each ending in a
    /// cancellation with a reputation hit. dispatch-svc arms nothing for a driver mid-ride, or the
    /// two would race over one fact.
    /// </summary>
    [Fact]
    public async Task A_driver_who_is_mid_ride_is_left_to_ride_svcs_offline_grace()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);

        var rideId = await harness.RequestRideAsync(await harness.CreatePassengerAsync());
        await OfferLoopTests.DispatchAsync(harness, rideId);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatchService>()
                .MarkAcceptedAsync(rideId, driver.DriverId, TestContext.Current.CancellationToken);
        }

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            Assert.False(
                await scope.ServiceProvider.GetRequiredService<IVehicleStatusService>()
                    .WentOfflineAsync(driver.VehicleId, TestContext.Current.CancellationToken));
        }

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.timers WHERE driver_id = @DriverId;", new { driver.DriverId }));
    }

    /// <summary>
    /// Most last wills on <c>veh/+/status</c> belong to Mode A buses and Mode B shared vehicles,
    /// which have no presence row here at all.
    /// </summary>
    [Fact]
    public async Task A_last_will_for_a_vehicle_nobody_is_on_standby_with_is_a_no_op()
    {
        await using var harness = await StartAsync();

        await using var scope = harness.Services.CreateAsyncScope();

        Assert.False(
            await scope.ServiceProvider.GetRequiredService<IVehicleStatusService>()
                .WentOfflineAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A device that flaps must not keep pushing its own deadline out, or an unstable connection
    /// would hold an offer for as long as the instability lasted (migration 0711's index).
    /// </summary>
    [Fact]
    public async Task A_repeated_offline_does_not_extend_the_grace()
    {
        await using var harness = await StartAsync();

        var driver = await harness.CreateOnlineDriverAsync(Nearest);
        await OfferLoopTests.DispatchAsync(
            harness, await harness.RequestRideAsync(await harness.CreatePassengerAsync()));

        await using var scope = harness.Services.CreateAsyncScope();
        var status = scope.ServiceProvider.GetRequiredService<IVehicleStatusService>();

        Assert.True(await status.WentOfflineAsync(driver.VehicleId, TestContext.Current.CancellationToken));

        await using var connection = await harness.OpenAsync();
        var first = await connection.ExecuteScalarAsync<DateTimeOffset>(
            "SELECT fire_at FROM dispatch.timers WHERE driver_id = @DriverId AND fired_at IS NULL;",
            new { driver.DriverId });

        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.True(await status.WentOfflineAsync(driver.VehicleId, TestContext.Current.CancellationToken));

        var second = await connection.ExecuteScalarAsync<DateTimeOffset>(
            "SELECT fire_at FROM dispatch.timers WHERE driver_id = @DriverId AND fired_at IS NULL;",
            new { driver.DriverId });

        Assert.Equal(first, second);

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM dispatch.timers WHERE driver_id = @DriverId;", new { driver.DriverId }));
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Publishes on the vehicle's own status topic as the device would. <c>acl.conf</c> allows
    /// <c>veh/${username}/status</c> precisely so a device can register the last will at CONNECT,
    /// which is why this client authenticates as the vehicle rather than as a service.
    /// </summary>
    private async Task PublishStatusAsync(Guid vehicleId, string payload)
    {
        var tokens = new MqttSessionTokenIssuer(
            Microsoft.Extensions.Options.Options.Create(new MqttOptions
            {
                Host = emqx.Host,
                Port = emqx.Port,
                SessionTokenSecret = EmqxFixture.SessionTokenSecret,
            }),
            TimeProvider.System);

        var credential = tokens.IssueForVehicle(vehicleId, deviceId: vehicleId.ToString());

        using var client = new MqttClientFactory().CreateMqttClient();

        var connected = await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(emqx.Host, emqx.Port)
                .WithClientId($"mageride-test-device-{Guid.NewGuid():N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .Build(),
            TestContext.Current.CancellationToken);

        Assert.Equal(MqttClientConnectResultCode.Success, connected.ResultCode);

        // Retained, exactly as the broker publishes a last will (D6' §3.1: "QoS1 retained").
        await client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(MqttTopics.Status(vehicleId))
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build(),
            TestContext.Current.CancellationToken);

        await client.DisconnectAsync(
            new MqttClientDisconnectOptionsBuilder().Build(), TestContext.Current.CancellationToken);
    }

    private static async Task WaitForSubscriptionAsync(DispatchHarness harness)
    {
        var worker = harness.Services.GetRequiredService<VehicleStatusWorker>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (!worker.IsSubscribed && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(worker.IsSubscribed, "the veh/+/status subscription never came up");
    }

    private static Task<Guid> WaitForGraceAsync(DispatchHarness harness, Guid driverId) =>
        PollAsync<Guid>(
            harness,
            "SELECT id FROM dispatch.timers WHERE driver_id = @DriverId AND kind = @Kind AND fired_at IS NULL;",
            driverId,
            id => id != Guid.Empty,
            "the R-15 grace was never armed");

    private static Task WaitForNoGraceAsync(DispatchHarness harness, Guid driverId) =>
        PollAsync<int>(
            harness,
            """
            SELECT count(*)::int FROM dispatch.timers
             WHERE driver_id = @DriverId AND kind = @Kind AND fired_at IS NULL;
            """,
            driverId,
            count => count == 0,
            "the R-15 grace was never retired");

    private static async Task<T> PollAsync<T>(
        DispatchHarness harness, string sql, Guid driverId, Func<T, bool> done, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = await harness.OpenAsync();

            var value = await connection.ExecuteScalarAsync<T>(
                sql, new { DriverId = driverId, Kind = DispatchTimerKinds.OfferReleaseGrace });

            if (value is not null && done(value))
            {
                return value;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail(failure);
        return default!;
    }

    private static async Task MakeDueAsync(DispatchHarness harness, Guid timerId)
    {
        await using var connection = await harness.OpenAsync();

        await connection.ExecuteAsync(
            "UPDATE dispatch.timers SET fire_at = now() - interval '1 second' WHERE id = @TimerId;",
            new { TimerId = timerId });
    }

    private static async Task<int> SweepAsync(DispatchHarness harness)
    {
        var worker = ActivatorUtilities.CreateInstance<DispatchTimerWorker>(harness.Services);

        return await worker.SweepOnceAsync(TestContext.Current.CancellationToken);
    }

    private Task<DispatchHarness> StartWithBrokerAsync()
    {
        Assert.SkipWhen(!emqx.IsAvailable, emqx.SkipReason ?? string.Empty);

        return StartAsync(new Dictionary<string, string?>
        {
            ["Dispatch:LastWillEnabled"] = "true",
            ["Mqtt:Host"] = emqx.Host,
            ["Mqtt:Port"] = emqx.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mqtt:SessionTokenSecret"] = EmqxFixture.SessionTokenSecret,
        });
    }

    private Task<DispatchHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        return DispatchHarness.StartAsync(postgres, redis, settings);
    }
}

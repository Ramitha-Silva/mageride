using System.Text;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.E2E.Infrastructure;

/// <summary>
/// A vehicle's own MQTT session on the real broker, last will and all.
/// </summary>
/// <remarks>
/// <para>
/// The device authenticates as its <c>vehicleId</c> — <c>emqx.conf</c>'s
/// <c>verify_claims = { vehicleId = "${username}" }</c> refuses the CONNECT otherwise — and
/// registers <c>veh/{vehicleId}/status = offline</c>, retained at QoS 1, as its will. That is
/// exactly what D6' §3.1 describes and what <c>acl.conf</c> grants a device the publish right to
/// precisely so the will can be registered at CONNECT.
/// </para>
/// <para>
/// <b>The broker publishes the will, not this class.</b> <see cref="DropAsync"/> ends the session
/// with MQTT 5's <c>DisconnectWithWillMessage</c> reason, which is the protocol's way of saying
/// "I am going away and the will applies" — so what ride-svc's and dispatch-svc's subscriptions see
/// is a genuine last will from EMQX rather than a message a test published on the device's behalf.
/// A scenario that published <c>offline</c> itself would prove the two services can parse a string.
/// </para>
/// </remarks>
internal sealed class DeviceSession : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly Guid _vehicleId;

    private DeviceSession(IMqttClient client, Guid vehicleId)
    {
        _client = client;
        _vehicleId = vehicleId;
    }

    /// <summary>Connects a vehicle to the broker with the R-15 last will armed.</summary>
    public static async Task<DeviceSession> ConnectAsync(EmqxFixture emqx, Guid vehicleId)
    {
        ArgumentNullException.ThrowIfNull(emqx);

        var tokens = new MqttSessionTokenIssuer(
            Options.Create(new MqttOptions
            {
                Host = emqx.Host,
                Port = emqx.Port,
                SessionTokenSecret = EmqxFixture.SessionTokenSecret,
            }),
            TimeProvider.System);

        var credential = tokens.IssueForVehicle(vehicleId, deviceId: vehicleId.ToString());

        var client = new MqttClientFactory().CreateMqttClient();

        var connected = await client.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(emqx.Host, emqx.Port)
                .WithClientId($"mageride-e2e-device-{vehicleId:N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .WithSessionExpiryInterval(0)
                .WithWillTopic(MqttTopics.Status(vehicleId))
                .WithWillPayload(Encoding.UTF8.GetBytes(VehicleStatus.Offline))
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithWillRetain()
                .Build(),
            TestContext.Current.CancellationToken);

        Assert.Equal(MqttClientConnectResultCode.Success, connected.ResultCode);

        var session = new DeviceSession(client, vehicleId);

        // A device that has just connected is online, and the retained `offline` from its previous
        // session — or from a scenario that ran before it — would otherwise still be the last thing
        // on the topic for every subscriber that joins next.
        await session.PublishAsync(VehicleStatus.Online);

        return session;
    }

    /// <summary>
    /// Ends the session so the broker publishes the will — the R-15 event proper.
    /// </summary>
    public async Task DropAsync()
    {
        await _client.DisconnectAsync(
            new MqttClientDisconnectOptionsBuilder()
                .WithReason(MqttClientDisconnectOptionsReason.DisconnectWithWillMessage)
                .Build(),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Publishes the vehicle's status directly — how a device says it is back (R-16's flap).</summary>
    public Task PublishAsync(string status) => PublishAsync(_client, _vehicleId, status);

    /// <summary>
    /// Reconnects a vehicle that has been dropped and announces it, without arming a second will.
    /// </summary>
    /// <remarks>
    /// The retained <c>online</c> is what retires an unfired grace: ride-svc's presence subscriber
    /// treats it as "the vehicle is back", which is R-16's distinction between a connection that
    /// wobbled and a driver who is gone.
    /// </remarks>
    public static async Task ComeBackAsync(EmqxFixture emqx, Guid vehicleId)
    {
        ArgumentNullException.ThrowIfNull(emqx);

        await using var session = await ConnectAsync(emqx, vehicleId);
        await session.PublishAsync(VehicleStatus.Online);
    }

    private static async Task PublishAsync(IMqttClient client, Guid vehicleId, string status)
    {
        await client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(MqttTopics.Status(vehicleId))
                .WithPayload(Encoding.UTF8.GetBytes(status))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build(),
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            // A clean DISCONNECT: the will is discarded, which is what a driver going off shift
            // deliberately does and is not what a scenario that has already called DropAsync wants
            // to happen a second time.
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }

        _client.Dispose();
    }
}

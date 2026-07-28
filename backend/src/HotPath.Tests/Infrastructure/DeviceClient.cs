using MageRide.Shared.Mqtt;
using MageRide.Shared.Telemetry;
using MageRide.TestKit;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.HotPath.Tests.Infrastructure;

/// <summary>
/// A driver handset, as far as EMQX is concerned: an MQTT 5 client presenting a session JWT bound
/// to one vehicle.
/// </summary>
/// <remarks>
/// The credential is minted with the same <see cref="MqttSessionTokenIssuer"/> the bridge and
/// provisioning-svc use, against the same secret the fixture gives the broker. That is what makes
/// the ACL assertions mean anything: the token is real, the username is the one <c>emqx.conf</c>
/// verifies the <c>vehicleId</c> claim against, and <c>acl.conf</c> is the deployed file.
/// </remarks>
internal sealed class DeviceClient : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly TaskCompletionSource<MqttClientDisconnectedEventArgs> _disconnected = new();

    private DeviceClient(IMqttClient client, Guid vehicleId, MqttSessionToken credential)
    {
        _client = client;
        VehicleId = vehicleId;
        Credential = credential;

        _client.DisconnectedAsync += args =>
        {
            _disconnected.TrySetResult(args);
            return Task.CompletedTask;
        };
    }

    public Guid VehicleId { get; }

    public MqttSessionToken Credential { get; }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Completes when the broker drops this client — how a denied publish is observed.</summary>
    public Task<MqttClientDisconnectedEventArgs> Disconnected => _disconnected.Task;

    /// <summary>Mints a session token for <paramref name="vehicleId"/> against the fixture's secret.</summary>
    public static MqttSessionToken CredentialFor(Guid vehicleId, string deviceId = "test-device") =>
        Issuer().IssueForVehicle(vehicleId, deviceId);

    /// <summary>Connects as <paramref name="vehicleId"/>, presenting a token minted for it.</summary>
    public static Task<DeviceClient> ConnectAsync(
        EmqxFixture emqx, Guid vehicleId, CancellationToken cancellationToken = default) =>
        ConnectAsync(emqx, vehicleId, CredentialFor(vehicleId), cancellationToken);

    /// <summary>
    /// Connects as <paramref name="vehicleId"/> with an arbitrary credential — for the tests that
    /// need the username and the token's claim to disagree.
    /// </summary>
    public static async Task<DeviceClient> ConnectAsync(
        EmqxFixture emqx,
        Guid vehicleId,
        MqttSessionToken credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emqx);
        emqx.RequireAvailable();

        var client = new MqttClientFactory().CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(emqx.Host, emqx.Port)
            .WithClientId($"device-{vehicleId:N}-{Guid.NewGuid():N}"[..40])
            // The MQTT username is the principal: emqx.conf refuses the CONNECT unless the token's
            // vehicleId claim equals it, and acl.conf writes every device rule as ${username}.
            .WithCredentials(vehicleId.ToString(), credential.Jwt)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0)
            .WithTimeout(TimeSpan.FromSeconds(15))
            .Build();

        var device = new DeviceClient(client, vehicleId, credential);
        var result = await client.ConnectAsync(options, cancellationToken);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            await device.DisposeAsync();
            throw new MqttConnectRefusedException(result.ResultCode, result.ReasonString);
        }

        return device;
    }

    /// <summary>Publishes a CBOR <see cref="PositionSample"/> to this vehicle's own live topic.</summary>
    public Task<MqttClientPublishResult> PublishPositionAsync(
        PositionSample sample, CancellationToken cancellationToken = default) =>
        PublishAsync(MqttTopics.PositionLive(VehicleId), PositionSampleCodec.Encode(sample), cancellationToken);

    /// <summary>Subscribes to a filter — including one the ACL should refuse.</summary>
    public Task<MqttClientSubscribeResult> SubscribeAsync(string filter, CancellationToken cancellationToken = default) =>
        _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic => topic
                    .WithTopic(filter)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken);

    /// <summary>Publishes to an arbitrary topic — including one the ACL should refuse.</summary>
    public Task<MqttClientPublishResult> PublishAsync(
        string topic, byte[] payload, CancellationToken cancellationToken = default) =>
        _client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // A broker that already dropped us — which several of these tests arrange on purpose.
        }

        _client.Dispose();
    }

    private static MqttSessionTokenIssuer Issuer() => new(
        Options.Create(new MqttOptions { SessionTokenSecret = EmqxFixture.SessionTokenSecret }),
        TimeProvider.System);
}

/// <summary>The broker refused a CONNECT, and with which reason code.</summary>
internal sealed class MqttConnectRefusedException(MqttClientConnectResultCode code, string? reason)
    : Exception($"EMQX refused the CONNECT: {code} ({reason ?? "no reason given"}).")
{
    public MqttClientConnectResultCode Code { get; } = code;
}

using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MageRide.Shared.Mqtt;
using MageRide.TestKit;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.Security.Tests.AntiSpoof.Mqtt;

/// <summary>Which of the broker's three live listeners a device is dialling.</summary>
public enum MqttPlane
{
    /// <summary>1883, plain TCP + session JWT. In-cluster only — mqtt-bridge and tcp-adapter.</summary>
    InClusterTcp,

    /// <summary>8084, MQTT over WSS + session JWT. <b>What a driver's handset actually connects to.</b></summary>
    MobileWebSocket,

    /// <summary>8883, mutual TLS, username from the certificate CN. The hardware-tracker plane (T-02).</summary>
    TrackerMutualTls,
}

/// <summary>
/// One device, on one plane, with one credential — the client the ACL matrix drives.
/// </summary>
/// <remarks>
/// <para>
/// The three planes differ in transport and in how the broker decides the principal, and in nothing
/// else: <c>acl.conf</c>'s <c>veh/${username}/*</c> rules are shared. That is the property the
/// matrix exists to prove, and it can only be proved by driving each plane with its own credential
/// shape rather than by driving one and reasoning about the others.
/// </para>
/// <para>
/// <b>A fresh TLS target host per connection.</b> .NET caches and resumes TLS sessions per target
/// host, and this class deliberately makes connections with and without a client certificate to the
/// same broker; resuming a certificate-less ticket on a <c>fail_if_no_peer_cert</c> listener is
/// refused, so without this the tests poison each other in whatever order they run. Real devices
/// share neither a process nor a session cache.
/// </para>
/// </remarks>
internal sealed class MqttDevice : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly TaskCompletionSource _disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MqttDevice(IMqttClient client, Guid vehicleId, MqttPlane plane)
    {
        _client = client;
        VehicleId = vehicleId;
        Plane = plane;

        _client.DisconnectedAsync += _ =>
        {
            _disconnected.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public Guid VehicleId { get; }

    public MqttPlane Plane { get; }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Completes when the broker drops this client — how a denied publish is observed.</summary>
    public Task Disconnected => _disconnected.Task;

    /// <summary>Connects as <paramref name="vehicleId"/> on <paramref name="plane"/>.</summary>
    /// <param name="certificate">
    /// Required on <see cref="MqttPlane.TrackerMutualTls"/> and ignored elsewhere: the tracker
    /// listener has no authenticator, so the certificate is the credential.
    /// </param>
    public static async Task<MqttDevice> ConnectAsync(
        EmqxFixture emqx,
        Guid vehicleId,
        MqttPlane plane,
        MqttSessionToken? credential = null,
        X509Certificate2? certificate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emqx);
        emqx.RequireAvailable();

        var builder = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0)
            .WithTimeout(TimeSpan.FromSeconds(20))
            // Unique per connection. Two sessions under one vehicle credential is a case D-17
            // exists for — the listener ceiling is per connection and D-17's is per vehicleId — so
            // it has to be expressible, and a repeated client id makes the broker drop the earlier.
            .WithClientId($"c128-{vehicleId.ToString("N")[..8]}-{Guid.NewGuid().ToString("N")[..12]}");

        switch (plane)
        {
            case MqttPlane.InClusterTcp:
                builder = builder
                    .WithTcpServer(emqx.Host, emqx.Port)
                    .WithCredentials(vehicleId.ToString(), (credential ?? TokenFor(vehicleId)).Jwt);
                break;

            case MqttPlane.MobileWebSocket:
                builder = builder
                    .WithWebSocketServer(server => server.WithUri(emqx.WebSocketUri))
                    .WithTlsOptions(tls => tls
                        .UseTls()
                        .WithTargetHost(FreshTargetHost())
                        .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                        // The replica's edge certificate is self-signed by design; what is under
                        // test is the broker's authorisation, not the build host's trust store.
                        .WithCertificateValidationHandler(_ => true))
                    .WithCredentials(vehicleId.ToString(), (credential ?? TokenFor(vehicleId)).Jwt);
                break;

            case MqttPlane.TrackerMutualTls:
                ArgumentNullException.ThrowIfNull(certificate);
                builder = builder
                    .WithTcpServer(emqx.Host, emqx.TlsPort)
                    .WithTlsOptions(tls => tls
                        .UseTls()
                        .WithTargetHost(FreshTargetHost())
                        .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                        .WithClientCertificates([certificate])
                        .WithCertificateValidationHandler(_ => true));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(plane), plane, "Unknown MQTT plane.");
        }

        var client = new MqttClientFactory().CreateMqttClient();
        var device = new MqttDevice(client, vehicleId, plane);

        MqttClientConnectResult result;

        try
        {
            result = await client.ConnectAsync(builder.Build(), cancellationToken);
        }
        catch (Exception exception)
        {
            await device.DisposeAsync();

            // A refused mutual-TLS handshake surfaces client-side as a bare EOF; the reason code
            // only exists on the JWT planes.
            throw new MqttPlaneRefusedException(plane, MqttClientConnectResultCode.UnspecifiedError, exception.Message);
        }

        if (result.ResultCode is not MqttClientConnectResultCode.Success)
        {
            await device.DisposeAsync();
            throw new MqttPlaneRefusedException(plane, result.ResultCode, result.ReasonString);
        }

        return device;
    }

    /// <summary>Mints a session token for a vehicle against the fixture's own secret.</summary>
    public static MqttSessionToken TokenFor(Guid vehicleId, string deviceId = "c128-device") =>
        new MqttSessionTokenIssuer(
                Options.Create(new MqttOptions { SessionTokenSecret = EmqxFixture.SessionTokenSecret }),
                TimeProvider.System)
            .IssueForVehicle(vehicleId, deviceId);

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

    public Task<MqttClientSubscribeResult> SubscribeAsync(
        string filter, CancellationToken cancellationToken = default) =>
        _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic => topic
                    .WithTopic(filter)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // A broker that already dropped us — which several of these tests arrange on purpose.
        }

        _client.Dispose();
    }

    private static string FreshTargetHost() => $"c128-{Guid.NewGuid().ToString("N")[..12]}.mageride.test";
}

/// <summary>The broker refused a connection, and on which plane.</summary>
internal sealed class MqttPlaneRefusedException(MqttPlane plane, MqttClientConnectResultCode code, string? reason)
    : Exception($"EMQX refused the CONNECT on the {plane} plane: {code} ({reason ?? "no reason given"}).")
{
    public MqttPlane Plane { get; } = plane;

    public MqttClientConnectResultCode Code { get; } = code;
}

using System.Text.Json;
using MageRide.Shared.Http;
using MageRide.Shared.Mqtt;
using MageRide.TripState.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.TripState.Mqtt;

/// <summary>The downlink envelope ADD §7.7.5 defines, carrying D5' §5.2's cadence hint.</summary>
/// <param name="Cmd">Always <c>setPosRate</c> here; the other four commands belong to other services.</param>
/// <param name="Args">D5' §5.2 spells the hint <c>{"cmd":"setPosRate","intervalMs":2000}</c>.</param>
/// <param name="ExpiresAt">ADD §7.7.5: "expired commands are not delivered on reconnect". A rate
/// hint is about the phase the vehicle is in *now*, and delivering yesterday's to a device that
/// has been offline would set the wrong rate for the journey it is on today.</param>
public sealed record DownlinkCommand(string Cmd, IReadOnlyDictionary<string, object> Args, DateTimeOffset ExpiresAt);

/// <summary>
/// Pushes the adaptive GPS cadence hint to a vehicle (D5' §5.2, R-07).
/// </summary>
/// <remarks>
/// <para>
/// D5' §5.2 puts "Standby moving" (5–10 s) against a vehicle with a live A/B session and "Standby
/// idle" (30–60 s) against one without, and says <b>the server</b> pushes the hint on
/// <c>veh/{vehicleId}/cmd</c>. This is the only place in the Mode A/B plane that knows when a
/// vehicle crosses between those two phases, so it is where the push belongs.
/// </para>
/// <para>
/// <b>Best effort, always.</b> A device that never receives the hint keeps its previous rate:
/// that costs battery and a little bandwidth, not correctness, and a session start must not fail
/// because the broker was briefly unreachable. The publish therefore happens after COMMIT and
/// swallows its errors — loudly.
/// </para>
/// </remarks>
public interface ICadencePublisher
{
    Task PublishAsync(Guid vehicleId, TimeSpan interval, CancellationToken cancellationToken);
}

/// <summary>
/// The no-op. Registered when <c>TripState:PublishCadenceHints</c> is off, which is the default.
/// </summary>
/// <remarks>
/// Off by default because it is the one part of this service that needs a broker connection, and a
/// deployment without EMQX reachable should run the session lifecycle rather than log a connection
/// failure on every start. It says so once, at start-up, rather than on every transition.
/// </remarks>
public sealed class DisabledCadencePublisher(ILogger<DisabledCadencePublisher> logger) : ICadencePublisher
{
    private int _warned;

    public Task PublishAsync(Guid vehicleId, TimeSpan interval, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogInformation(
                "TripState:PublishCadenceHints is off, so no setPosRate hint is sent on session transitions " +
                "(D5' §5.2). Devices keep whatever rate they were last given.");
        }

        return Task.CompletedTask;
    }
}

/// <inheritdoc cref="ICadencePublisher"/>
public sealed class MqttCadencePublisher(
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    IOptions<TripStateOptions> tripOptions,
    TimeProvider clock,
    ILogger<MqttCadencePublisher> logger) : ICadencePublisher, IAsyncDisposable
{
    /// <summary>
    /// How long a cadence hint stays deliverable.
    /// </summary>
    /// <remarks>
    /// Five minutes: long enough to survive a device's reconnect after a tunnel, short enough that
    /// a vehicle coming back from an hour offline is told its rate afresh rather than resuming one
    /// chosen for a journey that has since ended.
    /// </remarks>
    private static readonly TimeSpan HintLifetime = TimeSpan.FromMinutes(5);

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));
    private readonly TripStateOptions _trip =
        tripOptions?.Value ?? throw new ArgumentNullException(nameof(tripOptions));

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IMqttClient? _client;

    public async Task PublishAsync(Guid vehicleId, TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            var client = await ConnectedClientAsync(cancellationToken);

            var hint = new DownlinkCommand(
                "setPosRate",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["intervalMs"] = (int)interval.TotalMilliseconds,
                },
                clock.GetUtcNow() + HintLifetime);

            await client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(MqttTopics.Command(vehicleId))
                    .WithPayload(JsonSerializer.SerializeToUtf8Bytes(hint, MageRideJson.Options))
                    // QoS 1: the hint matters enough to redeliver, and the device applies the last
                    // one it sees — a duplicate sets the same rate twice.
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not push the {IntervalMs} ms cadence hint to vehicle {VehicleId}; it keeps its previous rate",
                (int)interval.TotalMilliseconds,
                vehicleId);
        }
    }

    private async Task<IMqttClient> ConnectedClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true } connected)
        {
            return connected;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_client is { IsConnected: true } fresh)
            {
                return fresh;
            }

            if (_client is not null)
            {
                _client.Dispose();
                _client = null;
            }

            var client = new MqttClientFactory().CreateMqttClient();

            // The same credential shape the bridge uses: `svc-<name>` plus a session JWT, which
            // `acl.conf` grants `veh/#` publish. A device cannot publish to another vehicle's
            // `cmd` topic; a service account is exactly what may.
            var credential = tokens.IssueForService(_trip.MqttServiceName);

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_mqtt.Host, _mqtt.Port)
                .WithClientId($"mageride-trip-state-{Guid.NewGuid():N}")
                .WithCredentials(credential.Username, credential.Jwt)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCleanStart(true)
                .WithSessionExpiryInterval(0);

            if (_mqtt.UseTls)
            {
                options = options.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(_mqtt.Host));
            }

            var result = await client.ConnectAsync(options.Build(), cancellationToken);

            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                client.Dispose();

                throw new InvalidOperationException(
                    $"EMQX refused the trip-state CONNECT: {result.ResultCode} ({result.ReasonString}). " +
                    "Check that Mqtt:SessionTokenSecret matches EMQX_AUTHENTICATION__1__SECRET.");
            }

            _client = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
            }

            _client.Dispose();
            _client = null;
        }

        _gate.Dispose();
    }
}

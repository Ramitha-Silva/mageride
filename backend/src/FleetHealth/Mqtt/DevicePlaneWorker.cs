using System.Text;
using MageRide.FleetHealth.Configuration;
using MageRide.FleetHealth.Diagnostics;
using MageRide.FleetHealth.Domain;
using MageRide.FleetHealth.Persistence;
using MageRide.Shared.Mqtt;
using MageRide.Shared.Observability;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.FleetHealth.Mqtt;

/// <summary>
/// Holds this service's two EMQX subscriptions: the retained <c>veh/{vehicleId}/status</c> last will
/// (R-15, T-04) and <c>sys/diag/{vehicleId}</c> (D6' §3.1, US-3.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Both filters on one broker session.</b> The two are alike in every way that matters — low rate,
/// idempotent, one small write each — so a second session would double the connection count for no
/// isolation. That is the opposite of C038's call for <c>pos/live</c> and <c>pos/replay</c>, which
/// share an inflight window and had to be split precisely because a throttled backlog would stall live
/// delivery; nothing here is throttled and nothing here is on the hot path.
/// </para>
/// <para>
/// <b>Whole-topic subscriptions, not shared ones.</b> Presence and diagnostics are rare and every
/// write is idempotent, so a replica taking a copy costs one upsert and missing one costs a device
/// reading staler than it is. <c>mqtt-topics.md</c> §4's shared subscription exists to stop duplicate
/// *ingest* into <c>telemetry.raw</c>; there is no equivalent hazard here. The same call ride-svc,
/// dispatch-svc and fanout-svc all make for <c>veh/+/status</c>.
/// </para>
/// <para>
/// <b>Manual acknowledgement, so a failed write is redelivered.</b> The alternative — acknowledging
/// and dropping — would lose the one message that says a fleet went dark, which is the message this
/// subscription exists for.
/// </para>
/// <para>
/// <b>T-04's two publishers are indistinguishable here, deliberately.</b> EMQX publishes the last will
/// for an MQTT device; tcp-adapter publishes the same retained <c>offline</c> when a legacy device's
/// socket half-closes (C043). Both arrive on the same topic with the same payload and neither is
/// treated specially — which is what makes the state ladder identical for a GT06 on a raw socket and a
/// Teltonika on MQTT.
/// </para>
/// <para>
/// A retained message is delivered on every (re)subscribe, so this worker sees each vehicle's last
/// known status at start-up and after every reconnect. That is why the status upsert compares
/// timestamps rather than assuming a message is news: a redelivered <c>offline</c> for a device that
/// has since come back must not move it out of <c>Online</c>.
/// </para>
/// </remarks>
public sealed class DevicePlaneWorker(
    IDeviceHealthRepository repository,
    MqttSessionTokenIssuer tokens,
    IOptions<MqttOptions> mqttOptions,
    IOptions<FleetHealthOptions> healthOptions,
    TimeProvider clock,
    ILogger<DevicePlaneWorker> logger) : BackgroundService
{
    /// <summary>Every vehicle's presence topic — <c>veh/+/status</c> (D3' §3.2).</summary>
    public const string StatusFilter = "veh/+/status";

    /// <summary>Every vehicle's diagnostics topic — <c>sys/diag/+</c>.</summary>
    public const string DiagnosticsFilter = "sys/diag/+";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly MqttOptions _mqtt = mqttOptions?.Value ?? throw new ArgumentNullException(nameof(mqttOptions));

    private readonly FleetHealthOptions _health =
        healthOptions?.Value ?? throw new ArgumentNullException(nameof(healthOptions));

    private bool _subscribed;

    /// <summary>
    /// What the last session died of, or <see langword="null"/> while one is healthy.
    /// </summary>
    /// <remarks>
    /// The retry loop below swallows every exception into <c>logger.LogError</c>, which is right —
    /// a broker that went away must not take the worker with it. But it makes the failure
    /// undiagnosable anywhere the log is not visible, and a test harness is exactly such a place:
    /// xUnit captures console output, so `IsSubscribed` staying false looks like a timeout with no
    /// cause. Keeping the exception on the worker gives the one channel that always works.
    /// </remarks>
    private volatile Exception? _lastError;
    private long _statusApplied;
    private long _diagnosticsApplied;

    /// <summary>True once both subscriptions are live, so a test can wait rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    /// <summary>What the last session died of. See the field's remark.</summary>
    public Exception? LastError => _lastError;

    /// <summary>Presence messages this replica has applied.</summary>
    public long StatusApplied => Interlocked.Read(ref _statusApplied);

    /// <summary>Diagnostics frames this replica has applied.</summary>
    public long DiagnosticsApplied => Interlocked.Read(ref _diagnosticsApplied);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
                attempt = 0;
                _lastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                attempt++;
                _lastError = exception;
                logger.LogError(exception, "Device-plane subscription session {Attempt} ended; reconnecting", attempt);
            }
            finally
            {
                Volatile.Write(ref _subscribed, false);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(BackoffFor(attempt), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.DisconnectedAsync += args =>
        {
            logger.LogWarning(
                "Device-plane subscription disconnected: {Reason}", args.ReasonString ?? args.Reason.ToString());

            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += args => ApplyAsync(args, stoppingToken);

        var credential = tokens.IssueForService(_health.MqttServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqtt.Host, _mqtt.Port)
            .WithClientId($"mageride-fleet-health-{Guid.NewGuid():N}")
            .WithCredentials(credential.Username, credential.Jwt)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithTimeout(ConnectTimeout)
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0);

        if (_mqtt.UseTls)
        {
            options = options.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(_mqtt.Host));
        }

        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCancellation.CancelAfter(ConnectTimeout);

        var result = await client.ConnectAsync(options.Build(), connectCancellation.Token);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"EMQX refused the fleet-health CONNECT: {result.ResultCode} ({result.ReasonString}). " +
                "Check that Mqtt:SessionTokenSecret matches EMQX_AUTHENTICATION__1__SECRET.");
        }

        var builder = new MqttClientSubscribeOptionsBuilder();

        if (_health.StatusEnabled)
        {
            builder = builder.WithTopicFilter(filter => filter
                .WithTopic(StatusFilter)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        }

        if (_health.DiagnosticsEnabled)
        {
            // QoS 0 on the wire (D6' §3.1), subscribed at QoS 1: the subscription's QoS is a ceiling,
            // so asking for 1 costs nothing on a QoS-0 publish and means a future QoS-1 publisher is
            // not silently downgraded.
            builder = builder.WithTopicFilter(filter => filter
                .WithTopic(DiagnosticsFilter)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        }

        var subscription = await client.SubscribeAsync(builder.Build(), stoppingToken);

        foreach (var item in subscription.Items)
        {
            // A refused subscription is the failure that looks like success from the outside: the
            // client stays connected and simply never hears about a vehicle going offline.
            if (item.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2))
            {
                throw new InvalidOperationException(
                    $"EMQX refused the subscription to '{item.TopicFilter.Topic}': {item.ResultCode}.");
            }
        }

        Volatile.Write(ref _subscribed, true);
        logger.LogInformation(
            "Fleet-health device-plane subscription live (status: {Status}, diagnostics: {Diagnostics})",
            _health.StatusEnabled, _health.DiagnosticsEnabled);

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (stoppingToken.Register(() => stopped.TrySetResult()))
        {
            await Task.WhenAny(disconnected.Task, stopped.Task);
        }

        if (stoppingToken.IsCancellationRequested && client.IsConnected)
        {
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
    }

    private async Task ApplyAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        args.AutoAcknowledge = false;

        try
        {
            if (!MqttTopics.TryParse(args.ApplicationMessage.Topic, out var reference))
            {
                return;
            }

            var now = clock.GetUtcNow();

            switch (reference.Kind)
            {
                case MqttTopicKind.Status when _health.StatusEnabled:
                    await ApplyStatusAsync(reference.VehicleId, args, now, cancellationToken);
                    break;

                case MqttTopicKind.Diagnostics when _health.DiagnosticsEnabled:
                    await ApplyDiagnosticsAsync(reference.VehicleId, args, now, cancellationToken);
                    break;

                default:
                    // Another branch of the tree. Acknowledged below: nothing here will ever be able
                    // to read it, so holding it would stall the session.
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not acknowledged, so EMQX redelivers. Both writes are idempotent — the status upsert
            // takes the later timestamp and the diagnostics upsert coalesces — so redelivery is
            // strictly better than dropping the message that says a fleet went dark.
            logger.LogError(exception, "Could not apply {Topic}", args.ApplicationMessage.Topic);
            return;
        }

        await args.AcknowledgeAsync(cancellationToken);
    }

    private async Task ApplyStatusAsync(
        Guid vehicleId,
        MqttApplicationMessageReceivedEventArgs args,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload).Trim();

        var status = payload switch
        {
            _ when string.Equals(payload, VehicleStatus.Online, StringComparison.OrdinalIgnoreCase) =>
                VehicleStatus.Online,
            _ when string.Equals(payload, VehicleStatus.Offline, StringComparison.OrdinalIgnoreCase) =>
                VehicleStatus.Offline,
            _ => null,
        };

        if (status is null)
        {
            // `mqtt-topics.md` §2.3 makes the payload the literal string `online` or `offline`.
            // Anything else is a publisher that has changed shape, and guessing which it meant would
            // be guessing whether a fleet is up.
            logger.LogWarning(
                "Unrecognised presence payload on {Topic}: '{Payload}'", args.ApplicationMessage.Topic, payload);

            return;
        }

        await repository.UpsertStatusAsync(
            [new DeviceStatusReport(vehicleId, status, now)], cancellationToken);

        Interlocked.Increment(ref _statusApplied);
        MageRideDiagnostics.DeviceHealthUpdates.Add(1, new KeyValuePair<string, object?>("input", "status"));
    }

    private async Task ApplyDiagnosticsAsync(
        Guid vehicleId,
        MqttApplicationMessageReceivedEventArgs args,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!DiagnosticsPayload.TryParse(vehicleId, args.ApplicationMessage.Payload, now, out var report))
        {
            // Nothing usable in the frame. Acknowledged rather than retried: a malformed diagnostics
            // payload is malformed on every redelivery, and losing one costs a battery reading.
            logger.LogDebug("No usable diagnostics in {Topic}", args.ApplicationMessage.Topic);
            return;
        }

        await repository.UpsertDiagnosticsAsync([report], cancellationToken);

        Interlocked.Increment(ref _diagnosticsApplied);
        MageRideDiagnostics.DeviceHealthUpdates.Add(1, new KeyValuePair<string, object?>("input", "diag"));
    }

    private static TimeSpan BackoffFor(int attempt) =>
        attempt <= 0 ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 5))));
}

using System.Buffers;
using MageRide.HotPath.MqttBridge.Configuration;
using MageRide.Shared.Mqtt;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MageRide.HotPath.MqttBridge.Bridging;

/// <summary>
/// One broker connection holding one shared subscription, supervised across reconnects.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>$share/</c> prefix is the whole component.</b> N replicas all subscribe
/// <c>$share/posGroup/veh/+/pos/live</c>; EMQX dispatches each message to <b>exactly one</b> of
/// them, so the bridge scales horizontally with no coordinator and no duplicate ingest. Dropping
/// the prefix would not fail — every replica would simply receive every message and
/// <c>telemetry.raw</c> would carry one copy per replica, which is why
/// <see cref="MqttTopics.SharedPositionLive"/> builds the filter and nothing here writes it by hand.
/// </para>
/// <para>
/// <b>Live and replay get a session each, not one session with two filters.</b> R-09 splits them so
/// a fleet draining its backlog after a regional outage cannot drown the samples saying where
/// vehicles are right now — and a share group on its own does not deliver that, because MQTT's
/// inflight window is per session. One session holding both filters would let 32 unacknowledged
/// backlog samples, each waiting on T-05's 20/s token, stall live delivery on the same socket for
/// as long as the wait. Two sessions have two windows, and the backlog can only ever starve itself.
/// </para>
/// <para>
/// <b>Stopping is a drain, not a disconnect.</b> UNSUBSCRIBE goes first, so EMQX stops dispatching
/// to this member while the forwards already started finish and acknowledge; only then does the
/// socket close. A payload produced but unacknowledged when a socket drops is one EMQX redispatches,
/// and <c>telemetry.raw</c> would carry it twice — which is the difference between "N replicas
/// ingest each message exactly once" surviving a rollout and surviving only an idle cluster.
/// </para>
/// </remarks>
internal sealed class MqttStreamSession(
    string stream,
    string filter,
    string clientId,
    Func<BridgedMessage, Task> onMessage,
    MqttOptions mqtt,
    MqttBridgeOptions bridge,
    MqttSessionTokenIssuer tokens,
    ILogger logger)
{
    private bool _subscribed;
    private bool _draining;

    /// <summary><c>live</c> | <c>replay</c>.</summary>
    public string Stream => stream;

    /// <summary>The filter this session holds, including its <c>$share/{group}/</c> prefix.</summary>
    public string Filter => filter;

    /// <summary>
    /// This session's MQTT client id. Unique per process and per stream: two clients presenting one
    /// id make the broker disconnect the first, which across replicas looks exactly like a flapping
    /// bridge.
    /// </summary>
    public string ClientId => clientId;

    /// <summary>True once the subscription is live, so a test can wait for readiness rather than sleep.</summary>
    public bool IsSubscribed => Volatile.Read(ref _subscribed);

    /// <summary>
    /// How this session waits for the work it already started, between UNSUBSCRIBE and DISCONNECT.
    /// Set by <see cref="MqttBridgeWorker"/>, which owns the queue and the forwarder the session
    /// hands messages to.
    /// </summary>
    public required Func<TimeSpan, Task<bool>> DrainWithinAsync { get; init; }

    /// <summary>Holds the subscription, reconnecting on R-09's jittered backoff until stopped.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                logger.LogError(ex, "MQTT bridge {Stream} session {Attempt} ended; reconnecting", stream, attempt);
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
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        // Signalled when the broker (or the network) drops us, so the supervision loop above can
        // reconnect instead of sitting on a dead socket.
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.DisconnectedAsync += args =>
        {
            logger.LogWarning(
                "MQTT bridge {Stream} client {ClientId} disconnected: {Reason}",
                stream, clientId, args.ReasonString ?? args.Reason.ToString());

            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        client.ApplicationMessageReceivedAsync += ReceiveAsync;

        var credential = tokens.IssueForService(bridge.ServiceName);

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(mqtt.Host, mqtt.Port)
            .WithClientId(clientId)
            // The bridge authenticates exactly as a device does — username plus session JWT — and
            // `acl.conf` grants the `svc-` prefix the wildcard and `$share/#` privileges E-08 needs.
            .WithCredentials(credential.Username, credential.Jwt)
            // Shared subscriptions and the reason codes that report them are MQTT 5 features.
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithKeepAlivePeriod(bridge.KeepAlive)
            .WithTimeout(bridge.ConnectTimeout)
            // Clean start with no session expiry: a bridge replica holds no state worth resuming,
            // and ending the session promptly is what makes EMQX redispatch anything this replica
            // took but never acknowledged to another member of the group.
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0);

        if (mqtt.UseTls)
        {
            options = options.WithTlsOptions(tls => tls.UseTls(true).WithTargetHost(mqtt.Host));
        }

        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCancellation.CancelAfter(bridge.ConnectTimeout);

        var result = await client.ConnectAsync(options.Build(), connectCancellation.Token);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"EMQX refused the bridge CONNECT: {result.ResultCode} ({result.ReasonString}). " +
                "Check that Mqtt:SessionTokenSecret matches EMQX_AUTHENTICATION__1__SECRET.");
        }

        await SubscribeAsync(client, stoppingToken);

        Volatile.Write(ref _subscribed, true);
        logger.LogInformation("MQTT bridge {Stream} subscribed to {Filter} as {ClientId}", stream, filter, clientId);

        // Park until the broker goes away or the host stops. The receive loop runs on MQTTnet's own
        // thread; there is nothing for this one to do but supervise.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (stoppingToken.Register(() => stopped.TrySetResult()))
        {
            await Task.WhenAny(disconnected.Task, stopped.Task);
        }

        if (stoppingToken.IsCancellationRequested && client.IsConnected)
        {
            await StopCleanlyAsync(client);
        }
    }

    /// <summary>Unsubscribe, drain, disconnect — in that order, and only that order.</summary>
    private async Task StopCleanlyAsync(IMqttClient client)
    {
        Volatile.Write(ref _draining, true);
        Volatile.Write(ref _subscribed, false);

        try
        {
            // Tells EMQX to stop routing this group's messages here. Anything already dispatched is
            // still ours to finish; anything after this goes to another replica without ever having
            // been in flight to this one.
            await client.UnsubscribeAsync(
                new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(filter).Build(), CancellationToken.None)
                .WaitAsync(bridge.ConnectTimeout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT bridge {Stream} could not unsubscribe cleanly; draining anyway", stream);
        }

        if (!await DrainWithinAsync(bridge.DrainTimeout))
        {
            logger.LogWarning(
                "MQTT bridge {Stream} disconnected with work still in flight; EMQX will redispatch it", stream);
        }

        try
        {
            // A clean DISCONNECT rather than a dropped socket, so EMQX redistributes this replica's
            // share immediately instead of waiting out the keep-alive.
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MQTT bridge {Stream} could not send a clean DISCONNECT", stream);
        }
    }

    private async Task SubscribeAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var result = await client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic => topic
                    .WithTopic(filter)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                .Build(),
            cancellationToken);

        foreach (var item in result.Items)
        {
            // A refused shared subscription is the one failure that looks like success from the
            // outside: the bridge stays connected and simply never receives anything.
            if (item.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2))
            {
                throw new InvalidOperationException(
                    $"EMQX refused the subscription to '{item.TopicFilter.Topic}': {item.ResultCode}.");
            }
        }
    }

    private async Task ReceiveAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        // Before anything can throw: MQTTnet acknowledges on return unless this is cleared first.
        args.AutoAcknowledge = false;

        var receivedAt = DateTimeOffset.UtcNow;
        var topic = args.ApplicationMessage.Topic;

        if (!MqttTopics.TryParse(topic, out var reference)
            || reference.Kind is not (MqttTopicKind.PositionLive or MqttTopicKind.PositionReplay))
        {
            // Not a position topic. Acknowledged so it does not sit in flight forever — this session
            // holds one filter and it cannot deliver anything else, so this is a misconfiguration
            // worth saying out loud.
            logger.LogWarning("Ignoring a message on an unexpected topic '{Topic}'", topic);
            await args.AcknowledgeAsync(CancellationToken.None);
            return;
        }

        if (Volatile.Read(ref _draining))
        {
            // Arrived between UNSUBSCRIBE and DISCONNECT. Left unacknowledged on purpose: EMQX still
            // holds it and hands it to a replica that is going to be alive to forward it.
            return;
        }

        // The copy happens here, on the receive loop, because MQTTnet reuses the packet buffer for
        // the next packet the moment this returns — and neither path forwards synchronously.
        var bridged = new BridgedMessage(
            args,
            topic,
            reference.VehicleId,
            stream,
            BuffersExtensions.ToArray(args.ApplicationMessage.Payload),
            receivedAt);

        await onMessage(bridged);
    }

    /// <summary>
    /// R-09's jittered exponential backoff: 1 s to 60 s with ±25 %. The same symmetric band the
    /// kernel uses for Polly — a decorrelated curve would let a fleet re-synchronise into a second
    /// thundering herd.
    /// </summary>
    private TimeSpan BackoffFor(int attempt)
    {
        if (attempt <= 0)
        {
            return bridge.ReconnectDelayMin;
        }

        var exponential = bridge.ReconnectDelayMin * Math.Pow(2, Math.Min(attempt - 1, 16));
        var capped = exponential > bridge.ReconnectDelayMax ? bridge.ReconnectDelayMax : exponential;
        var jitter = 1 + ((Random.Shared.NextDouble() - 0.5) / 2);

        return capped * jitter;
    }
}

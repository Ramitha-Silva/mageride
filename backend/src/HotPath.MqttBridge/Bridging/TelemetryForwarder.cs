using System.Diagnostics;
using System.Globalization;
using MageRide.HotPath.MqttBridge.Throttling;
using MageRide.Shared.Messaging;
using MageRide.Shared.Observability;

namespace MageRide.HotPath.MqttBridge.Bridging;

/// <summary>
/// Produces one MQTT payload to <c>telemetry.raw</c> and acknowledges it once the broker has it
/// (ADD §7.3, D6' §2.1, E-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Acknowledgement follows the produce, and nothing else.</b> MQTTnet acknowledges as soon as a
/// handler returns; that would make EMQX → Redpanda at-most-once, so a Redpanda blip would lose
/// positions the broker had already been told were safe. Here the PUBACK goes out only after the
/// delivery report names a partition and an offset. A payload that cannot be produced is <b>not</b>
/// acknowledged, and EMQX redispatches it to another member of the group when this session ends.
/// </para>
/// <para>
/// <b>The forward is started synchronously and completed asynchronously.</b>
/// <see cref="Forward"/> hands the record to librdkafka's send queue before it returns, then leaves
/// the wait for the delivery report on a continuation — so the receive loop takes the next message
/// immediately and several produces are in flight at once. Awaiting each one in turn would cap a
/// replica at one round trip per message (a few hundred samples/s against ADD §7.6's 1 200/s
/// sustained and 6 000/s burst budget). Ordering survives because the enqueue is synchronous and in
/// call order, and <see cref="KafkaOptions.EnableIdempotence"/> keeps a retry from reordering a
/// partition.
/// </para>
/// <para>
/// <b>It decodes nothing.</b> The payload crosses as opaque bytes. CBOR is what the driver app
/// sends today, but a tracker's firmware is not something this platform gets to revise, and a
/// bridge that parsed payloads would drop a sample it merely failed to understand — before anyone
/// could see it on <c>telemetry.raw</c> and find out why. Normalisation, the <c>seq</c> dedupe and
/// the anti-spoof gates are position-processor-svc's.
/// </para>
/// </remarks>
internal sealed class TelemetryForwarder(
    IEventPublisher publisher,
    PartitionOffsetLog offsets,
    PublishRateMonitor? rateMonitor,
    string bridgeId,
    ILogger logger) : IDisposable
{
    private long _forwarded;
    private long _forwardedLive;
    private long _forwardedReplay;
    private long _inFlight;

    /// <summary>Payloads this replica has produced and acknowledged, both streams.</summary>
    public long Forwarded => Interlocked.Read(ref _forwarded);

    /// <summary>Payloads forwarded off <c>veh/+/pos/live</c>.</summary>
    public long ForwardedLive => Interlocked.Read(ref _forwardedLive);

    /// <summary>Payloads forwarded off <c>veh/+/pos/replay</c>.</summary>
    public long ForwardedReplay => Interlocked.Read(ref _forwardedReplay);

    /// <summary>Forwards started but not yet acknowledged. Zero is what a clean stop waits for.</summary>
    public long InFlight => Interlocked.Read(ref _inFlight);

    /// <summary>Highest offset written per <c>telemetry.raw</c> partition.</summary>
    public IReadOnlyDictionary<int, long> PartitionOffsets => offsets.Offsets;

    /// <summary>
    /// Enqueues the payload for <c>telemetry.raw</c> and returns a task that completes once it has
    /// been acknowledged to EMQX (or has failed and deliberately not been).
    /// </summary>
    /// <remarks>
    /// Not an <c>async</c> method on purpose: everything up to the produce runs on the caller, so a
    /// caller that starts several without awaiting still enqueues them in receive order.
    /// </remarks>
    public Task Forward(BridgedMessage bridged)
    {
        var message = new EventMessage(
            EventTopics.TelemetryRaw,
            // vehicleId keys the partition, so every sample from one vehicle stays in order end to
            // end (D6' §2.1). It comes from the *topic*, never the payload: the topic is the half
            // EMQX authenticated — `acl.conf` binds a device to `veh/${username}/*` and `emqx.conf`
            // binds `${username}` to the token's `vehicleId` claim — while the payload is whatever
            // the device chose to write. Keying on the payload would let a compromised handset
            // write into another vehicle's partition.
            bridged.VehicleId.ToString(),
            bridged.Payload,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MqttBridgeWorker.TopicHeader] = bridged.Topic,
                [MqttBridgeWorker.StreamHeader] = bridged.Stream,
                [MqttBridgeWorker.ReceivedAtHeader] =
                    bridged.ReceivedAt.ToString("O", CultureInfo.InvariantCulture),
                [MqttBridgeWorker.BridgeHeader] = bridgeId,
            });

        var activity = MageRideDiagnostics.ActivitySource.StartActivity("mqtt-bridge.forward", ActivityKind.Producer);
        activity?.SetTag("mageride.vehicle_id", bridged.VehicleId);
        activity?.SetTag("mageride.mqtt.stream", bridged.Stream);
        activity?.SetTag("messaging.destination.name", EventTopics.TelemetryRaw);

        Interlocked.Increment(ref _inFlight);

        // No cancellation token. A produce already in librdkafka's queue has to be allowed to
        // finish: cancelling it on shutdown would leave the payload unacknowledged after the broker
        // had already taken it, and EMQX would then redispatch a record telemetry.raw already
        // carries. The producer's own MessageTimeoutMs is the bound. Draining is what makes a
        // planned stop cost nothing — see MqttBridgeOptions.DrainTimeout.
        var publish = publisher.PublishAsync(message);

        return CompleteAsync(publish, bridged, activity);
    }

    /// <summary>Waits until every started forward has been acknowledged, or the timeout elapses.</summary>
    /// <returns><see langword="true"/> if the bridge drained; <see langword="false"/> on timeout.</returns>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (InFlight > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                logger.LogWarning(
                    "{Bridge} still had {InFlight} forward(s) in flight after {Timeout}; EMQX will redispatch them",
                    bridgeId, InFlight, timeout);
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    public void Dispose() => offsets.Dispose();

    private async Task CompleteAsync(Task<PublishReceipt> publish, BridgedMessage bridged, Activity? activity)
    {
        var stream = bridged.Stream;

        try
        {
            var receipt = await publish;
            offsets.Record(receipt);

            activity?.SetTag("messaging.kafka.partition", receipt.Partition);
            activity?.SetTag("messaging.kafka.offset", receipt.Offset);

            await bridged.Args.AcknowledgeAsync(CancellationToken.None);

            Interlocked.Increment(ref _forwarded);

            MageRideDiagnostics.MqttBridgeForwarded.Add(1, new KeyValuePair<string, object?>("stream", stream));

            if (stream == MqttBridgeWorker.LiveStream)
            {
                Interlocked.Increment(ref _forwardedLive);

                // D-17's counter, and only for the live stream: the ceiling ADD §7.5.2 sets is on
                // `veh/+/pos/live`, and a backlog burst is what pos/replay is *for*.
                rateMonitor?.Observe(bridged.VehicleId, bridged.ReceivedAt);
            }
            else
            {
                Interlocked.Increment(ref _forwardedReplay);
            }
        }
        catch (Exception ex)
        {
            // Deliberately not acknowledged. EMQX still holds it and redispatches it to another
            // member of the group when this session ends. There is no in-process retry and no
            // `telemetry.raw.dlq` — D6' §2.3 specifies one and C039 owns it.
            MageRideDiagnostics.MqttBridgeFailures.Add(1, new KeyValuePair<string, object?>("stream", stream));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            logger.LogError(
                ex, "Could not forward {Topic} to {RawTopic}; leaving it unacknowledged",
                bridged.Topic, EventTopics.TelemetryRaw);
        }
        finally
        {
            activity?.Dispose();
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

using MQTTnet;

namespace MageRide.HotPath.MqttBridge.Bridging;

/// <summary>
/// One device payload lifted off EMQX, with everything the bridge needs to forward it later.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is a copy, made on the receive loop.</b> MQTTnet hands the handler a
/// <c>ReadOnlySequence&lt;byte&gt;</c> over its own packet buffer and reuses that buffer for the
/// next packet as soon as the handler returns. The live path produces immediately and the backlog
/// path queues for up to T-05's wait, so neither can hold the sequence — a bridge that did would
/// forward whatever arrived next under the right vehicle's key.
/// </para>
/// <para>
/// <see cref="Args"/> outlives the handler on purpose: <c>AutoAcknowledge</c> is off, so the PUBACK
/// is sent through it after the produce is confirmed, which is what keeps EMQX → Redpanda
/// at-least-once instead of at-most-once.
/// </para>
/// </remarks>
/// <param name="Args">The delivery, kept so the PUBACK can follow the produce.</param>
/// <param name="Topic">The concrete topic — a shared subscription delivers that, not the filter.</param>
/// <param name="VehicleId">Parsed out of <paramref name="Topic"/>, which is the half EMQX authenticated.</param>
/// <param name="Stream"><c>live</c> or <c>replay</c>.</param>
/// <param name="Payload">The device's bytes, copied. The bridge decodes nothing.</param>
/// <param name="ReceivedAt">When the bridge saw it — the platform's receive clock.</param>
internal readonly record struct BridgedMessage(
    MqttApplicationMessageReceivedEventArgs Args,
    string Topic,
    Guid VehicleId,
    string Stream,
    byte[] Payload,
    DateTimeOffset ReceivedAt);

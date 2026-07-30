namespace MageRide.Shared.Telemetry;

/// <summary>
/// The Kafka headers mqtt-bridge-svc stamps on every <c>telemetry.raw</c> record, and the two
/// values <see cref="Stream"/> takes.
/// </summary>
/// <remarks>
/// <para>
/// Promoted here from <c>MqttBridgeWorker</c> by C039, when position-processor-svc became the
/// second party to the contract. The producer and the consumer live in different assemblies and the
/// consumer's project does not — and should not — reference the bridge's; two copies of a header
/// name that must match exactly is the drift that shows up as "every sample looks like a replay".
/// </para>
/// <para>
/// <b><see cref="Stream"/> is the one that changes behaviour.</b> A <see cref="Replay"/> record is
/// a vehicle's own history arriving late (R-17), so the checks that compare it against the vehicle's
/// <i>current</i> state — implied speed, the monotonic GNSS clock, the R-08 presence heartbeat —
/// are meaningless for it and are skipped. The <c>seq</c> watermark (T-05) is what handles a
/// backlog, and it needs no header to do so.
/// </para>
/// </remarks>
public static class TelemetryHeaders
{
    /// <summary>Header naming the concrete MQTT topic a record came off.</summary>
    public const string Topic = "mqttTopic";

    /// <summary>Header distinguishing the live stream from the backlog: <c>live</c> | <c>replay</c>.</summary>
    public const string Stream = "stream";

    /// <summary>Header stamping when the bridge saw the payload — the platform's receive clock.</summary>
    public const string ReceivedAt = "receivedTs";

    /// <summary>Header naming the replica that forwarded it, for E-08 attribution.</summary>
    public const string Bridge = "bridge";

    /// <summary><see cref="Stream"/> value for <c>veh/+/pos/live</c>.</summary>
    public const string Live = "live";

    /// <summary><see cref="Stream"/> value for <c>veh/+/pos/replay</c>.</summary>
    public const string Replay = "replay";

    /// <summary>
    /// What a record with no <see cref="Stream"/> header is treated as.
    /// </summary>
    /// <remarks>
    /// <b>Live.</b> Every producer on the plane stamps the header, so an unstamped record is either
    /// a hand-published test payload or a producer written before C038 — and treating those as a
    /// backlog would silently switch off the plausibility gates and the presence heartbeat for
    /// them. Failing towards "check it" is the safe direction; failing towards "this is history"
    /// is not.
    /// </remarks>
    public const string DefaultStream = Live;

    /// <summary>Whether <paramref name="stream"/> names the <c>pos/replay</c> backlog.</summary>
    public static bool IsReplay(string? stream) =>
        string.Equals(stream, Replay, StringComparison.Ordinal);
}

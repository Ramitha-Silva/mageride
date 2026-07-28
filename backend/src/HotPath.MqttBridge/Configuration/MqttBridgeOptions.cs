using System.ComponentModel.DataAnnotations;

namespace MageRide.HotPath.MqttBridge.Configuration;

/// <summary>
/// What the bridge subscribes to and how it behaves when the broker goes away
/// (<c>MqttBridge</c> section).
/// </summary>
public sealed class MqttBridgeOptions
{
    public const string SectionName = "MqttBridge";

    /// <summary>
    /// Runs the bridge worker in this process. Off in tests that assert on a broker directly, so a
    /// background consumer cannot drain the subscription an assertion is watching.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// MQTT username, without the <c>svc-</c> prefix — <c>acl.conf</c> grants that prefix the
    /// wildcard and <c>$share/#</c> subscriptions E-08 needs.
    /// </summary>
    [Required]
    public string ServiceName { get; set; } = "mqtt-bridge";

    /// <summary>
    /// The E-08 shared-subscription group for live samples. Every replica naming the same group
    /// gets a share of the messages; a replica that named a different one would get its own full
    /// copy and double the ingest.
    /// </summary>
    [Required]
    public string LiveShareGroup { get; set; } = Shared.Mqtt.MqttTopics.LiveShareGroup;

    /// <summary>The parallel group for the backlog stream (R-09 keeps replay off the live path).</summary>
    [Required]
    public string ReplayShareGroup { get; set; } = Shared.Mqtt.MqttTopics.ReplayShareGroup;

    /// <summary>
    /// Consume <c>veh/+/pos/replay</c> as well as the live stream.
    /// </summary>
    /// <remarks>
    /// On by default because the topic exists and a backlog nobody consumes is a vehicle whose
    /// history is silently lost. The <b>priority</b> half of R-09 — live preempting replay 4:1 — is
    /// not implemented: it needs broker-side priority the C009 configuration does not set, and
    /// faking it here with a client-side delay would throttle replay without protecting live.
    /// Recorded as a gap in the C024 handoff.
    /// </remarks>
    public bool ConsumeReplay { get; set; } = true;

    /// <summary>
    /// MQTT client id prefix. A per-process suffix is appended, because two clients presenting the
    /// same id make the broker disconnect the first — which, across replicas, looks exactly like a
    /// flapping bridge.
    /// </summary>
    [Required]
    public string ClientIdPrefix { get; set; } = "mageride-bridge";

    /// <summary>Keep-alive on the broker connection.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for CONNECT and SUBSCRIBE.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>First reconnect delay. R-09's jittered exponential backoff starts here.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan ReconnectDelayMin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the reconnect delay (R-09: 1–60 s, ±25 % jitter).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan ReconnectDelayMax { get; set; } = TimeSpan.FromSeconds(60);
}

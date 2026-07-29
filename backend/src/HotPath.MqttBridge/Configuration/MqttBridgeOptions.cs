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
    /// history is silently lost. The backlog gets its <b>own broker session</b> as well as its own
    /// share group, so the two streams cannot share an inflight window — see
    /// <c>MqttStreamSession</c>.
    /// </remarks>
    public bool ConsumeReplay { get; set; } = true;

    /// <summary>
    /// Hold the backlog to T-05's 20 samples/s/device. Off only in a test that is measuring
    /// something else — a bridge with no throttle is the reconnect storm R-09 exists to prevent.
    /// </summary>
    public bool ThrottleReplay { get; set; } = true;

    /// <summary>
    /// Samples per second per device on <c>pos/replay</c> (T-05, ADD §7.5.2). The bucket has no
    /// burst credit: capacity equals this.
    /// </summary>
    [Range(1, 1000)]
    public int ReplaySamplesPerSecond { get; set; } = 20;

    /// <summary>
    /// Longest a single backlog sample may sit waiting for a token before the bridge sheds it.
    /// </summary>
    /// <remarks>
    /// A shed sample is <b>not</b> acknowledged, so EMQX still holds it and redispatches it when
    /// this session ends. The ceiling exists because a device that reconnects and floods for an
    /// hour would otherwise pin one lane's worth of memory for that hour.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan ReplayMaxWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Backlog samples queued per device before the bridge sheds rather than queues.
    /// </summary>
    /// <remarks>
    /// Rarely reached: EMQX stops dispatching once a session's inflight window is full of
    /// unacknowledged QoS 1 messages, so the broker is the first back-pressure. This is the second.
    /// </remarks>
    [Range(1, 100_000)]
    public int ReplayQueueDepth { get; set; } = 256;

    /// <summary>How long a device's replay lane survives with nothing in it before it is closed.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan ReplayLaneIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Watch <c>pos/live</c> for D-17's per-vehicle ceiling and raise <c>mqtt.rate_violation</c> on
    /// <c>audit.events</c>.
    /// </summary>
    public bool MonitorPublishRate { get; set; } = true;

    /// <summary>
    /// D-17's ceiling: messages per second per <c>vehicleId</c> on <c>veh/+/pos/live</c>.
    /// </summary>
    /// <remarks>
    /// Five, "because the near-geofence 1 s cadence + retries must not be falsely throttled"
    /// (ADD §7.5.2). It is a misbehaviour ceiling, not the expected rate. <b>The bridge does not
    /// enforce it</b> — enforcement is the broker's <c>messages_rate = "5/s"</c>; the bridge counts
    /// and reports, and forwards the sample either way.
    /// </remarks>
    [Range(1, 1000)]
    public int PublishCeilingPerSecond { get; set; } = 5;

    /// <summary>One <c>mqtt.rate_violation</c> per vehicle per this window, cluster-wide.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RateViolationCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the per-second publish counts are folded into the shared Redis window.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:10")]
    public TimeSpan RateFlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a stopping replica waits for forwards it already started before it disconnects.
    /// </summary>
    /// <remarks>
    /// This is the whole of "graceful rebalance with no duplicate ingest". A payload produced but
    /// not yet acknowledged when the socket closes is one EMQX redispatches to another replica, and
    /// <c>telemetry.raw</c> then carries it twice. Draining first is what makes a planned rollout
    /// cost nothing; an unplanned kill still falls back to at-least-once, which is the guarantee
    /// MQTT QoS 1 actually offers.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:02:00")]
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(15);

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

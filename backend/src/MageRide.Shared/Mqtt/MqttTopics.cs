using System.Diagnostics.CodeAnalysis;

namespace MageRide.Shared.Mqtt;

/// <summary>Which branch of the EMQX topic tree a topic belongs to.</summary>
public enum MqttTopicKind
{
    /// <summary><c>veh/{vehicleId}/pos/live</c> — the hot path. QoS 1, retain last.</summary>
    PositionLive,

    /// <summary><c>veh/{vehicleId}/pos/replay</c> — offline backlog, rate-limited, never retained (R-17).</summary>
    PositionReplay,

    /// <summary><c>veh/{vehicleId}/cmd</c> — downlink; the device subscribes to its own only.</summary>
    Command,

    /// <summary><c>veh/{vehicleId}/status</c> — <c>online</c>/<c>offline</c>, the LWT topic (R-15, T-04).</summary>
    Status,

    /// <summary><c>sys/diag/{vehicleId}</c> — device diagnostics, QoS 0.</summary>
    Diagnostics,
}

/// <summary>A parsed topic: which branch, and whose.</summary>
public sealed record MqttTopicRef(MqttTopicKind Kind, Guid VehicleId);

/// <summary>
/// The EMQX topic tree, exactly as <c>backend/contracts/realtime/mqtt-topics.md</c> §1 prints it
/// (ADD §7.2, D6' §3.1) — the server half of the KMP module's <c>MqttTopics</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The topic is not where authorisation lives.</b> EMQX binds <c>{vehicleId}</c> from the
/// device's JWT claim (<c>infra/deploy/emqx/emqx.conf</c>'s <c>verify_claims</c>) and
/// <c>acl.conf</c> writes every device rule in terms of <c>${username}</c>, so a device physically
/// cannot publish under another vehicle's topic. Building a topic for a vehicle the credential does
/// not cover produces a rejected publish and a disconnect, not a leak — which is why these builders
/// take a <see cref="Guid"/> and never a free-form string.
/// </para>
/// <para>
/// The bridge consumes through <b>shared subscriptions</b>: <c>$share/{group}/veh/+/pos/live</c>.
/// EMQX dispatches each message to exactly one member of the group, which is what lets
/// mqtt-bridge-svc scale horizontally without duplicate ingest into <c>telemetry.raw</c> (E-08).
/// </para>
/// </remarks>
public static class MqttTopics
{
    private const string VehicleRoot = "veh";
    private const string DiagRoot = "sys";

    /// <summary>
    /// The shared-subscription group mqtt-bridge-svc's live consumers join (ADD §7.3, D6' §3.3).
    /// </summary>
    public const string LiveShareGroup = "posGroup";

    /// <summary>
    /// The parallel group for the replay stream. <b>Separate on purpose</b>: R-09 splits live from
    /// replay so a fleet reconnecting after a regional outage cannot drown live samples with its
    /// backlog, and the two groups are what makes the priorities independent.
    /// </summary>
    public const string ReplayShareGroup = "posReplayGroup";

    /// <summary>Live GPS samples, <c>veh/{vehicleId}/pos/live</c>.</summary>
    public static string PositionLive(Guid vehicleId) => $"{VehicleRoot}/{vehicleId}/pos/live";

    /// <summary>Offline-buffered backlog, <c>veh/{vehicleId}/pos/replay</c>.</summary>
    public static string PositionReplay(Guid vehicleId) => $"{VehicleRoot}/{vehicleId}/pos/replay";

    /// <summary>Downlink commands, <c>veh/{vehicleId}/cmd</c> — the cadence hint arrives here (R-07).</summary>
    public static string Command(Guid vehicleId) => $"{VehicleRoot}/{vehicleId}/cmd";

    /// <summary>Presence, <c>veh/{vehicleId}/status</c> — the last-will topic.</summary>
    public static string Status(Guid vehicleId) => $"{VehicleRoot}/{vehicleId}/status";

    /// <summary>Device diagnostics, <c>sys/diag/{vehicleId}</c>.</summary>
    public static string Diagnostics(Guid vehicleId) => $"{DiagRoot}/diag/{vehicleId}";

    /// <summary>The wildcard every vehicle's live stream matches: <c>veh/+/pos/live</c>.</summary>
    public const string AllPositionsLive = "veh/+/pos/live";

    /// <summary>The wildcard every vehicle's replay stream matches: <c>veh/+/pos/replay</c>.</summary>
    public const string AllPositionsReplay = "veh/+/pos/replay";

    /// <summary>
    /// A load-balanced filter: <c>$share/{group}/{filter}</c>. Every replica in
    /// <paramref name="group"/> gets a share of the messages, none gets a copy (E-08).
    /// </summary>
    public static string Shared(string group, string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        return $"$share/{group}/{filter}";
    }

    /// <summary><c>$share/posGroup/veh/+/pos/live</c> — the bridge's live subscription.</summary>
    public static string SharedPositionLive(string group = LiveShareGroup) => Shared(group, AllPositionsLive);

    /// <summary><c>$share/posReplayGroup/veh/+/pos/replay</c> — the bridge's backlog subscription.</summary>
    public static string SharedPositionReplay(string group = ReplayShareGroup) => Shared(group, AllPositionsReplay);

    /// <summary>
    /// Reads a concrete topic back, or <see langword="false"/> when it is not one of ours.
    /// </summary>
    /// <remarks>
    /// Used on the receive side. A shared subscription delivers the <i>concrete</i> topic, not the
    /// filter, so this is how the bridge learns which vehicle a payload belongs to — the partition
    /// key for <c>telemetry.raw</c> comes from the topic, never from the payload, because the topic
    /// is the part EMQX authenticated.
    /// </remarks>
    public static bool TryParse(string? topic, [NotNullWhen(true)] out MqttTopicRef? reference)
    {
        reference = null;

        if (string.IsNullOrWhiteSpace(topic))
        {
            return false;
        }

        var parts = topic.Split('/');

        if (parts.Length == 3 && parts[0] == DiagRoot && parts[1] == "diag" && Guid.TryParse(parts[2], out var diagId))
        {
            reference = new MqttTopicRef(MqttTopicKind.Diagnostics, diagId);
            return true;
        }

        if (parts.Length < 3 || parts[0] != VehicleRoot || !Guid.TryParse(parts[1], out var vehicleId))
        {
            return false;
        }

        MqttTopicKind? kind = (parts.Length, parts[2]) switch
        {
            (4, "pos") when parts[3] == "live" => MqttTopicKind.PositionLive,
            (4, "pos") when parts[3] == "replay" => MqttTopicKind.PositionReplay,
            (3, "cmd") => MqttTopicKind.Command,
            (3, "status") => MqttTopicKind.Status,
            _ => null,
        };

        if (kind is null)
        {
            return false;
        }

        reference = new MqttTopicRef(kind.Value, vehicleId);
        return true;
    }
}

/// <summary>Presence on <c>veh/{vehicleId}/status</c> — the retained LWT payload (R-15, T-04).</summary>
public static class VehicleStatus
{
    /// <summary>Published by the device after a successful CONNECT.</summary>
    public const string Online = "online";

    /// <summary>The broker's last will, or the TCP adapter's emulation of it on half-close (T-04).</summary>
    public const string Offline = "offline";
}

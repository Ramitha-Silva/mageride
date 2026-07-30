using System.Diagnostics.Metrics;
using MageRide.Shared.Observability;

namespace MageRide.TcpAdapter.Observability;

/// <summary>
/// tcp-adapter's counters, on the platform's own meter.
/// </summary>
/// <remarks>
/// <para>
/// Declared here rather than in <c>MageRide.Shared.MageRideDiagnostics</c>, which is where every
/// other service's instruments live. The kernel is for cross-cutting code, and none of these is
/// cross-cutting: no other component publishes a socket count or a GT06 frame rejection, and adding
/// them there would put a protocol adapter's vocabulary into the assembly all twenty-odd services
/// compile against. They are created on <see cref="MageRideDiagnostics.Meter"/>, so the Prometheus
/// exporter D7' §12 configures picks them up by meter name exactly as if they had been.
/// </para>
/// <para>
/// The adapter has <b>no HTTP surface</b> and therefore no <c>/metrics</c> endpoint of its own
/// (<c>mqtt-topics.md</c> §7); these reach the collector over OTLP, which is the transport
/// <c>AddMageRideTelemetry</c> configures beside the scrape.
/// </para>
/// </remarks>
public static class AdapterDiagnostics
{
    /// <summary>Device sockets accepted, by protocol family.</summary>
    public static readonly Counter<long> SocketsAccepted = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.sockets_accepted", "{socket}", "Device sockets accepted by a protocol listener.");

    /// <summary>
    /// Sockets refused before or at authentication, tagged <c>reason</c>.
    /// </summary>
    /// <remarks>
    /// <c>reason=budget</c> is ADD §7.7.6's 10k ceiling being hit and is the one an operator scales
    /// on; the rest are <see cref="Identity.AuthOutcome"/> spellings and are what a
    /// mis-provisioned fleet looks like.
    /// </remarks>
    public static readonly Counter<long> SocketsRefused = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.sockets_refused", "{socket}", "Device sockets refused, by reason.");

    /// <summary>Open device sockets on this pod — read against <c>Adapter:MaxSockets</c>.</summary>
    public const string OpenSocketsGauge = "mageride.tracker.sockets_open";

    /// <summary>Frames that decoded, by family and frame kind.</summary>
    public static readonly Counter<long> FramesDecoded = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.frames_decoded", "{frame}", "Protocol frames decoded off a device socket.");

    /// <summary>
    /// Bytes discarded without producing a frame — a failed checksum, an unsynchronised stream.
    /// </summary>
    /// <remarks>
    /// The signal to watch is a rising ratio against <see cref="FramesDecoded"/> on one family: a
    /// device population talking a protocol the listener is not is the failure this catches, and it
    /// is otherwise completely silent.
    /// </remarks>
    public static readonly Counter<long> FramesRejected = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.frames_rejected", "{frame}", "Frames discarded as malformed or unreadable.");

    /// <summary>Samples published to EMQX, tagged <c>family</c> and <c>stream</c> (live | replay).</summary>
    public static readonly Counter<long> SamplesPublished = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.samples_published", "{sample}", "Position samples published into EMQX.");

    /// <summary>
    /// Samples the T-11 mode gate refused, tagged <c>reason</c>.
    /// </summary>
    /// <remarks>
    /// <c>reason=mode_c_offline</c> is the ordinary case and is not an error: a Mode C tracker keeps
    /// reporting while its driver is off duty and §7.7.7 says those pings never reach the map. A
    /// non-zero <c>reason=mode_unknown</c> means the registry lookup is failing and the gate is open
    /// — see <c>Adapter:PublishWhenModeUnknown</c>.
    /// </remarks>
    public static readonly Counter<long> SamplesGated = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.samples_gated", "{sample}", "Position samples refused before publication.");

    /// <summary>Retained presence publishes, tagged <c>state</c> (online | offline) — T-04.</summary>
    public static readonly Counter<long> PresencePublished = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.presence_published", "{message}", "Retained veh/{id}/status publishes.");

    /// <summary>How long the T-04 offline publish took, against <c>Adapter:OfflineWindow</c>.</summary>
    public static readonly Histogram<double> OfflineLatencyMs = MageRideDiagnostics.Meter.CreateHistogram<double>(
        "mageride.tracker.offline.latency", "ms", "Half-close to retained status=offline, in milliseconds.");

    /// <summary>Downlink commands written to a device, tagged <c>command</c> and <c>family</c>.</summary>
    public static readonly Counter<long> CommandsDelivered = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.commands_delivered", "{command}", "Downlink commands translated onto a device socket.");

    /// <summary>
    /// Commands that reached the adapter and did not reach a device, tagged <c>reason</c>.
    /// </summary>
    /// <remarks>
    /// <c>unsupported</c> is a family with no frame for the command (§7.7.5's five are not all
    /// expressible on all four protocols), <c>expired</c> is the envelope's <c>expiresAt</c> having
    /// passed, <c>no_session</c> is a vehicle whose device is not connected to this pod.
    /// </remarks>
    public static readonly Counter<long> CommandsDropped = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.commands_dropped", "{command}", "Downlink commands that did not reach a device.");

    /// <summary>T-08 clone reports sent to provisioning-svc.</summary>
    public static readonly Counter<long> ClonesReported = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.clones_reported", "{report}", "IMEIs reported as seen on two live sockets.");

    /// <summary>Sockets force-closed by a revocation signal (T-12).</summary>
    public static readonly Counter<long> RevocationClosures = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.revocation_closures", "{socket}", "Sockets closed because a credential was released.");

    /// <summary>Signal to socket close, against the 1 s ADD §7.7.3 allows.</summary>
    public static readonly Histogram<double> RevocationLatencyMs = MageRideDiagnostics.Meter.CreateHistogram<double>(
        "mageride.tracker.revocation.latency", "ms", "prov:tracker signal to socket close, in milliseconds.");

    /// <summary>Five-minute re-validations, tagged <c>outcome</c> (ADD §7.7.3).</summary>
    public static readonly Counter<long> Revalidations = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.revalidations", "{check}", "Long-socket binding re-validations, by outcome.");

    /// <summary>AL-32 ignition reports to trip-state-svc, tagged <c>outcome</c>.</summary>
    public static readonly Counter<long> IgnitionReports = MageRideDiagnostics.Meter.CreateCounter<long>(
        "mageride.tracker.ignition_reports", "{report}", "ACC transitions reported to trip-state-svc.");

    /// <summary>A tag, spelled once.</summary>
    public static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);
}

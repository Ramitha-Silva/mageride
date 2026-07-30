using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MageRide.TcpAdapter.Protocols;

namespace MageRide.TcpAdapter.Configuration;

/// <summary>
/// Everything tcp-adapter reads from configuration (<c>Adapter</c> section, D7' §4.2).
/// </summary>
/// <remarks>
/// Every knob is argued at its declaration and mirrored in <c>infra/env/.env.app.example</c>. The
/// ones that carry a spec number say which; the ones that do not say so out loud, because a
/// threshold nobody wrote down is a decision this component made and a reviewer has to be able to
/// see that.
/// </remarks>
public sealed class AdapterOptions
{
    public const string SectionName = "Adapter";

    /// <summary>
    /// Listener ports, comma-separated, <b>in protocol-family order</b>: GT06, JT/T 808, H02,
    /// generic-NMEA/UDP (D6' §4.1's table).
    /// </summary>
    /// <remarks>
    /// A CSV rather than four settings because <c>infra/env/.env.app.example</c> already ships
    /// <c>Adapter__Ports=5023,5024,5025,5026</c> and an <c>env_file</c> is a flat map —
    /// <c>Adapter__Ports__0</c> is the only way to bind an array from one, and nobody writing a
    /// deployment would guess it. Positional, so the order is part of the contract and is asserted
    /// in the test suite.
    /// </remarks>
    [Required]
    public string Ports { get; set; } = "5023,5024,5025,5026";

    /// <summary>
    /// Address the listeners bind. <c>0.0.0.0</c> in a container; a test binds the loopback.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Gates every listener. Off in the test suite, which drives sessions directly.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which protocol families this pod serves.</summary>
    /// <remarks>
    /// ADD §7.7.1 makes each adapter "an independent StatefulSet … so that protocol churn does not
    /// destabilise the others", and that is a deployment shape rather than four binaries: one image
    /// with three of these off is the same isolation with one artefact to build. The dev compose
    /// runs all four in one container (D7' §2.1's Container 9), which is why they default on.
    /// </remarks>
    public bool Gt06Enabled { get; set; } = true;

    /// <inheritdoc cref="Gt06Enabled"/>
    public bool Jt808Enabled { get; set; } = true;

    /// <inheritdoc cref="Gt06Enabled"/>
    public bool H02Enabled { get; set; } = true;

    /// <inheritdoc cref="Gt06Enabled"/>
    public bool NmeaUdpEnabled { get; set; } = true;

    /// <summary>
    /// Bare service name; the <c>svc-</c> prefix <c>acl.conf</c> grants <c>veh/#</c> to is added by
    /// <see cref="Shared.Mqtt.MqttSessionTokenIssuer.IssueForService"/>.
    /// </summary>
    /// <remarks>
    /// Must stay <c>tcp-adapter</c> unless <c>infra/scripts/slim-verify.sh</c> changes with it — that
    /// script asserts <c>svc-tcp-adapter may publish on behalf of a tracker</c> against the deployed
    /// ACL.
    /// </remarks>
    [Required]
    public string ServiceName { get; set; } = "tcp-adapter";

    /// <summary>
    /// Concurrent device sockets this pod accepts — ADD §7.7.6's "3 pods × 10k sockets each per
    /// protocol family".
    /// </summary>
    /// <remarks>
    /// The budget is per pod and shared across families, not per listener: the constraint it stands
    /// for is file descriptors and the 512 MB D7' §2.1 gives Container 9, and both are per process.
    /// A connection past the ceiling is accepted and closed immediately rather than left queued in
    /// the accept backlog, so the device's retry goes to another pod instead of waiting on this one.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int MaxSockets { get; set; } = 10_000;

    /// <summary>Listen backlog handed to the OS.</summary>
    [Range(1, 65_535)]
    public int Backlog { get; set; } = 512;

    /// <summary>
    /// How many adapter pods share the fleet, for the sticky-by-IMEI-hash check. 0 disables it.
    /// </summary>
    /// <remarks>
    /// ADD §7.7.6 sizes the plane "sticky-hash by IMEI", and the stickiness itself is the L4 load
    /// balancer's: HAProxy's <c>stick-table</c> in front of 5023-5025 is what keeps one device on
    /// one pod. This is the pod's own check that the balancer agrees with it — a device that hashes
    /// elsewhere is served anyway and counted, never refused, because a mis-shared socket is a
    /// balancer misconfiguration and dropping the device would turn it into an outage. See
    /// <see cref="Identity.ImeiShards"/>.
    /// </remarks>
    [Range(0, 4_096)]
    public int ShardCount { get; set; }

    /// <summary>This pod's shard index, 0-based. Ignored when <see cref="ShardCount"/> is 0.</summary>
    [Range(0, 4_095)]
    public int Shard { get; set; }

    /// <summary>
    /// How long a socket may say nothing before it is closed.
    /// </summary>
    /// <remarks>
    /// <b>No spec pins it.</b> A GT06 sends a heartbeat every 3 minutes by default and HAProxy's
    /// tracker frontends set <c>timeout client 4h</c>; fifteen minutes is five missed heartbeats —
    /// long enough that a device on a bad cell does not lose its session, short enough that a
    /// half-open socket (the peer vanished without a FIN) does not hold a slot in
    /// <see cref="MaxSockets"/> for hours. It is also what makes T-04's <c>offline</c> eventually
    /// fire for a device that dropped off the network without closing.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:10", "04:00:00")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often an authenticated socket re-checks its binding — ADD §7.7.3's "re-validates every 5
    /// minutes on long-lived sockets".
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan RevalidateInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The T-12 budget: how long after a revocation signal a matching socket may still be open.
    /// </summary>
    /// <remarks>
    /// ADD §7.7.3: "force-closes any matching socket within 1 s". This is not a delay — the close is
    /// started on the pub/sub callback itself — it is the deadline the test asserts against and the
    /// timeout the close is given before it is abandoned as unclean.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan RevocationCloseBudget { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The T-04 window: how long after a socket half-closes the retained <c>status=offline</c> must
    /// be published.
    /// </summary>
    /// <remarks>
    /// The publish is started as soon as the read loop sees EOF, so the window is a deadline rather
    /// than a wait. It exists because the publish is a network call to EMQX on a path where the thing
    /// that would normally be retried — the device — has already gone: past this the session gives
    /// up, counts it, and leaves the retained state stale until the broker's own view or the next
    /// connect corrects it.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan OfflineWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Publish the retained <c>status=online</c> after a successful authenticate, as an MQTT device
    /// does after CONNECT.
    /// </summary>
    /// <remarks>
    /// The pair is what makes the emulation honest: <c>veh/{vehicleId}/status</c> is retained, so a
    /// consumer subscribing later reads the last value written there. An adapter that only ever wrote
    /// <c>offline</c> would leave every tracker-equipped vehicle permanently offline from the moment
    /// of its first disconnect.
    /// </remarks>
    public bool PublishPresence { get; set; } = true;

    /// <summary>
    /// A fix older than this goes to <c>pos/replay</c> instead of <c>pos/live</c> (T-05).
    /// </summary>
    /// <remarks>
    /// <b>No spec gives a number.</b> §7.7.4 describes the case — a tracker that lost GSM coverage
    /// buffers to its flash ring and bursts the backlog on reconnect — and the GT06 and H02 frame
    /// formats carry no "this is buffered" bit, so age is the only signal available. JT/T 808 is the
    /// exception: its <c>0x0704</c> bulk-upload message *is* the backlog by definition and is routed
    /// to replay whatever its age. Sixty seconds is comfortably above any live cadence D5' §5.2
    /// allows (1 s near a geofence, 30 s idle) and below the shortest coverage gap worth calling one.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan ReplayAge { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a vehicle's mode and type are cached in process (T-11's input).</summary>
    /// <remarks>
    /// A vehicle's mode changes when an operator re-registers it, which is a human-scale event; ten
    /// minutes matches position-processor's <c>VehicleMetaTtl</c> so the two planes do not disagree
    /// about a vehicle's tier for longer than either of them caches it.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "24:00:00")]
    public TimeSpan VehicleProfileTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What the T-11 gate does when the vehicle's mode cannot be resolved at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Open, deliberately.</b> The two failure directions are not symmetric. Closed means a
    /// database blip takes every Mode A bus on the platform off the live map — and §7.7.7 makes the
    /// tracker "the authoritative and only source" for those vehicles, with no app to fall back to.
    /// Open means a Mode C vehicle whose driver is offline may appear on the map until the lookup
    /// recovers, which position-processor's freshness gate and dispatch's own availability check both
    /// still see.
    /// </para>
    /// <para>
    /// A stale cache entry is preferred to either: <see cref="Modes.VehicleProfileCache"/> keeps
    /// expired entries and serves them while the database is unreachable, so this only decides the
    /// case of a vehicle this pod has never resolved. Set false on a deployment that would rather
    /// lose Mode A telemetry than admit an offline Mode C ping.
    /// </para>
    /// </remarks>
    public bool PublishWhenModeUnknown { get; set; } = true;

    /// <summary>
    /// Refuse a device that presents no credential, on the protocols that can carry one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and that is a finding rather than a preference. ADD §7.7.3 gives raw-TCP
    /// devices a "per-device pre-shared bearer + IMEI signature", and <b>the GT06 login packet has
    /// nowhere to put one</b>: protocol <c>0x01</c> carries eight BCD bytes of terminal id and a
    /// two-byte type code, full stop. H02 and generic-NMEA are the same. JT/T 808 is the only one of
    /// the four with a field for it (<c>0x0102</c>, the terminal authentication code), so requiring a
    /// credential everywhere would refuse three of the four families outright.
    /// </para>
    /// <para>
    /// When a credential <i>is</i> presented it is always verified, whatever this is set to — see
    /// <see cref="Identity.PskCredentials"/>. This only decides what happens when one is absent.
    /// </para>
    /// </remarks>
    public bool RequireCredential { get; set; }

    /// <summary>
    /// Directory holding provisioning-svc's PSK signing key — the same <c>StepCa:RootKeyPath</c>
    /// that service is given, from which <c>secrets/psk_signing_key</c> is read.
    /// </summary>
    /// <remarks>
    /// Unset means a presented PSK token cannot be verified locally and the adapter falls back to
    /// asking <c>validate</c>, which answers the revocation question but not the forgery one. Said
    /// loudly at start-up.
    /// </remarks>
    public string? PskKeyDirectory { get; set; }

    /// <summary>Base URL of provisioning-svc, e.g. <c>http://app-services:5000</c>.</summary>
    /// <remarks>
    /// Unset means no device authenticates: an IMEI whose <c>imei:{imei}</c> cache entry is absent
    /// cannot be resolved, and there is nothing else to ask. That is the safe direction (C030's
    /// "an adapter that cannot reach validate refuses every device") and it is said at start-up
    /// because it is completely silent from the device's side.
    /// </remarks>
    public string? ProvisioningBaseUrl { get; set; }

    /// <summary>Must equal provisioning-svc's <c>Provisioning:InternalApiKey</c>.</summary>
    public string? ProvisioningInternalApiKey { get; set; }

    /// <summary>How long a <c>validate</c> call may take before the connect is refused.</summary>
    /// <remarks>
    /// Two seconds. It sits in front of a device's login packet, not a person, and a tracker retries
    /// on its own schedule — so the useful failure is a fast one that frees the socket slot.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.250", "00:00:30")]
    public TimeSpan ProvisioningTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Report an IMEI seen on two live sockets at once to
    /// <c>POST /v1/internal/trackers/{imei}/quarantine</c> (T-08).
    /// </summary>
    /// <remarks>
    /// C030's fence: "at the adapter a clone presents a copy of the genuine credential — same serial
    /// — and what tells the two apart is two live sockets holding one identity, which is the
    /// adapter's state". So this is the only component that can see that case, and provisioning-svc
    /// adjudicates it.
    /// </remarks>
    public bool ReportDuplicateSockets { get; set; } = true;

    /// <summary>Base URL of trip-state-svc for the AL-32 ignition report; unset disables it.</summary>
    /// <remarks>
    /// <c>POST /v1/internal/sessions/ignition</c> exists and has had no caller since C031 landed it
    /// ("the tracker plane decodes ACC out of a GT06/JT808 frame (tcp-adapter, C043) and had nowhere
    /// to say so"). Unset means tracker-equipped Mode A/B vehicles do not auto-start or auto-end
    /// their sessions on ignition (US-3.22/3.23), which is invisible from the device's side, so it is
    /// said at start-up.
    /// </remarks>
    public string? TripStateBaseUrl { get; set; }

    /// <summary>Must equal trip-state-svc's <c>TripState:InternalApiKey</c>.</summary>
    public string? TripStateInternalApiKey { get; set; }

    /// <inheritdoc cref="ProvisioningTimeout"/>
    [Range(typeof(TimeSpan), "00:00:00.250", "00:00:30")]
    public TimeSpan TripStateTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Subscribe <c>veh/+/cmd</c> and translate envelopes into command frames (§7.7.5).</summary>
    public bool DownlinkEnabled { get; set; } = true;

    /// <summary>Hold the EMQX connection at all. Off in the codec-only tests.</summary>
    public bool BrokerEnabled { get; set; } = true;

    /// <summary>Keep-alive on the adapter's own broker connection.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a CONNECT or a PUBLISH may take.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>R-09's jittered reconnect floor for the broker connection.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:05:00")]
    public TimeSpan ReconnectDelayMin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>R-09's jittered reconnect ceiling.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan ReconnectDelayMax { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Local time zone the JT/T 808 BCD timestamp is expressed in.
    /// </summary>
    /// <remarks>
    /// JT/T 808-2013 §8.18 writes the location report's time as BCD <c>YY-MM-DD-hh-mm-ss</c> in
    /// <b>Beijing time</b>, not UTC — the standard is a Chinese national one and says so. A grey
    /// import re-flashed for another market may or may not have been changed; this is the knob for
    /// that, and <c>+00:00</c> is what a device that was corrected wants. Getting it wrong shifts
    /// every fix by eight hours, which position-processor's clock-skew gate then refuses — loudly,
    /// which is the point.
    /// </remarks>
    public TimeSpan Jt808DeviceUtcOffset { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Largest frame the adapter will buffer before closing the socket as unsynchronised.
    /// </summary>
    /// <remarks>
    /// A JT/T 808 <c>0x0704</c> bulk upload is the biggest legitimate frame here and its body length
    /// field is ten bits — 1 023 bytes, before escaping. 8 KiB leaves room for that plus a partial
    /// second frame; past it the stream is not this protocol and the read buffer is not a place to
    /// find out.
    /// </remarks>
    [Range(256, 1_048_576)]
    public int MaxFrameBytes { get; set; } = 8 * 1024;

    /// <summary>The ports, resolved in family order and validated.</summary>
    public IReadOnlyDictionary<ProtocolFamily, int> ResolvePorts()
    {
        var order = ProtocolFamilies.All;
        var fields = Ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length != order.Count)
        {
            throw new InvalidOperationException(
                $"Adapter:Ports must carry {order.Count} comma-separated ports in family order " +
                $"({string.Join(", ", order.Select(ProtocolFamilies.Name))}); found {fields.Length} in '{Ports}'.");
        }

        var resolved = new Dictionary<ProtocolFamily, int>(order.Count);

        for (var index = 0; index < order.Count; index++)
        {
            // 0 is allowed and means "let the OS choose". The test suite asks for it on all four so it
            // can run beside a dev stack that already holds 5023-5026; a deployment never sets it,
            // because a device dials a fixed port.
            if (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 0 or > 65_535)
            {
                throw new InvalidOperationException(
                    $"Adapter:Ports entry {index} ('{fields[index]}') is not a TCP/UDP port.");
            }

            resolved[order[index]] = port;
        }

        return resolved;
    }

    /// <summary>Whether this pod serves <paramref name="family"/>.</summary>
    public bool IsEnabled(ProtocolFamily family) => family switch
    {
        ProtocolFamily.Gt06 => Gt06Enabled,
        ProtocolFamily.Jt808 => Jt808Enabled,
        ProtocolFamily.H02 => H02Enabled,
        ProtocolFamily.NmeaUdp => NmeaUdpEnabled,
        _ => false,
    };
}

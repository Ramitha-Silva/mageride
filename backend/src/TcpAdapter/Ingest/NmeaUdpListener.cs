using System.Net;
using System.Net.Sockets;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Observability;
using MageRide.TcpAdapter.Protocols;

// `System.Net.Sockets` has a ProtocolFamily of its own (the address family of a socket). This
// file needs both namespaces, so the spec's word wins by name.
using ProtocolFamily = MageRide.TcpAdapter.Protocols.ProtocolFamily;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>
/// <c>adapter-nmea-udp</c> — generic NMEA over UDP on 5026 (D6' §4.1, ADD §7.7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A datagram is not a session, and three things follow.</b> ADD §7.7.1 calls this family's adapter
/// "a Deployment for stateless UDP" rather than a StatefulSet, and that is the whole difference:
/// </para>
/// <list type="bullet">
/// <item><b>No T-04 presence.</b> There is no socket to half-close, so there is no moment at which
/// this device went away — only a gap in datagrams, which is what a consumer of the *positions* can
/// already see. Publishing a retained <c>offline</c> on a timer would be inventing an event.</item>
/// <item><b>No downlink.</b> Generic NMEA has no command grammar (see <see cref="NmeaCodec"/>), and a
/// source port a device sent from an hour ago is not an address to write to.</item>
/// <item><b>No T-08 detection.</b> "Two devices, one IMEI" is two live sockets, and there are none.
/// A cloned UDP tracker is caught at <c>bind</c> or not at all.</item>
/// </list>
/// <para>
/// <b>Authentication is cached, and that cache is the T-12 surface.</b> Resolving every datagram
/// through provisioning-svc would put an HTTP round trip on every fix a fleet of asset trackers sends;
/// resolving none of them would mean a revoked device publishes for ever. So an authorisation is held
/// for <c>Adapter:RevalidateInterval</c> — the same clock ADD §7.7.3 gives a long TCP socket — in
/// <see cref="SessionRegistry"/>, where the revocation watcher can drop it inside the one-second budget.
/// </para>
/// <para>
/// <b>5026 is published by the container, not by HAProxy.</b> HAProxy has no UDP forwarder;
/// <c>infra/docker-compose.dev.yml</c> maps the host port straight onto this listener and
/// <c>infra/deploy/haproxy.cfg</c>'s header says why.
/// </para>
/// </remarks>
public sealed class NmeaUdpListener : BackgroundService
{
    private const ProtocolFamily Family = ProtocolFamily.NmeaUdp;

    /// <summary>
    /// Largest datagram accepted. A burst of NMEA sentences from a cheap tracker is a few hundred
    /// bytes; the ceiling is <c>Adapter:MaxFrameBytes</c> and anything past it is truncated by the
    /// socket, which the checksum then rejects.
    /// </summary>
    private readonly byte[] _buffer;

    private readonly int _port;
    private readonly SessionServices _services;
    private readonly AdapterOptions _options;
    private readonly IProtocolCodec _codec;
    private readonly ILogger _logger;

    public NmeaUdpListener(int port, SessionServices services, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggers);

        _port = port;
        _services = services;
        _options = services.Options;
        _codec = services.Codecs.Create(Family);
        _logger = loggers.CreateLogger($"MageRide.TcpAdapter.{ProtocolFamilies.Name(Family)}");
        _buffer = new byte[Math.Min(_options.MaxFrameBytes, 64 * 1024)];
    }

    /// <summary>The port actually bound. Differs from the configured one when a test asks for 0.</summary>
    public int BoundPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var address = IPAddress.Parse(_options.BindAddress);
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        socket.Bind(new IPEndPoint(address, _port));
        BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;

        _logger.LogInformation(
            "{Adapter} listening on {Address}:{Port}/udp", ProtocolFamilies.Name(Family), _options.BindAddress, BoundPort);

        EndPoint peer = new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any, 0);

        while (!stoppingToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;

            try
            {
                received = await socket.ReceiveFromAsync(_buffer, SocketFlags.None, peer, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException exception)
            {
                // ICMP port-unreachable from an earlier send arrives as a receive error on some
                // platforms. Nothing to do but keep listening.
                _logger.LogDebug(exception, "{Adapter} receive failed", ProtocolFamilies.Name(Family));
                continue;
            }

            if (received.ReceivedBytes <= 0)
            {
                continue;
            }

            try
            {
                await HandleAsync(
                    _buffer.AsMemory(0, received.ReceivedBytes),
                    received.RemoteEndPoint as IPEndPoint,
                    stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One malformed datagram must not take the listener down; there is no socket to close
                // in its place.
                _logger.LogError(exception, "{Adapter} could not handle a datagram", ProtocolFamilies.Name(Family));
            }
        }
    }

    /// <summary>Decodes one datagram, authenticates its device, and publishes what it carried.</summary>
    internal async Task HandleAsync(ReadOnlyMemory<byte> datagram, IPEndPoint? peer, CancellationToken cancellationToken)
    {
        var familyTag = AdapterDiagnostics.Tag("family", ProtocolFamilies.Name(Family));

        if (!_codec.TryDecode(datagram.Span, out var frame, out _) || frame is null)
        {
            AdapterDiagnostics.FramesRejected.Add(1, familyTag, AdapterDiagnostics.Tag("reason", "malformed"));
            return;
        }

        AdapterDiagnostics.FramesDecoded.Add(
            1, familyTag, AdapterDiagnostics.Tag("kind", frame.Kind.ToString().ToLowerInvariant()));

        if (frame.Identity is null)
        {
            // A datagram with no identity region. Nothing can be done with a position that names no
            // device, and there is no session to refuse.
            AdapterDiagnostics.SocketsRefused.Add(
                1, familyTag, AdapterDiagnostics.Tag("reason", "malformedidentity"));

            return;
        }

        if (frame.Positions.Count == 0)
        {
            return;
        }

        var now = _services.Clock.GetUtcNow();
        var vehicleId = _services.Registry.RecallDatagram(frame.Identity, now);

        if (vehicleId is null)
        {
            var authorisation = await _services.Directory.AuthenticateAsync(
                frame.Identity, frame.Credential, peer?.Address, cancellationToken);

            if (!authorisation.IsAuthorised)
            {
                AdapterDiagnostics.SocketsRefused.Add(
                    1,
                    familyTag,
                    AdapterDiagnostics.Tag("reason", authorisation.Outcome.ToString().ToLowerInvariant()));

                return;
            }

            vehicleId = authorisation.VehicleId;

            _services.Registry.RememberDatagram(
                frame.Identity, authorisation.VehicleId, now + _options.RevalidateInterval);

            _logger.LogInformation(
                "{Adapter} device {Imei} authenticated as vehicle {VehicleId} from {Peer}",
                ProtocolFamilies.Name(Family), frame.Identity, authorisation.VehicleId, peer);
        }

        var verdict = await _services.Gate.EvaluateAsync(vehicleId.Value, cancellationToken);

        if (!verdict.Publishable)
        {
            return;
        }

        foreach (var fix in frame.Positions)
        {
            if (!fix.IsPublishable)
            {
                AdapterDiagnostics.FramesRejected.Add(
                    1, familyTag, AdapterDiagnostics.Tag("reason", fix.Valid ? "zero_fix" : "unpositioned"));

                continue;
            }

            var replay = TrackerSamples.IsReplay(fix, now, _options.ReplayAge);
            var sample = TrackerSamples.From(fix, vehicleId.Value, Family, verdict.Profile, now);

            await _services.Publisher.PublishSampleAsync(sample, Family, replay, cancellationToken);
        }
    }
}

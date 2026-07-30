using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MageRide.Shared.Telemetry;
using MageRide.TcpAdapter.Configuration;
using MageRide.TcpAdapter.Identity;
using MageRide.TcpAdapter.Modes;
using MageRide.TcpAdapter.Observability;
using MageRide.TcpAdapter.Protocols;
using MageRide.TcpAdapter.Publishing;

// `System.Net.Sockets` has a ProtocolFamily of its own (the address family of a socket). This
// file needs both namespaces, so the spec's word wins by name.
using ProtocolFamily = MageRide.TcpAdapter.Protocols.ProtocolFamily;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>The collaborators one session needs. One object so the listener's constructor stays readable.</summary>
/// <param name="Directory">Authentication and the T-08 clone report.</param>
/// <param name="Gate">T-11.</param>
/// <param name="Publisher">EMQX.</param>
/// <param name="Registry">Live sessions on this pod.</param>
/// <param name="Ignition">AL-32's report.</param>
/// <param name="Codecs">Per-session protocol decoders.</param>
/// <param name="Options">Configuration.</param>
/// <param name="Clock">The platform's clock.</param>
/// <param name="Loggers">Logger factory — a session logs under its own category.</param>
public sealed record SessionServices(
    ITrackerDirectory Directory,
    IModeGate Gate,
    ITrackerPublisher Publisher,
    SessionRegistry Registry,
    IIgnitionReporter Ignition,
    IProtocolCodecFactory Codecs,
    AdapterOptions Options,
    TimeProvider Clock,
    ILoggerFactory Loggers);

/// <summary>
/// One hardware tracker's socket, from the first frame to the retained <c>status=offline</c>.
/// </summary>
/// <remarks>
/// <para>
/// The order of operations is the component: <b>frame, identify, authenticate, register, announce,
/// then publish</b> — and nothing is published before the vehicle is known, because the topic is
/// derived from the binding and there is no other authorisation on this path (see
/// <see cref="EmqxLink"/>).
/// </para>
/// <para>
/// <b>The canonical sample is built by <see cref="TrackerSamples"/>, not here</b> — the same function
/// the UDP listener uses, because the mapping is the contract and two producers filling it differently
/// would be visible as a vehicle whose type depends on which port its tracker speaks. That is also
/// where the <c>seq</c>-is-the-capture-instant decision is argued.
/// </para>
/// </remarks>
public sealed class TrackerSession : ITrackerSession
{
    /// <summary>
    /// How many frames with no identity a session may send before it is closed.
    /// </summary>
    /// <remarks>
    /// H02 and NMEA carry the identity on every message, and GT06 and JT/T 808 both open with a login,
    /// so an unauthenticated socket sending frames is either a device talking the wrong protocol at
    /// the wrong port or something scanning. Three is enough slack for a device that starts with a
    /// heartbeat.
    /// </remarks>
    private const int MaxUnidentifiedFrames = 3;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SessionServices _services;
    private readonly AdapterOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[] _buffer;
    private int _length;
    private int _unidentified;
    private int _commandSerial;
    private int _closing;
    private string? _closeReason;
    private DateTimeOffset _validatedAt;
    private bool? _ignition;
    private bool _announced;

    public TrackerSession(Socket socket, ProtocolFamily family, SessionServices services)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(services);

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _services = services;
        _options = services.Options;
        _logger = services.Loggers.CreateLogger($"MageRide.TcpAdapter.{ProtocolFamilies.Name(family)}");

        Family = family;
        Codec = services.Codecs.Create(family);
        SessionId = Guid.NewGuid().ToString("N")[..12];
        Peer = (socket.RemoteEndPoint as IPEndPoint)?.ToString();
        PeerAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address;

        _buffer = new byte[Math.Min(_options.MaxFrameBytes, 64 * 1024)];
    }

    public string SessionId { get; }

    public string Imei { get; private set; } = string.Empty;

    public Guid VehicleId { get; private set; }

    public string? CredentialSerial { get; private set; }

    public ProtocolFamily Family { get; }

    public string? Peer { get; }

    public IProtocolCodec Codec { get; }

    /// <summary>The device's address, as provisioning-svc's audit trail wants it.</summary>
    public IPAddress? PeerAddress { get; }

    /// <summary>True once the device has been resolved to a vehicle.</summary>
    public bool IsAuthenticated => VehicleId != Guid.Empty;

    public ushort NextCommandSerial() => (ushort)Interlocked.Increment(ref _commandSerial);

    public async Task<bool> TryWriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_options.ConnectTimeout);

            await _stream.WriteAsync(frame, timeout.Token);
            await _stream.FlushAsync(timeout.Token);

            return true;
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException
                                             or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Closes the socket and waits for the read loop to finish its teardown.
    /// </summary>
    /// <remarks>
    /// Bounded by <c>Adapter:RevocationCloseBudget</c>, because the caller that most needs this to
    /// return is the T-12 watcher and its budget is one second (ADD §7.7.3). The wait is for the
    /// teardown — the retained <c>offline</c> and the deregistration — not for the FIN, which the
    /// shutdown below has already sent.
    /// </remarks>
    public async Task CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            await _finished.Task.WaitAsync(_options.RevocationCloseBudget).ConfigureAwait(false);
            return;
        }

        _closeReason = reason;

        try
        {
            // Shutdown first so the peer sees a FIN rather than a reset, then cancel: the read loop is
            // parked in ReadAsync and either wakes with 0 bytes or with the cancellation.
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // Already gone. The teardown below is what matters.
        }

        await _lifetime.CancelAsync();

        try
        {
            await _finished.Task.WaitAsync(_options.RevocationCloseBudget);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Session {SessionId} (IMEI {Imei}) did not finish its teardown inside {Budget}",
                SessionId, Imei, _options.RevocationCloseBudget);
        }
    }

    /// <summary>Reads until the peer goes away, the host stops, or something refuses the device.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.Token);

        try
        {
            await ReadLoopAsync(linked.Token);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            _closeReason ??= "socket error";
        }
        catch (OperationCanceledException)
        {
            _closeReason ??= stoppingToken.IsCancellationRequested ? "host stopping" : "closed";
        }
        catch (Exception exception)
        {
            // A codec that threw on attacker-supplied bytes must not take the listener down with it.
            _closeReason ??= "unhandled decode failure";
            _logger.LogError(exception, "Session {SessionId} (IMEI {Imei}) failed", SessionId, Imei);
        }
        finally
        {
            await TeardownAsync();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_length == _buffer.Length)
            {
                // The buffer is full and no codec found a frame in it, so the stream is not this
                // protocol — or a device is sending an unterminated message. Either way there is
                // nothing to be gained by growing it past Adapter:MaxFrameBytes.
                _closeReason = "unsynchronised stream";
                AdapterDiagnostics.FramesRejected.Add(1, FamilyTag, AdapterDiagnostics.Tag("reason", "overflow"));
                return;
            }

            int read;

            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(_options.IdleTimeout);

                try
                {
                    read = await _stream.ReadAsync(_buffer.AsMemory(_length), idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _closeReason = "idle";
                    return;
                }
            }

            if (read == 0)
            {
                // Half-close: the peer sent a FIN. This is the case T-04 names — indistinguishable,
                // to every consumer, from an MQTT device whose last will fired.
                _closeReason ??= "half-close";
                return;
            }

            _length += read;

            await DrainFramesAsync(cancellationToken);

            if (Volatile.Read(ref _closing) != 0)
            {
                return;
            }
        }
    }

    private async Task DrainFramesAsync(CancellationToken cancellationToken)
    {
        while (_length > 0)
        {
            var progressed = Codec.TryDecode(_buffer.AsSpan(0, _length), out var frame, out var consumed);

            if (consumed > 0)
            {
                var remaining = _length - consumed;

                if (remaining > 0)
                {
                    Buffer.BlockCopy(_buffer, consumed, _buffer, 0, remaining);
                }

                _length = remaining;
            }

            if (frame is not null)
            {
                await HandleAsync(frame, cancellationToken);
            }
            else if (progressed && consumed > 0)
            {
                // A failed checksum, an unreadable escape sequence, bytes between frames. Counted
                // rather than logged: a device on a marginal cell produces a steady trickle and the
                // ratio against FramesDecoded is what says something is actually wrong.
                AdapterDiagnostics.FramesRejected.Add(1, FamilyTag, AdapterDiagnostics.Tag("reason", "malformed"));
            }

            if (!progressed || consumed == 0 || Volatile.Read(ref _closing) != 0)
            {
                return;
            }
        }
    }

    private async Task HandleAsync(TrackerFrame frame, CancellationToken cancellationToken)
    {
        AdapterDiagnostics.FramesDecoded.Add(
            1, FamilyTag, AdapterDiagnostics.Tag("kind", frame.Kind.ToString().ToLowerInvariant()));

        if (!await ResolveIdentityAsync(frame, cancellationToken))
        {
            return;
        }

        // The protocol acknowledgement goes out only once the device is known. A GT06 whose login is
        // acknowledged starts streaming positions; acknowledging one this service is about to refuse
        // would invite exactly the traffic it refused.
        if (frame.Reply is { Length: > 0 } reply && IsAuthenticated)
        {
            await TryWriteAsync(reply, cancellationToken);
        }

        if (IsAuthenticated && frame.Ignition is { } ignition)
        {
            await ReportIgnitionAsync(ignition, frame, cancellationToken);
        }

        if (!IsAuthenticated || frame.Positions.Count == 0)
        {
            return;
        }

        await PublishAsync(frame, cancellationToken);
    }

    /// <summary>
    /// Authenticates on the first frame carrying an identity, and re-validates on the ADD §7.7.3 clock.
    /// </summary>
    /// <remarks>
    /// The re-validation is driven by traffic rather than by a timer, because a socket that is silent
    /// publishes nothing and there is nothing to gate; the sub-second half of T-12 is
    /// <see cref="RevocationWatcher"/>, and this is the backstop for a signal that was never delivered.
    /// </remarks>
    private async Task<bool> ResolveIdentityAsync(TrackerFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Identity is null)
        {
            if (IsAuthenticated)
            {
                return await RevalidateIfDueAsync(frame, cancellationToken);
            }

            if (++_unidentified < MaxUnidentifiedFrames)
            {
                return false;
            }

            _closeReason = "no identity presented";
            await CloseFromInsideAsync();
            return false;
        }

        if (IsAuthenticated)
        {
            if (!string.Equals(frame.Identity, Imei, StringComparison.Ordinal))
            {
                // One socket, two identities. Not a clone — a clone is two sockets — but nothing
                // legitimate does it, and the vehicle a sample publishes under was decided by the
                // first one.
                _logger.LogWarning(
                    "Session {SessionId} authenticated as {Imei} and then presented {Other}; closing",
                    SessionId, Imei, frame.Identity);

                _closeReason = "identity changed mid-session";
                await CloseFromInsideAsync();
                return false;
            }

            return await RevalidateIfDueAsync(frame, cancellationToken);
        }

        var authorisation = await _services.Directory.AuthenticateAsync(
            frame.Identity, frame.Credential, PeerAddress, cancellationToken);

        if (!authorisation.IsAuthorised)
        {
            var reason = authorisation.Outcome.ToString().ToLowerInvariant();

            AdapterDiagnostics.SocketsRefused.Add(1, FamilyTag, AdapterDiagnostics.Tag("reason", reason));

            _logger.LogWarning(
                "Refused a {Family} device at {Peer}: {Reason} ({Detail})",
                ProtocolFamilies.Name(Family), Peer, reason, authorisation.Detail);

            _closeReason = reason;
            await CloseFromInsideAsync();
            return false;
        }

        Imei = frame.Identity;
        VehicleId = authorisation.VehicleId;
        CredentialSerial = authorisation.CredentialSerial;
        _validatedAt = _services.Clock.GetUtcNow();

        WarnIfWrongShard();

        var displaced = _services.Registry.Register(this);

        if (displaced is not null)
        {
            // T-08's adapter half: one IMEI, two live sockets. Both stay open until provisioning-svc
            // adjudicates — closing one would destroy the evidence and might well leave the clone
            // publishing.
            await _services.Directory.ReportCloneAsync(
                Imei,
                $"IMEI presented on two live sockets: {displaced.Peer ?? "unknown"} (session " +
                $"{displaced.SessionId}) and {Peer ?? "unknown"} (session {SessionId}) on " +
                $"{ProtocolFamilies.Name(Family)}",
                cancellationToken);
        }

        _logger.LogInformation(
            "{Family} device {Imei} authenticated as vehicle {VehicleId} from {Peer} (session {SessionId}, {Via})",
            ProtocolFamilies.Name(Family), Imei, VehicleId, Peer, SessionId, authorisation.Detail);

        if (_options.PublishPresence)
        {
            _announced = await _services.Publisher.PublishPresenceAsync(VehicleId, online: true, cancellationToken);
        }

        return true;
    }

    private async Task<bool> RevalidateIfDueAsync(TrackerFrame frame, CancellationToken cancellationToken)
    {
        var now = _services.Clock.GetUtcNow();

        if (now - _validatedAt < _options.RevalidateInterval)
        {
            return true;
        }

        var authorisation = await _services.Directory.AuthenticateAsync(
            Imei, frame.Credential, PeerAddress, cancellationToken);

        _validatedAt = now;

        if (authorisation.IsAuthorised && authorisation.VehicleId == VehicleId)
        {
            AdapterDiagnostics.Revalidations.Add(1, AdapterDiagnostics.Tag("outcome", "ok"));
            return true;
        }

        AdapterDiagnostics.Revalidations.Add(
            1,
            AdapterDiagnostics.Tag(
                "outcome", authorisation.IsAuthorised ? "rebound" : authorisation.Outcome.ToString().ToLowerInvariant()));

        _logger.LogWarning(
            "IMEI {Imei} no longer resolves to vehicle {VehicleId} ({Outcome}); closing session {SessionId}",
            Imei, VehicleId, authorisation.Outcome, SessionId);

        _closeReason = "revalidation failed";
        await CloseFromInsideAsync();
        return false;
    }

    private async Task PublishAsync(TrackerFrame frame, CancellationToken cancellationToken)
    {
        var verdict = await _services.Gate.EvaluateAsync(VehicleId, cancellationToken);

        if (!verdict.Publishable)
        {
            // §7.7.7: a Mode C tracker's pings while the driver is offline "are rejected and never
            // reach the live map or dispatch". Counted by the gate, not logged per sample.
            return;
        }

        var now = _services.Clock.GetUtcNow();

        foreach (var fix in frame.Positions)
        {
            if (!fix.IsPublishable)
            {
                AdapterDiagnostics.FramesRejected.Add(
                    1, FamilyTag, AdapterDiagnostics.Tag("reason", fix.Valid ? "zero_fix" : "unpositioned"));

                continue;
            }

            var replay = TrackerSamples.IsReplay(fix, now, _options.ReplayAge);
            var sample = TrackerSamples.From(fix, VehicleId, Family, verdict.Profile, now);

            await _services.Publisher.PublishSampleAsync(sample, Family, replay, cancellationToken);
        }
    }

    private async Task ReportIgnitionAsync(bool ignition, TrackerFrame frame, CancellationToken cancellationToken)
    {
        if (_ignition == ignition)
        {
            return;
        }

        var previous = _ignition;
        _ignition = ignition;

        if (previous is null && !ignition)
        {
            // The first frame of a session reporting ACC-off is not a transition — it is the state the
            // device was already in. Reporting it would auto-end a session the dashboard started,
            // which AL-32 explicitly forbids the device from doing.
            return;
        }

        // The device's own stamp where it has one, so a burst arriving after a coverage gap does not
        // start a session at the moment it reconnected.
        var at = frame.Positions.Count > 0 ? frame.Positions[0].CapturedAt : _services.Clock.GetUtcNow();

        await _services.Ignition.ReportAsync(VehicleId, ignition, at, cancellationToken);
    }

    /// <summary>Closes from within the read loop — cancels the lifetime without waiting on itself.</summary>
    private async Task CloseFromInsideAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // Already gone.
        }

        await _lifetime.CancelAsync();
    }

    private async Task TeardownAsync()
    {
        Volatile.Write(ref _closing, 1);

        var current = IsAuthenticated && _services.Registry.IsCurrent(this);

        if (IsAuthenticated)
        {
            _services.Registry.Unregister(this);
        }

        // T-04. Only when this session is still the one holding the IMEI: a device that reconnected
        // while this socket was draining has already published `online`, and a retained `offline`
        // behind it would leave every LWT consumer believing a publishing vehicle is dark.
        if (current && _announced)
        {
            var started = Stopwatch.GetTimestamp();

            await _services.Publisher.PublishPresenceAsync(VehicleId, online: false, CancellationToken.None);

            _logger.LogInformation(
                "Session {SessionId} (IMEI {Imei}, vehicle {VehicleId}) closed after {Reason}; " +
                "retained status=offline published in {Elapsed} ms",
                SessionId,
                Imei,
                VehicleId,
                _closeReason ?? "close",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
        }
        else
        {
            _logger.LogInformation(
                "Session {SessionId} (IMEI {Imei}) closed after {Reason}", SessionId, Imei, _closeReason ?? "close");
        }

        try
        {
            _stream.Dispose();
            _socket.Dispose();
        }
        catch (Exception)
        {
            // Disposal races a concurrent close; there is nothing left to protect.
        }

        _lifetime.Dispose();
        _writeLock.Dispose();
        _buffer = [];

        _finished.TrySetResult();
    }

    private void WarnIfWrongShard()
    {
        if (_options.ShardCount <= 1)
        {
            return;
        }

        var expected = ImeiShards.ShardFor(Imei, _options.ShardCount);

        if (expected == _options.Shard)
        {
            return;
        }

        // Served anyway — see ImeiShards for why refusing would turn a balancer misconfiguration into
        // an outage. What it costs is that the downlink, the T-08 detection and the T-04 presence pair
        // for this device are now split across pods.
        _logger.LogWarning(
            "IMEI {Imei} hashes to shard {Expected} and this pod is shard {Actual}; the L4 balancer is " +
            "not sticky by device (ADD §7.7.6). Serving it regardless.",
            Imei, expected, _options.Shard);
    }

    private KeyValuePair<string, object?> FamilyTag =>
        AdapterDiagnostics.Tag("family", ProtocolFamilies.Name(Family));
}

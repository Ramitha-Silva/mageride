using System.Collections.Concurrent;
using MageRide.TcpAdapter.Protocols;

namespace MageRide.TcpAdapter.Ingest;

/// <summary>What the registry, the downlink router and the revocation watcher need of a live socket.</summary>
public interface ITrackerSession
{
    /// <summary>This process's id for the session — in every log line the socket produces.</summary>
    string SessionId { get; }

    /// <summary>The authenticated IMEI.</summary>
    string Imei { get; }

    /// <summary>The vehicle its samples publish under.</summary>
    Guid VehicleId { get; }

    /// <summary>The credential serial the device presented, when it presented one.</summary>
    string? CredentialSerial { get; }

    /// <summary>Which protocol it speaks.</summary>
    ProtocolFamily Family { get; }

    /// <summary>The peer, for the T-08 report's detail line.</summary>
    string? Peer { get; }

    /// <summary>The codec instance holding this session's protocol state.</summary>
    IProtocolCodec Codec { get; }

    /// <summary>The next serial number a downlink frame should carry.</summary>
    ushort NextCommandSerial();

    /// <summary>Writes bytes to the device. False when the socket is gone.</summary>
    Task<bool> TryWriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>Closes the socket. Idempotent, and never throws.</summary>
    Task CloseAsync(string reason);
}

/// <summary>
/// Every live device session on this pod, by IMEI and by vehicle.
/// </summary>
/// <remarks>
/// <para>
/// Three things need it and none of them can be served from anywhere else:
/// </para>
/// <list type="bullet">
/// <item><b>T-12.</b> The revocation signal names an IMEI (and credential serials) and the socket has
/// to be force-closed inside a second — so there has to be a map from IMEI to socket.</item>
/// <item><b>§7.7.5.</b> A downlink envelope names a <i>vehicle</i>, and the frame has to go out on that
/// vehicle's device socket — so there has to be a map from vehicle to socket too.</item>
/// <item><b>T-08.</b> "Two devices presenting one IMEI" is, at the adapter, two live sockets holding
/// one identity. <see cref="Register"/> returning a previous session <i>is</i> that detection.</item>
/// </list>
/// <para>
/// <b>The UDP families have no socket and are kept here anyway.</b> Generic NMEA arrives as datagrams
/// with no connection to close and no session to hold, so a short-lived authorisation is cached
/// instead — and it lives in this same class so that a revocation invalidates the TCP sockets and the
/// datagram authorisations in one place. A second store would be a second thing to remember to clear.
/// </para>
/// </remarks>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, ITrackerSession> _byImei = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, ITrackerSession>> _byVehicle = new();

    private readonly ConcurrentDictionary<string, DatagramAuthorisation> _datagrams = new(StringComparer.Ordinal);

    /// <summary>Live TCP sessions on this pod.</summary>
    public int Count => _byImei.Count;

    /// <summary>
    /// Adds a session and reports the one it displaced, if any.
    /// </summary>
    /// <remarks>
    /// The displaced session is <b>not</b> closed here. Two live sockets under one IMEI is the T-08
    /// evidence and the caller is what reports it; closing one of them first would destroy the fact
    /// and leave the surviving device — which may well be the clone — publishing.
    /// </remarks>
    public ITrackerSession? Register(ITrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _byImei.TryGetValue(session.Imei, out var previous);
        _byImei[session.Imei] = session;

        _byVehicle.GetOrAdd(session.VehicleId, _ => new ConcurrentDictionary<string, ITrackerSession>(StringComparer.Ordinal))[session.SessionId] = session;

        return ReferenceEquals(previous, session) ? null : previous;
    }

    /// <summary>
    /// Removes a session, but only if it is still the current one for its IMEI.
    /// </summary>
    /// <remarks>
    /// The guard is what stops a slow close from unregistering its own replacement: a device that
    /// reconnects while the old socket is still draining registers first, and an unconditional
    /// removal afterwards would leave the live device unreachable by the downlink and unclosable by a
    /// revocation. It is the same reason the presence publish is guarded — see
    /// <see cref="IsCurrent"/>.
    /// </remarks>
    public void Unregister(ITrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_byImei.TryGetValue(session.Imei, out var current) && ReferenceEquals(current, session))
        {
            _byImei.TryRemove(session.Imei, out _);
        }

        if (!_byVehicle.TryGetValue(session.VehicleId, out var sessions))
        {
            return;
        }

        sessions.TryRemove(session.SessionId, out _);

        if (sessions.IsEmpty)
        {
            _byVehicle.TryRemove(session.VehicleId, out _);
        }
    }

    /// <summary>
    /// Whether this session is still the one holding its IMEI.
    /// </summary>
    /// <remarks>
    /// The T-04 guard. An <c>offline</c> published by a socket that has already been replaced would
    /// overwrite the <c>online</c> the replacement just published — and the value is retained, so the
    /// vehicle would read offline until its next reconnect. Across pods this cannot be checked at all,
    /// which is one more reason stickiness is a deployment property (<see cref="Identity.ImeiShards"/>).
    /// </remarks>
    public bool IsCurrent(ITrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _byImei.TryGetValue(session.Imei, out var current) && ReferenceEquals(current, session);
    }

    /// <summary>The live session for an IMEI, or null.</summary>
    public ITrackerSession? ForImei(string imei) =>
        _byImei.TryGetValue(imei, out var session) ? session : null;

    /// <summary>Every live session publishing under a vehicle.</summary>
    /// <remarks>
    /// Plural because <c>ux_tracker_imei_active</c> is unique on the IMEI and not on the vehicle: a
    /// vehicle carrying two trackers is unusual and not forbidden, and a downlink for it goes to both
    /// rather than to whichever the map happened to hold.
    /// </remarks>
    public IReadOnlyCollection<ITrackerSession> ForVehicle(Guid vehicleId) =>
        _byVehicle.TryGetValue(vehicleId, out var sessions) ? [.. sessions.Values] : [];

    /// <summary>Every live session, for the shutdown drain.</summary>
    public IReadOnlyCollection<ITrackerSession> All() => [.. _byImei.Values];

    /// <summary>Caches a datagram device's authorisation until <paramref name="expiresAt"/>.</summary>
    public void RememberDatagram(string imei, Guid vehicleId, DateTimeOffset expiresAt) =>
        _datagrams[imei] = new DatagramAuthorisation(vehicleId, expiresAt);

    /// <summary>The cached authorisation for a datagram device, if it has not expired.</summary>
    public Guid? RecallDatagram(string imei, DateTimeOffset now)
    {
        if (!_datagrams.TryGetValue(imei, out var cached))
        {
            return null;
        }

        if (cached.ExpiresAt > now)
        {
            return cached.VehicleId;
        }

        _datagrams.TryRemove(imei, out _);
        return null;
    }

    /// <summary>Drops a datagram device's cached authorisation — the UDP half of T-12.</summary>
    public bool ForgetDatagram(string imei) => _datagrams.TryRemove(imei, out _);

    private readonly record struct DatagramAuthorisation(Guid VehicleId, DateTimeOffset ExpiresAt);
}

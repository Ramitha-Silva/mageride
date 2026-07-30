using System.Collections.Concurrent;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Which connections <b>this replica</b> holds, who they belong to, and which
/// <c>vehicle:{vehicleId}</c> and <c>ride:{rideId}</c> groups they are in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per replica, and that is what makes the control channel correct.</b> A directed send arrives
/// on every replica; each one applies it to its own connections and nobody else's, so a passenger
/// receives it exactly once however many replicas the deployment has. The same reasoning as
/// <see cref="ICellSubscriptions"/>, one level up: SignalR's own Redis backplane would deliver every
/// replica's send to every replica's clients.
/// </para>
/// <para>
/// <b>SignalR cannot answer these questions itself.</b> <c>IHubContext.Clients.User(id)</c> can
/// <em>send</em> to a user, but D-22 needs <c>RemoveFromGroupAsync</c>, which takes a connection id;
/// and the position pumps need to know which vehicles and rides are worth reading, which no
/// SignalR API exposes. Both are cheap to keep and impossible to derive.
/// </para>
/// </remarks>
public interface IHubConnections
{
    /// <summary>Records a connection and the user it authenticated as.</summary>
    void Connected(string connectionId, Guid userId);

    /// <summary>Forgets a connection and every group it was in.</summary>
    void Disconnected(string connectionId);

    /// <summary>This replica's connections belonging to <paramref name="userId"/>.</summary>
    IReadOnlyCollection<string> ConnectionsOf(Guid userId);

    /// <summary>Adds a connection to a vehicle's stream (Mode B entitlement, or the own driver).</summary>
    void JoinVehicle(string connectionId, Guid vehicleId);

    /// <summary>Removes a connection from a vehicle's stream. Immediate — a revocation has no hysteresis.</summary>
    void LeaveVehicle(string connectionId, Guid vehicleId);

    /// <summary>Adds a connection to a ride's group.</summary>
    void JoinRide(string connectionId, Guid rideId);

    /// <summary>How many vehicle streams a connection already holds, against the per-connection ceiling.</summary>
    int VehicleCountOf(string connectionId);

    /// <summary>Vehicles this replica has at least one watcher for.</summary>
    IReadOnlyCollection<Guid> WatchedVehicles { get; }

    /// <summary>Rides this replica has at least one subscriber for.</summary>
    IReadOnlyCollection<Guid> WatchedRides { get; }

    /// <summary>Whether a connection is in a vehicle's stream — asked by the tests, not the hot path.</summary>
    bool Watches(string connectionId, Guid vehicleId);
}

/// <inheritdoc cref="IHubConnections"/>
public sealed class HubConnections : IHubConnections
{
    private readonly ConcurrentDictionary<string, Guid> _users = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _byUser = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _vehiclesOf =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _ridesOf =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, int> _vehicleWatchers = new();

    private readonly ConcurrentDictionary<Guid, int> _rideWatchers = new();

    /// <summary>
    /// Held while a membership changes shape, for the same reason <see cref="CellSubscriptions"/>
    /// holds one: "the last watcher left, so stop polling" is not safe against a join landing
    /// between the test and the removal, and losing that race means a replica that silently stops
    /// reading a vehicle somebody is still watching.
    /// </summary>
    private readonly Lock _gate = new();

    public IReadOnlyCollection<Guid> WatchedVehicles => [.. _vehicleWatchers.Keys];

    public IReadOnlyCollection<Guid> WatchedRides => [.. _rideWatchers.Keys];

    public void Connected(string connectionId, Guid userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            _users[connectionId] = userId;
            _byUser.GetOrAdd(userId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
                [connectionId] = 0;
        }
    }

    public void Disconnected(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (_users.TryRemove(connectionId, out var userId)
                && _byUser.TryGetValue(userId, out var connections))
            {
                connections.TryRemove(connectionId, out _);

                if (connections.IsEmpty)
                {
                    _byUser.TryRemove(userId, out _);
                }
            }

            if (_vehiclesOf.TryRemove(connectionId, out var vehicles))
            {
                foreach (var vehicleId in vehicles.Keys)
                {
                    Release(_vehicleWatchers, vehicleId);
                }
            }

            if (_ridesOf.TryRemove(connectionId, out var rides))
            {
                foreach (var rideId in rides.Keys)
                {
                    Release(_rideWatchers, rideId);
                }
            }
        }
    }

    public IReadOnlyCollection<string> ConnectionsOf(Guid userId) =>
        _byUser.TryGetValue(userId, out var connections) ? [.. connections.Keys] : [];

    public void JoinVehicle(string connectionId, Guid vehicleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            var held = _vehiclesOf.GetOrAdd(connectionId, static _ => new ConcurrentDictionary<Guid, byte>());

            if (held.TryAdd(vehicleId, 0))
            {
                _vehicleWatchers.AddOrUpdate(vehicleId, 1, static (_, count) => count + 1);
            }
        }
    }

    public void LeaveVehicle(string connectionId, Guid vehicleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (_vehiclesOf.TryGetValue(connectionId, out var held) && held.TryRemove(vehicleId, out _))
            {
                Release(_vehicleWatchers, vehicleId);
            }
        }
    }

    public void JoinRide(string connectionId, Guid rideId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            var held = _ridesOf.GetOrAdd(connectionId, static _ => new ConcurrentDictionary<Guid, byte>());

            if (held.TryAdd(rideId, 0))
            {
                _rideWatchers.AddOrUpdate(rideId, 1, static (_, count) => count + 1);
            }
        }
    }

    public int VehicleCountOf(string connectionId) =>
        _vehiclesOf.TryGetValue(connectionId, out var held) ? held.Count : 0;

    public bool Watches(string connectionId, Guid vehicleId) =>
        _vehiclesOf.TryGetValue(connectionId, out var held) && held.ContainsKey(vehicleId);

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private static void Release(ConcurrentDictionary<Guid, int> counts, Guid id)
    {
        if (!counts.TryGetValue(id, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            counts.TryRemove(id, out _);
            return;
        }

        counts[id] = count - 1;
    }
}

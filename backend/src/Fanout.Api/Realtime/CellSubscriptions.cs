using System.Collections.Concurrent;
using MageRide.Fanout.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MageRide.Fanout.Realtime;

/// <summary>A group membership whose removal is due.</summary>
public sealed record PendingLeave(string ConnectionId, string Cell);

/// <summary>
/// Which <c>cell:{h3index}</c> groups <b>this replica</b> has members in, and where in each cell's
/// stream it has read to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per replica, on purpose, and there is no SignalR backplane.</b> D6' §5 offers a Redis
/// backplane, and it would be wrong here: every replica reads the cell streams independently and
/// pushes to its own local group members, so coverage is already complete. Adding a backplane on top
/// would broadcast each replica's send to every other replica and a passenger would receive one copy
/// of every frame per replica in the deployment. The backplane earns its place when a <i>directed</i>
/// message has to reach a connection whose replica is unknown — <c>ShareRevoked</c> (D-22),
/// <c>RideStateChanged</c>, <c>DriverPosition</c> — which is C041's work, not this slice's.
/// </para>
/// <para>
/// <b>Leaving is deferred, joining is immediate.</b> ADD §7.4 step 6 gives group churn a 30 s
/// hysteresis so a passenger walking along a cell edge does not join and leave the same six groups
/// every few seconds. A re-join inside the window simply cancels the pending removal.
/// </para>
/// </remarks>
public interface ICellSubscriptions
{
    /// <summary>Cells this replica currently has at least one member in.</summary>
    IReadOnlyCollection<string> ActiveCells { get; }

    /// <summary>Adds <paramref name="connectionId"/> to each cell and cancels any pending removal.</summary>
    void Join(string connectionId, IReadOnlyCollection<string> cells);

    /// <summary>Schedules removal for the hysteresis window rather than removing now.</summary>
    void ScheduleLeave(string connectionId, IReadOnlyCollection<string> cells, DateTimeOffset now);

    /// <summary>Removals whose window has elapsed. Applying them is the caller's job.</summary>
    IReadOnlyCollection<PendingLeave> DrainDueLeaves(DateTimeOffset now);

    /// <summary>
    /// Drops a connection from every cell at once. No hysteresis: the socket is gone, so there is no
    /// membership left to preserve and holding it would keep a replica polling for nobody.
    /// </summary>
    IReadOnlyCollection<string> Disconnect(string connectionId);

    /// <summary>Where this replica has read to in a cell's stream, or null if it has not started.</summary>
    RedisValue? PositionOf(string cell);

    /// <summary>Records the newest stream id read from a cell.</summary>
    void Advance(string cell, RedisValue position);

    /// <summary>Cells this connection currently holds — including ones pending removal.</summary>
    IReadOnlyCollection<string> CellsOf(string connectionId);

    /// <summary>
    /// Connections in a cell's group on this replica.
    /// </summary>
    /// <remarks>
    /// Not used by the pump, which sends to the group and lets SignalR do the rest. It exists
    /// because the directed sends C041 owes — <c>ShareRevoked</c>'s targeted
    /// <c>RemoveFromGroupAsync</c> in under 200 ms (D-22) — need to go from "this vehicle" to "the
    /// connections watching it", and because a test asserting on membership should ask the registry
    /// rather than trust the client's view of its own connection id.
    /// </remarks>
    IReadOnlyCollection<string> ConnectionsIn(string cell);
}

/// <inheritdoc cref="ICellSubscriptions"/>
public sealed class CellSubscriptions(IOptions<FanoutOptions> options) : ICellSubscriptions
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>cell → the connections in its group.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _members =
        new(StringComparer.Ordinal);

    /// <summary>connection → the cells it holds, so a disconnect does not scan every cell.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byConnection =
        new(StringComparer.Ordinal);

    /// <summary>(connection, cell) → when its removal becomes due.</summary>
    private readonly ConcurrentDictionary<PendingLeave, DateTimeOffset> _pending = new();

    /// <summary>cell → the last stream id this replica pushed from.</summary>
    private readonly ConcurrentDictionary<string, RedisValue> _positions = new(StringComparer.Ordinal);

    /// <summary>
    /// Held while the membership maps change shape.
    /// </summary>
    /// <remarks>
    /// The concurrent dictionaries make each individual operation safe; they do not make
    /// "the group is empty, so remove it" safe against a join landing between the test and the
    /// removal, and the cost of losing that race is a cell this replica silently stops polling
    /// while a passenger is still in it. Membership changes are a handful per connection lifetime
    /// plus one drain per batch tick, so a lock costs nothing measurable. Reads —
    /// <see cref="ActiveCells"/>, <see cref="PositionOf"/>, <see cref="Advance"/> — stay lock-free
    /// because the pump touches them on every tick.
    /// </remarks>
    private readonly Lock _gate = new();

    public IReadOnlyCollection<string> ActiveCells => [.. _members.Keys];

    public void Join(string connectionId, IReadOnlyCollection<string> cells)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(cells);

        lock (_gate)
        {
            var held = _byConnection.GetOrAdd(
                connectionId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

            foreach (var cell in cells)
            {
                _members.GetOrAdd(
                    cell, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[connectionId] = 0;
                held[cell] = 0;

                // A re-join inside the hysteresis window is the boundary-oscillation case: the
                // membership never lapsed, so the pending removal is simply cancelled.
                _pending.TryRemove(new PendingLeave(connectionId, cell), out _);
            }
        }
    }

    public void ScheduleLeave(string connectionId, IReadOnlyCollection<string> cells, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(cells);

        var dueAt = now + _options.LeaveHysteresis;

        foreach (var cell in cells)
        {
            if (_members.TryGetValue(cell, out var group) && group.ContainsKey(connectionId))
            {
                _pending[new PendingLeave(connectionId, cell)] = dueAt;
            }
        }
    }

    public IReadOnlyCollection<PendingLeave> DrainDueLeaves(DateTimeOffset now)
    {
        var due = new List<PendingLeave>();

        lock (_gate)
        {
            foreach (var (leave, dueAt) in _pending)
            {
                if (dueAt > now || !_pending.TryRemove(leave, out _))
                {
                    continue;
                }

                Remove(leave.ConnectionId, leave.Cell);
                due.Add(leave);
            }
        }

        return due;
    }

    public IReadOnlyCollection<string> Disconnect(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (!_byConnection.TryRemove(connectionId, out var held))
            {
                return [];
            }

            var cells = held.Keys.ToArray();

            foreach (var cell in cells)
            {
                _pending.TryRemove(new PendingLeave(connectionId, cell), out _);
                Remove(connectionId, cell);
            }

            return cells;
        }
    }

    public RedisValue? PositionOf(string cell) =>
        _positions.TryGetValue(cell, out var position) ? (RedisValue?)position : null;

    public void Advance(string cell, RedisValue position) => _positions[cell] = position;

    public IReadOnlyCollection<string> CellsOf(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var held) ? [.. held.Keys] : [];

    public IReadOnlyCollection<string> ConnectionsIn(string cell) =>
        _members.TryGetValue(cell, out var group) ? [.. group.Keys] : [];

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Remove(string connectionId, string cell)
    {
        if (_byConnection.TryGetValue(connectionId, out var held))
        {
            held.TryRemove(cell, out _);

            if (held.IsEmpty)
            {
                _byConnection.TryRemove(connectionId, out _);
            }
        }

        if (!_members.TryGetValue(cell, out var group))
        {
            return;
        }

        group.TryRemove(connectionId, out _);

        if (!group.IsEmpty)
        {
            return;
        }

        // The last member of a cell left. Drop both the group and the read position: this replica
        // stops polling the stream, and if someone joins the cell again it starts from the stream's
        // current end rather than replaying whatever accumulated while nobody was watching.
        _members.TryRemove(cell, out _);
        _positions.TryRemove(cell, out _);
    }
}

using MageRide.Fanout.Configuration;
using MageRide.Shared.Errors;
using MageRide.Shared.Geo;
using MageRide.Shared.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// <c>/hubs/live</c> — the passenger and driver realtime socket
/// (<c>backend/contracts/realtime/signalr-hub.md</c>, D6' §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two methods, and the other two are absent rather than stubbed.</b> The contract lists four:
/// <c>JoinGeocells</c> and <c>LeaveGeocells</c> are here; <c>SubscribeRide</c> and
/// <c>SubscribeLocRequest</c> are C041. Both of those are "rejected unless the caller is a
/// participant", and a version that joined the group without checking would be a working
/// subscription to somebody else's ride — a hole that reads, from the client, exactly like the
/// finished feature.
/// </para>
/// <para>
/// <b>No entitlement filter yet, and that is why the fence matters.</b> D-22/D-23 say public geocell
/// groups carry Mode A always, Mode B only to entitled passengers, and Mode C only while not on
/// active hire. None of that is here (C041), so this slice fans out every vehicle
/// position-processor-svc indexed. That is the documented state of the walking skeleton, not an
/// oversight — and it is why nothing in this component claims to implement D-22.
/// </para>
/// <para>
/// <b>Cells are validated as res-7 ids.</b> <c>JoinGeocells</c> takes an array off the wire and the
/// consequence of a bad value is silence, not an error: an unparseable id becomes a group name
/// nothing ever publishes to, and the passenger sees an empty map. So the resolution is checked
/// here, where it can still be answered with a <c>HubException</c> the client can act on.
/// </para>
/// </remarks>
[Authorize]
public sealed class LiveHub(
    ICellSubscriptions subscriptions,
    ICellStreamReader streams,
    IOptions<FanoutOptions> options,
    ILogger<LiveHub> logger) : Hub
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Subscribes to live vehicle frames for <paramref name="cells"/> — H3 <b>resolution 7</b> ids.
    /// </summary>
    /// <remarks>
    /// A 3 km passenger view is res-7 self + <c>ring(2)</c> = 19 cells (R-06). The client computes
    /// them; the server does not infer a view from a position, because the client is the only side
    /// that knows which view it is showing.
    /// </remarks>
    public async Task JoinGeocells(string[] cells)
    {
        var wanted = Validate(cells);

        // Where the pump resumes from is fixed HERE, before the cell becomes active — not on the
        // pump's first tick.
        //
        // Doing it on the first tick loses every position written between the join and that tick:
        // the tick would resolve the stream's current end, advance past those entries and send
        // nothing, because a batch with no frames is not a batch. With a two-second interval that is
        // a two-second hole at exactly the moment a passenger opens the map. Resolving the tail
        // first and joining second closes it — anything written after this read has an id greater
        // than the recorded position, so the pump picks it up.
        var seeds = await ResolveAsync(wanted);

        subscriptions.Join(Context.ConnectionId, wanted);

        foreach (var cell in wanted)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Contract.CellGroup(cell), Context.ConnectionAborted);
        }

        logger.LogDebug("Connection {ConnectionId} joined {Count} geocells", Context.ConnectionId, wanted.Count);

        foreach (var seed in seeds)
        {
            await Clients.Caller.SendAsync(Contract.Events.VehiclePositions, seed, Context.ConnectionAborted);
        }
    }

    /// <summary>
    /// Unsubscribes from <paramref name="cells"/>, after the 30 s boundary hysteresis.
    /// </summary>
    /// <remarks>
    /// The membership is <b>not</b> dropped now. ADD §7.4 step 6: a passenger walking along a cell
    /// edge would otherwise join and leave the same six groups every few seconds. A re-join inside
    /// the window cancels the pending removal, so the common case costs nothing at all.
    /// </remarks>
    public Task LeaveGeocells(string[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        subscriptions.ScheduleLeave(Context.ConnectionId, cells, DateTimeOffset.UtcNow);

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // No hysteresis on a disconnect: the socket is gone, so there is no membership to preserve
        // and holding one would keep this replica polling streams for nobody. SignalR removes the
        // connection from its groups itself; the registry has to be told so the pump stops.
        var dropped = subscriptions.Disconnect(Context.ConnectionId);

        if (dropped.Count > 0)
        {
            logger.LogDebug(
                "Connection {ConnectionId} dropped, releasing {Count} geocells", Context.ConnectionId, dropped.Count);
        }

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Fixes the pump's resume point for any cell this replica is not already reading, and collects
    /// the seed batches for the joining connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cell another connection already holds is <b>not</b> re-anchored: its position belongs to a
    /// read already in progress, and moving it forward would skip entries the existing subscriber
    /// has not been sent.
    /// </para>
    /// <para>
    /// The seed batches go to <see cref="IHubCallerClients.Caller"/> only — the group has already
    /// seen them. See <see cref="FanoutOptions.JoinSeedFrames"/> for why seeding exists at all and
    /// when it should go.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<IReadOnlyList<VehicleFrame>>> ResolveAsync(IReadOnlyCollection<string> cells)
    {
        var seeds = new List<IReadOnlyList<VehicleFrame>>();

        foreach (var cell in cells)
        {
            var untracked = subscriptions.PositionOf(cell) is null;

            if (!untracked && _options.JoinSeedFrames <= 0)
            {
                continue;
            }

            var batch = await streams.ReadTailAsync(cell, _options.JoinSeedFrames, Context.ConnectionAborted);

            if (batch is null)
            {
                continue;
            }

            if (untracked)
            {
                subscriptions.Advance(cell, batch.Position);
            }

            if (batch.Frames.Count > 0)
            {
                seeds.Add(batch.Frames);
            }
        }

        return seeds;
    }

    private IReadOnlyCollection<string> Validate(string[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Length == 0)
        {
            throw new HubException("JoinGeocells needs at least one cell.");
        }

        var held = subscriptions.CellsOf(Context.ConnectionId).Count;

        if (held + cells.Length > _options.MaxCellsPerConnection)
        {
            throw new HubException(
                $"A connection may hold at most {_options.MaxCellsPerConnection} geocells " +
                $"(the 3 km view is {GeoCells.PassengerViewCellCount}).");
        }

        var wanted = new List<string>(cells.Length);

        foreach (var cell in cells.Distinct(StringComparer.Ordinal))
        {
            if (!GeoCells.PassengerView.IsValidCell(cell))
            {
                // Named as a resolution problem, because that is what it almost always is: the
                // superseded "res-8 + ring(1)" figure is still in circulation and a client using it
                // would otherwise just see an empty map (R-06).
                throw new HubException(
                    $"'{cell}' is not an H3 resolution-{GeoCells.ViewResolution} cell. " +
                    "Geocell groups are res-7 only (R-06).");
            }

            wanted.Add(cell);
        }

        return wanted;
    }
}

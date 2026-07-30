using MageRide.Fanout.Configuration;
using MageRide.Fanout.Rides;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Auth;
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
/// <b>Four methods, and three kinds of group.</b> <c>JoinGeocells</c>/<c>LeaveGeocells</c> put a
/// client in the public <c>cell:{h3index}</c> groups; <c>SubscribeRide</c> in <c>ride:{rideId}</c>,
/// which is refused unless the caller is on the ride; <c>SubscribeLocRequest</c> in the booker's own
/// P-13 group. The fourth membership, <c>vehicle:{vehicleId}</c>, has no method at all — see
/// <see cref="OnConnectedAsync"/>.
/// </para>
/// <para>
/// <b>Entitlement is resolved once, at connect, and never per frame</b> (D-23). The server reads
/// <c>share:{userId}</c> and joins the caller to one group per vehicle they may watch. That is what
/// "checked on group join" means here, and it is what lets D-22's revocation be a single directed
/// <c>RemoveFromGroupAsync</c> rather than a filter that has to run against every batch.
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
    IHubConnections connections,
    ICellStreamReader streams,
    IEntitlementCache entitlements,
    IDriverVehicles driverVehicles,
    IRideProjection rides,
    IOptions<FanoutOptions> options,
    TimeProvider clock,
    ILogger<LiveHub> logger) : Hub
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Registers the connection and puts it in the vehicle streams its owner is entitled to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no <c>SubscribeVehicle</c>, deliberately.</b> Every membership of a
    /// <c>vehicle:{vehicleId}</c> group is derived from server-side state — the D-23 entitlement SET
    /// for a Mode B watcher, <c>lock:driver:{driverId}</c> for AL-31's own-vehicle home map — so
    /// there is no request a client could make that the server would not have to overrule. A method
    /// taking a vehicle id would be a method whose entire body is "ignore the argument".
    /// </para>
    /// <para>
    /// <b>AL-31 is enforced by what the server joins, not by what the client asks.</b> A driver is
    /// put in exactly one vehicle group — their own — so the driver home map cannot receive another
    /// driver's position, whatever the app does. The complementary rule, engaged Mode C vehicles
    /// staying off the public map, is US-7.16 and lives in the pump.
    /// </para>
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        var userId = RequireCaller();

        connections.Connected(Context.ConnectionId, userId);

        await SubscribeEntitledVehiclesAsync(userId);

        await base.OnConnectedAsync();
    }

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
        await AnchorAsync(wanted);

        subscriptions.Join(Context.ConnectionId, wanted);

        foreach (var cell in wanted)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Contract.CellGroup(cell), Context.ConnectionAborted);
        }

        logger.LogDebug("Connection {ConnectionId} joined {Count} geocells", Context.ConnectionId, wanted.Count);

        // D-23's "checked on group join", taken literally: a grant accepted since the socket opened
        // takes effect at the next map interaction as well as on the control channel, so the two
        // paths cover each other.
        await SubscribeEntitledVehiclesAsync(RequireCaller());
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

        subscriptions.ScheduleLeave(Context.ConnectionId, cells, clock.GetUtcNow());

        return Task.CompletedTask;
    }

    /// <summary>
    /// Joins <c>ride:{rideId}</c> — the assigned ride's live driver position and state (US-6A.12).
    /// </summary>
    /// <remarks>
    /// <b>Rejected unless the caller is a participant</b> (<c>signalr-hub.md</c> §2). The check is
    /// the whole method: a version that joined the group without it would be a working subscription
    /// to a stranger's journey, showing their driver's live position, and from the client it would
    /// look exactly like the finished feature.
    /// <para>
    /// A ride this service has never seen is refused rather than allowed. The projection is built
    /// from <c>ride.events</c> and a gap in it means fanout-svc does not know who the parties are —
    /// which is not the same as knowing the caller is one of them.
    /// </para>
    /// </remarks>
    public async Task SubscribeRide(string rideId)
    {
        var id = ParseId(rideId, nameof(rideId));
        var userId = RequireCaller();

        var ride = await rides.ReadAsync(id, Context.ConnectionAborted)
            ?? throw new HubException($"Ride '{id}' is not one this connection may subscribe to.");

        if (!ride.Includes(userId))
        {
            // Deliberately the same message as the unknown-ride case. Telling a caller that a ride
            // exists but is not theirs is a membership oracle over other people's journeys.
            throw new HubException($"Ride '{id}' is not one this connection may subscribe to.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Contract.RideGroup(id), Context.ConnectionAborted);
        connections.JoinRide(Context.ConnectionId, id);

        logger.LogDebug("Connection {ConnectionId} subscribed to ride {RideId}", Context.ConnectionId, id);
    }

    /// <summary>
    /// Joins <c>booker:{bookerId}:loc-req:{requestId}</c> — the P-13 proxy round-trip.
    /// </summary>
    /// <remarks>
    /// <b>The caller is the booker, by construction.</b> The group name is built from the token's
    /// subject rather than from anything on the wire, and ride-svc publishes to the name built from
    /// the request's own <c>bookerId</c>; a caller who is not that booker joins a group nothing will
    /// ever publish to. That is a stronger guarantee than a lookup would be, and it needs no read at
    /// all — which matters because the round-trip is issued before any ride exists and there is
    /// nothing yet for this service to have projected.
    /// </remarks>
    public async Task SubscribeLocRequest(string requestId)
    {
        var id = ParseId(requestId, nameof(requestId));
        var bookerId = RequireCaller();

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            Contract.BookerLocationRequestGroup(bookerId, id),
            Context.ConnectionAborted);

        logger.LogDebug(
            "Booker {BookerId} awaiting location request {RequestId} (P-13)", bookerId, id);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // No hysteresis on a disconnect: the socket is gone, so there is no membership to preserve
        // and holding one would keep this replica polling streams for nobody. SignalR removes the
        // connection from its groups itself; the registries have to be told so the pumps stop.
        var dropped = subscriptions.Disconnect(Context.ConnectionId);
        connections.Disconnected(Context.ConnectionId);

        if (dropped.Count > 0)
        {
            logger.LogDebug(
                "Connection {ConnectionId} dropped, releasing {Count} geocells", Context.ConnectionId, dropped.Count);
        }

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Puts the connection in one <c>vehicle:{vehicleId}</c> group per vehicle its owner may watch.
    /// </summary>
    /// <remarks>
    /// Idempotent, because it runs on connect and again on every <c>JoinGeocells</c>: SignalR's
    /// <c>AddToGroupAsync</c> is a set insert and <see cref="IHubConnections.JoinVehicle"/> counts
    /// each connection once.
    /// </remarks>
    private async Task SubscribeEntitledVehiclesAsync(Guid userId)
    {
        var wanted = new List<Guid>();

        // D-23. The SET is registry-svc's grants, projected; a miss is "no Mode B visibility", which
        // is the safe direction to fail — see IEntitlementCache.
        wanted.AddRange(await entitlements.EntitlementsOfAsync(userId, Context.ConnectionAborted));

        // AL-31. Only for a caller who actually holds the driver role: a passenger's user id can
        // never name a live vehicle, and asking Redis about it on every connect would put a wasted
        // round trip on the handshake of every passenger on the platform.
        if (Context.User?.IsInRole(MageRideRoles.Driver) == true
            && await driverVehicles.ActiveVehicleOfAsync(userId, Context.ConnectionAborted) is { } own
            && !wanted.Contains(own))
        {
            wanted.Add(own);
        }

        foreach (var vehicleId in wanted)
        {
            if (connections.VehicleCountOf(Context.ConnectionId) >= _options.MaxVehicleSubscriptions)
            {
                logger.LogWarning(
                    "Connection {ConnectionId} reached the {Ceiling}-vehicle ceiling; the rest are not subscribed",
                    Context.ConnectionId,
                    _options.MaxVehicleSubscriptions);

                return;
            }

            await Groups.AddToGroupAsync(
                Context.ConnectionId, Contract.VehicleGroup(vehicleId), Context.ConnectionAborted);

            connections.JoinVehicle(Context.ConnectionId, vehicleId);
        }
    }

    /// <summary>
    /// Fixes the pump's resume point for any cell this replica is not already reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cell another connection already holds is <b>not</b> re-anchored: its position belongs to a
    /// read already in progress, and moving it forward would skip entries the existing subscriber
    /// has not been sent.
    /// </para>
    /// <para>
    /// <b>Nothing is replayed to the joining connection any more.</b> C024 seeded the tail of each
    /// joined cell (<c>Fanout:JoinSeedFrames</c>) because <c>signalr-hub.md</c> §1.1 makes the
    /// snapshot <c>GET /v1/nearby</c>'s job and query-svc did not exist — a passenger opening the map
    /// saw nothing until each vehicle's next sample, which looks exactly like a broken map. C042
    /// landed that endpoint and this replay is retired with it: two snapshot paths is one more than
    /// the contract has, and the socket one was the weaker of them. It read only the cells the client
    /// happened to join and only the public audience, so it could never show a passenger their own
    /// engaged vehicle or their entitled Mode B van; <c>/v1/nearby</c> answers for the viewer and
    /// applies all four rules. <c>count: 0</c> below is the anchor and nothing else — the reader
    /// returns the resume id even when no frames are wanted.
    /// </para>
    /// </remarks>
    private async Task AnchorAsync(IReadOnlyCollection<string> cells)
    {
        foreach (var cell in cells)
        {
            if (subscriptions.PositionOf(cell) is not null)
            {
                continue;
            }

            var batch = await streams.ReadTailAsync(cell, count: 0, Context.ConnectionAborted);

            if (batch is not null)
            {
                subscriptions.Advance(cell, batch.Position);
            }
        }
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

    /// <summary>
    /// The caller's <c>iam.users.id</c>.
    /// </summary>
    /// <remarks>
    /// A connection that reached a hub method has already passed <c>[Authorize]</c> and the kernel's
    /// deny-by-default fallback, so a missing subject is a token this service should never have
    /// accepted rather than an unauthenticated caller — hence a throw and not a null.
    /// </remarks>
    private Guid RequireCaller() =>
        Guid.TryParse(Context.UserIdentifier, out var userId)
            ? userId
            : throw new HubException("This access token carries no usable subject.");

    private static Guid ParseId(string? value, string name) =>
        Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new HubException($"'{name}' is not a valid identifier.");
}

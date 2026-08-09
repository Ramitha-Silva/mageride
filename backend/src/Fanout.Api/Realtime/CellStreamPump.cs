using System.Collections.Concurrent;
using MageRide.Fanout.Configuration;
using MageRide.Fanout.Visibility;
using MageRide.Shared.Observability;
using MageRide.Shared.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Contract = MageRide.Shared.Realtime.LiveHub;

namespace MageRide.Fanout.Realtime;

/// <summary>
/// Drains the <c>cell:{h3index}</c> streams this replica has members in, applies the D-22/D-23
/// visibility filter, and pushes what survives to each cell's group (ADD §7.4 step 7,
/// D6' §5.2, <c>signalr-hub.md</c> §3/§4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pull, not push, and that is what makes the cost model work.</b> ADD §7.4's whole argument is
/// that fan-out must be O(updates × subscribers-per-cell) rather than O(passengers × vehicles): this
/// replica reads only the cells it actually has someone in, and sends one batch per cell however
/// many passengers are in it. A replica with no subscribers does no Redis work at all.
/// </para>
/// <para>
/// <b>The filter runs once per frame, not once per subscriber.</b> Stale, offline and on-hire are
/// facts about the vehicle and are the same for everyone in the cell, so filtering here leaves the
/// batch a batch. The one per-passenger rule — Mode B entitlement — is not applied here at all: a
/// private vehicle simply never reaches a public group, and its watchers are served through
/// <c>vehicle:{vehicleId}</c> by <see cref="VehicleStreamPump"/>.
/// </para>
/// <para>
/// <b>Every replica reads independently and pushes locally.</b> That is why there is no SignalR
/// backplane here — see <see cref="ICellSubscriptions"/>. Coverage is complete because each replica
/// covers its own connections; adding a backplane would multiply every batch by the replica count.
/// </para>
/// <para>
/// <b>Batched, never per fix.</b> One <c>VehiclePositions</c> per cell per tick carrying the newest
/// frame per vehicle. A per-fix fan-out would be five messages a second per vehicle times every
/// subscriber of its cell, which is the cost ADD §7.4 exists to avoid.
/// </para>
/// </remarks>
internal sealed class CellStreamPump(
    ICellSubscriptions subscriptions,
    ICellStreamReader streams,
    IVehicleSnapshotReader snapshots,
    IVisibilityIndex visibility,
    IHubContext<LiveHub> hub,
    IOptions<FanoutOptions> options,
    TimeProvider clock,
    ILogger<CellStreamPump> logger) : BackgroundService
{
    private readonly FanoutOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// cell → vehicle → when this replica last published it into that cell's group.
    /// </summary>
    /// <remarks>
    /// <b>US-7.17 is detected by absence, and absence needs a memory.</b> A vehicle that stops
    /// reporting produces no frames at all, so nothing in a batch says it has gone; and a client
    /// cannot infer removal from a batch that does not mention it, because batches only ever carry
    /// what moved. This is the record that lets the sweep say "this marker is stale" exactly once,
    /// to exactly the group that was shown it.
    /// </remarks>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, DateTimeOffset>> _published =
        new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var ticker = new PeriodicTimer(_options.BatchInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A Redis blip must not end the pump. The next tick re-reads from the same
                // positions, so nothing is skipped — at worst a batch arrives one interval late.
                logger.LogError(ex, "A fan-out tick failed; continuing");
            }

            try
            {
                if (!await ticker.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One drain of every active cell. Internal so a test can step it deterministically.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await ApplyDueLeavesAsync(cancellationToken);

        var now = clock.GetUtcNow();

        foreach (var cell in subscriptions.ActiveCells)
        {
            await DrainAsync(cell, now, cancellationToken);
            await SweepStaleAsync(cell, now, cancellationToken);
        }

        ForgetInactiveCells();
    }

    private async Task DrainAsync(string cell, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var batch = await streams.ReadAsync(
            cell, subscriptions.PositionOf(cell), _options.MaxEntriesPerCellPerTick, cancellationToken);

        if (batch is null)
        {
            return;
        }

        // Advanced whether or not anything is sent: an entry that produced no drawable frame — or
        // one the filter withheld — has still been read, and re-reading it every tick would pin the
        // position forever.
        if (!batch.Position.IsNull)
        {
            subscriptions.Advance(cell, batch.Position);
        }

        if (batch.Frames.Count == 0)
        {
            return;
        }

        var visible = await FilterAsync(cell, batch.Frames, now, cancellationToken);

        if (visible.Count == 0)
        {
            return;
        }

        await hub.Clients
            .Group(Contract.CellGroup(cell))
            .SendAsync(Contract.Events.VehiclePositions, visible, cancellationToken);

        MageRideDiagnostics.FanoutFramesSent.Add(visible.Count);

        if (batch.OldestEntryAt is { } written)
        {
            MageRideDiagnostics.FanoutLatencyMs.Record(Math.Max(0, (now - written).TotalMilliseconds));
        }
    }

    /// <summary>
    /// Splits a cell's frames into what the public map may show and what has to be taken off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The D-19 observation is recorded here, in the loop that already holds both of its ends.</b>
    /// ADD §13.3's position SLO is the sample's own GNSS instant to the moment its frame leaves for
    /// a subscriber, and <paramref name="now"/> is that moment — the tick's instant, captured once
    /// by <see cref="TickAsync"/> and used for the send too. Measuring from here rather than after
    /// the send keeps the return type <see cref="VehicleFrame"/>, which is what the group is
    /// actually sent: widening it to <see cref="CellFrame"/> to carry the timestamp one line further
    /// cost a projection and a second pass over every visible frame on the hot path, for a
    /// bit-identical number.
    /// </para>
    /// <para>
    /// Per frame, not per batch: a batch carries the newest frame per vehicle and those were
    /// captured at different instants, so one observation per batch would silently sample whichever
    /// vehicle happened to be last.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<VehicleFrame>> FilterAsync(
        string cell, IReadOnlyList<CellFrame> frames, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var states = await visibility.ReadAsync([.. frames.Select(frame => frame.Frame.VehicleId)], cancellationToken);
        var seen = _published.GetOrAdd(cell, static _ => new ConcurrentDictionary<Guid, DateTimeOffset>());
        var visible = new List<VehicleFrame>(frames.Count);

        foreach (var frame in frames)
        {
            var vehicleId = frame.Frame.VehicleId;
            var state = states.TryGetValue(vehicleId, out var known) ? known : VehicleState.Unknown;
            var verdict = VehicleVisibilityRules.Classify(
                frame.Frame.Mode, frame.SampleTs, state, now, _options.FreshnessWindow);

            if (verdict.Audience == VehicleAudience.Public)
            {
                visible.Add(frame.Frame);
                seen[vehicleId] = now;

                MageRideDiagnostics.RecordPositionE2E(
                    frame.SampleTs, now, MageRideDiagnostics.PositionSurfaces.Geocell);

                continue;
            }

            MageRideDiagnostics.FanoutFramesFiltered.Add(
                1, new KeyValuePair<string, object?>("reason", verdict.FilterReason));

            // A vehicle the public map was showing a moment ago and may not show now. The client is
            // told once, with the reason, and forgotten — a Mode B vehicle, which was never public,
            // is not in `seen` and so produces nothing here.
            if (verdict.RemovalReason is { } reason && seen.TryRemove(vehicleId, out _))
            {
                await SendRemovalAsync(cell, vehicleId, reason, cancellationToken);
            }
        }

        return visible;
    }

    /// <summary>US-7.17's other half: a vehicle that simply stopped sending anything.</summary>
    /// <remarks>
    /// <b>A vehicle that has left this cell is forgotten silently, not announced as stale.</b> A car
    /// driving from one of a passenger's nineteen cells into another stops appearing in the first
    /// cell's stream, which is indistinguishable here from having stopped reporting — and announcing
    /// it would tell the client to erase a marker the very next batch puts back, once every window,
    /// for every moving vehicle on the map. So a candidate is checked against its own current
    /// position first, and only a vehicle that is stale <em>everywhere</em> produces a removal.
    /// <para>
    /// A vehicle that leaves the passenger's whole view is neither stale nor offline nor engaged, so
    /// there is no reason in the contract to send — the client ages that marker out, as it must
    /// anyway for a passenger who pans the map and changes their cell set with no server event at
    /// all.
    /// </para>
    /// </remarks>
    private async Task SweepStaleAsync(string cell, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_published.TryGetValue(cell, out var seen))
        {
            return;
        }

        var overdue = seen
            .Where(entry => now - entry.Value > _options.FreshnessWindow)
            .Select(entry => entry.Key)
            .ToArray();

        if (overdue.Length == 0)
        {
            return;
        }

        var positions = await snapshots.ReadAsync(overdue, cancellationToken);

        foreach (var vehicleId in overdue)
        {
            if (!seen.TryRemove(vehicleId, out _))
            {
                continue;
            }

            if (positions.TryGetValue(vehicleId, out var snapshot)
                && snapshot.SampleTs is { } stamped
                && now - stamped <= _options.FreshnessWindow)
            {
                // Still reporting, just not here. It moved.
                continue;
            }

            MageRideDiagnostics.FanoutFramesFiltered.Add(
                1, new KeyValuePair<string, object?>("reason", VehicleRemovalReasons.Stale));

            await SendRemovalAsync(cell, vehicleId, VehicleRemovalReasons.Stale, cancellationToken);
        }
    }

    private Task SendRemovalAsync(string cell, Guid vehicleId, string reason, CancellationToken cancellationToken)
    {
        logger.LogDebug("Removing vehicle {VehicleId} from cell {Cell}: {Reason}", vehicleId, cell, reason);

        return hub.Clients
            .Group(Contract.CellGroup(cell))
            .SendAsync(
                Contract.Events.VehicleRemoved, new VehicleRemovedEvent(vehicleId, reason), cancellationToken);
    }

    /// <summary>
    /// Drops the published-vehicle memory of cells nobody is watching any more.
    /// </summary>
    /// <remarks>
    /// Without it the map grows for the lifetime of the process: a passenger who walks across a city
    /// leaves a memory behind in every cell they crossed. A cell that becomes active again starts
    /// with an empty memory, which is right — the group's membership changed too, so nothing there
    /// has been shown anything.
    /// </remarks>
    private void ForgetInactiveCells()
    {
        var active = subscriptions.ActiveCells;

        foreach (var cell in _published.Keys)
        {
            if (!active.Contains(cell))
            {
                _published.TryRemove(cell, out _);
            }
        }
    }

    /// <summary>
    /// Applies the group removals whose 30 s hysteresis window has elapsed (ADD §7.4 step 6).
    /// </summary>
    /// <remarks>
    /// Ridden on the pump's own tick rather than given a timer of its own: the window is 30 s and
    /// the tick is seconds, so the resolution is ample, and one loop is one thing to reason about
    /// when a group does not empty.
    /// </remarks>
    private async Task ApplyDueLeavesAsync(CancellationToken cancellationToken)
    {
        foreach (var leave in subscriptions.DrainDueLeaves(clock.GetUtcNow()))
        {
            await hub.Groups.RemoveFromGroupAsync(
                leave.ConnectionId, Contract.CellGroup(leave.Cell), cancellationToken);
        }
    }
}
